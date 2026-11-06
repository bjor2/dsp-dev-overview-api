using System.Net;
using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Dsp.DevOverview.Api.AppConsole;
using Dsp.DevOverview.Api.Flow;
using Dsp.DevOverview.Api.Health;
using Dsp.DevOverview.Api.Registration;
using Dsp.DevOverview.Api.ServiceBus;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dsp.DevOverview.Api.Tests;

/// <summary>ADDENDA-009: price.dev (timer/blob triggers, blobContainer entities, entities not provisioned on the namespace).</summary>
public class PriceRegistrationTests
{
    private static string PricePath => Path.Combine(RepoPaths.Root, "architecture", "registrations", "price.dev.json");
    private readonly ProjectRegistration _r = new RegistrationLoader(RepoPaths.Schema).Load(PricePath);
    private OverviewProject Project => new(_r, null, null, "no kit");

    private FlowGraph Build(IReadOnlyDictionary<string, EntityCounts>? depths = null, FlowScope scope = FlowScope.All) =>
        FlowGraphBuilder.Build(_r, scope, "LR", SbScope.Local, depths ?? new Dictionary<string, EntityCounts>(), new Dictionary<string, FlowPosition>(), []);

    [Fact]
    public void Price_loads_and_validates()
    {
        Assert.Equal(("price.dev", 2), (_r.Id, _r.SchemaVersion));
        Assert.Equal(18, _r.Handlers.Count);
        Assert.Equal(4, _r.Containers.Count);
        Assert.Empty(_r.UpgradeNotes);
        Assert.Empty(_r.LayoutWarnings);
        Assert.Equal("blobContainer", _r.FindEntity("b:pricedumpfromoptimera")!.Kind);
        Assert.False(_r.FindEntity("b:pricedumpfromoptimera")!.IsServiceBus);
        Assert.Equal("sb-bjoo-local-noe-dahl-shared.servicebus.windows.net", _r.Hosts!["developer-local-azure"].Namespace);
        Assert.False(_r.Hosts["developer-local-azure"].Provisioned);
        var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(PricePath))!.AsObject();
        Assert.Empty(RegistrationGenerator.RegistrationValidator.Check(json));
    }

    [Fact]
    public void V5_requires_a_blobContainer_trigger_edge_for_a_blob_trigger()
    {
        var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(PricePath))!.AsObject();
        var edges = json["edges"]!.AsArray();
        edges.Remove(edges.Single(e => e!["from"]!.ToString() == "b:pricedumpfromoptimera"));
        var issue = Assert.Single(RegistrationGenerator.RegistrationValidator.Check(json));
        Assert.Equal("V5_MISSING_TRIGGER_EDGE", issue.Code);
        Assert.Contains("price.fn.blob-optimera", issue.Message);
    }

    [Fact]
    public void Flow_has_blob_and_timer_handlers_and_a_blobContainer_node()
    {
        var g = Build();
        var blob = Assert.Single(g.Nodes, n => n.Kind == "blobContainer");
        Assert.Equal(("b:pricedumpfromoptimera", "pricedumpfromoptimera", null), (blob.Id, blob.Label, blob.Sb));
        var edge = Assert.Single(g.Edges, e => e.From == blob.Id);
        Assert.Equal(("price.fn.blob-optimera", "trigger"), (edge.To, edge.Kind));
        var handler = g.Nodes.Single(n => n.Id == "price.fn.blob-optimera").Handler!;
        var trigger = Assert.Single(handler.Triggers!);
        Assert.Equal(("blob", "b:pricedumpfromoptimera", "OptimeraSnapshotFileBlobTrigger"), (trigger.Kind, trigger.EntityId, trigger.Function));
        Assert.NotNull(trigger.Path);

        var reload = g.Nodes.Single(n => n.Id == "price.fn.timer-reload-m3cloud");
        Assert.Equal(6, reload.Handler!.Triggers!.Count);
        Assert.All(reload.Handler.Triggers!, t => Assert.Equal("timer", t.Kind));
        Assert.Empty(reload.Handler.TriggerEntityIds);
        Assert.DoesNotContain(g.Edges, e => e.To == reload.Id); // timers have no inbound edge
        Assert.Equal(["q:price"], g.Nodes.Single(n => n.Id == "price.fn.commands").Handler!.SelfRequeue.Select(s => s.EntityId));
        Assert.Equal(18, g.Nodes.Count(n => n.Kind == "handler"));
        Assert.Equal(4, g.Containers.Count);
    }

    [Fact]
    public void BlobContainer_never_reaches_Service_Bus()
    {
        Assert.DoesNotContain(OverviewService.ActiveEntities(Project, SbScope.Local), e => e.Kind == "blobContainer");
        Assert.DoesNotContain(OverviewService.ActiveEntities(Project, SbScope.Cloud), e => e.Kind == "blobContainer");

        // Fake reader: records every entity Service Bus would be asked about.
        var asked = new List<string>();
        var options = new OverviewOptions { ServiceBus = new ServiceBusOptions { ConnectionString = "Endpoint=sb://localhost;SharedAccessKeyName=k;SharedAccessKey=k;UseDevelopmentEmulator=true" } };
        var list = SbEntityReader.ReadAsync((_, _, _) => Task.FromResult(true),
            (e, _, _) => { lock (asked) asked.Add(e.Id); return Task.FromResult(new EntityCounts(0, 0, "fake")); },
            NullLogger.Instance, options, Project, default).GetAwaiter().GetResult();
        Assert.DoesNotContain("b:pricedumpfromoptimera", asked);
        Assert.DoesNotContain(list, s => s.EntityId.StartsWith("b:"));

        // The gateway answers a blobContainer without any Service Bus call (the fake host would fail if it tried).
        var gw = new SbGateway(new ServiceBusOptions { ConnectionString = "Endpoint=sb://nowhere.invalid/;SharedAccessKeyName=k;SharedAccessKey=dGVzdA==" }, NullLogger<SbGateway>.Instance);
        Assert.Equal(new EntityCounts(null, null, "none"), gw.GetCountsAsync(_r.FindEntity("b:pricedumpfromoptimera")!, _r, default).GetAwaiter().GetResult());
    }

    [Fact]
    public async Task Entities_not_found_are_missing_not_errors_and_health_says_not_provisioned()
    {
        var options = new OverviewOptions { ServiceBus = new ServiceBusOptions { ConnectionString = "Endpoint=sb://localhost;SharedAccessKeyName=k;SharedAccessKey=k;UseDevelopmentEmulator=true" } };
        var list = await SbEntityReader.ReadAsync((_, _, _) => Task.FromResult(true),
            (e, _, _) => Task.FromResult(e.Kind == "topic" ? new EntityCounts(null, null, "missing") : new EntityCounts(null, null, "missing")),
            NullLogger.Instance, options, Project, default);
        Assert.All(list, s => Assert.Equal(("missing", null, null, false), (s.State, s.ActiveCount, s.DeadLetterCount, s.DlqHot)));
        Assert.Null(SbEntityReader.DlqTotal(list)); // unknown, never a fake 0
        var (missing, checkedCount) = SbEntityReader.Provisioning(list);
        Assert.Equal(list.Count, missing);

        var health = new SbHealth(true, null, missing, checkedCount);
        var notice = ProjectHealthService.Notice(health)!;
        Assert.Equal(("ENTITIES_NOT_PROVISIONED", "warn"), (notice.Code, notice.Severity));
        Assert.NotEqual("red", HealthRules.Evaluate(new HealthDetail(0, 4, health.Reachable, false))); // reachable: no red banner
        Assert.Null(ProjectHealthService.Notice(new SbHealth(true, 7, Missing: 1, Checked: 7))); // customerproject: one local-only entity missing
        Assert.Null(ProjectHealthService.Notice(new SbHealth(false, null)));

        // Flow: per-entity sb.state "missing", null counts, no warning.
        var depths = _r.Entities.Where(e => e.IsServiceBus).ToDictionary(e => e.Id, _ => new EntityCounts(null, null, "missing"));
        var g = Build(depths);
        var q = g.Nodes.Single(n => n.Id == "q:price").Sb!;
        Assert.Equal(("missing", null, null, false), (q.State, q.ActiveCount, q.DeadLetterCount, q.DlqHot));
        Assert.Empty(g.Warnings);
    }

    [Fact]
    public async Task Not_found_auth_and_unreachable_are_told_apart()
    {
        var notFound = new ServiceBusException("The messaging entity could not be found.", ServiceBusFailureReason.MessagingEntityNotFound);
        Assert.True(SbCountReader.IsNotFound(notFound));
        Assert.False(SbGateway.IsAuthFailure(notFound));
        var denied = new Azure.RequestFailedException(401, "Unauthorized");
        Assert.True(SbGateway.IsAuthFailure(denied));
        Assert.False(SbCountReader.IsNotFound(denied));
        Assert.True(SbGateway.IsAuthFailure(new UnauthorizedAccessException("Put token failed. status-code: 401")));

        // Auth failure still propagates as SB_AUTH_FAILED (never a "missing" state); not-found is cached and quiet.
        var reader = new SbCountReader(new ServiceBusOptions(), NullLogger.Instance);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => reader.CountAsync("q:price|None", _ => throw new UnauthorizedAccessException("401"), default));
        Assert.False(reader.IsKnownMissing("q:price|None"));
        Assert.Null(await reader.CountAsync("q:price|None", _ => throw notFound, default));
        Assert.True(reader.IsKnownMissing("q:price|None"));

        var options = new OverviewOptions { ServiceBus = new ServiceBusOptions { ConnectionString = "Endpoint=sb://localhost;SharedAccessKeyName=k;SharedAccessKey=k;UseDevelopmentEmulator=true" } };
        var ex = await Assert.ThrowsAsync<ApiException>(() => SbEntityReader.ReadAsync((_, _, _) => Task.FromResult(true),
            (_, _, _) => throw new ApiException(503, "SB_AUTH_FAILED", "denied"), NullLogger.Instance, options, Project, default));
        Assert.Equal("SB_AUTH_FAILED", ex.Code);
    }

    [Fact]
    public void Host_namespace_mismatch_is_surfaced()
    {
        var bjoo = new ServiceBusOptions { FullyQualifiedNamespace = "sb-bjoo-local-noe-dahl-shared.servicebus.windows.net" };
        Assert.Null(FlowService.HostMismatch(Project, bjoo));
        var other = new ServiceBusOptions { FullyQualifiedNamespace = "sb-anna-local-noe-dahl-shared.servicebus.windows.net" };
        Assert.StartsWith("SB_HOST_NAMESPACE_MISMATCH", FlowService.HostMismatch(Project, other));
        var cloud = new ServiceBusOptions { FullyQualifiedNamespace = "sb-870-dev-euw-dahl-shared.servicebus.windows.net" };
        Assert.Null(FlowService.HostMismatch(Project, cloud)); // price's cloud-dev namespace is a placeholder: nothing to compare
    }

    [Fact]
    public void Customerproject_flow_is_unchanged()
    {
        var cp = new RegistrationLoader(RepoPaths.Schema).Load(RepoPaths.Registration);
        var g = FlowGraphBuilder.Build(cp, FlowScope.Local, "LR", SbScope.Local, new Dictionary<string, EntityCounts>
        {
            ["q:customerproject"] = new(1, 6, "admin"), ["q:customerproject-domain"] = new(0, 1, "admin"),
            ["q:customerproject-topic-inspect"] = new(null, null, "missing"),
        }, new Dictionary<string, FlowPosition>(), []);
        Assert.Equal((14, 15, 3), (g.Nodes.Count, g.Edges.Count, g.Containers.Count));
        Assert.Equal(["customerproject.api.http", "customerproject.fn.commands", "customerproject.fn.domain", "customerproject.fn.ingress", "customerproject.fn.snapshot"],
            g.Nodes.Where(n => n.Kind == "handler").Select(n => n.Id).Order());
        Assert.DoesNotContain(g.Nodes, n => n.Kind == "blobContainer");
        Assert.Equal(new FlowSbInfo(1, 6, true, "project", 10), g.Nodes.Single(n => n.Id == "q:customerproject").Sb);
        Assert.Equal("missing", g.Nodes.Single(n => n.Id == "q:customerproject-topic-inspect").Sb!.State); // emulator-only queue on a dev namespace
        Assert.All(g.Nodes.Where(n => n.Kind == "handler"), n => Assert.All(n.Handler!.Triggers!, t => Assert.Contains(t.Kind, new[] { "servicebus", "http" })));
        Assert.Equal(18, cp.LayoutDefaults!.Positions.Count);
        Assert.Equal(7, SbEntityReader.DlqTotal([
            new EntityStatus("q:customerproject", "queue", "x", 1, 6, true, "project", null),
            new EntityStatus("q:customerproject-domain", "queue", "x", 0, 1, true, "project", null),
            new EntityStatus("q:customerproject-topic-inspect", "queue", "x", null, null, false, "project", null, "missing"),
        ]));
    }
}

/// <summary>HTTP: both projects listed, price flow/samples/sb endpoints answer cleanly without Service Bus (Production config).</summary>
public sealed class PriceApiTests : IClassFixture<PriceApiTests.Factory>
{
    public sealed class Factory : WebApplicationFactory<Program>
    {
        public readonly string DataDir = Path.Combine(Path.GetTempPath(), "dsp-price-api-" + Guid.NewGuid().ToString("N"));

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("Overview:DataDir", DataDir);
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (Directory.Exists(DataDir)) Directory.Delete(DataDir, recursive: true);
        }
    }

    private readonly HttpClient _http;
    public PriceApiTests(Factory factory) => _http = factory.CreateClient();

    private async Task<(HttpStatusCode, JsonElement)> Get(string url)
    {
        var r = await _http.GetAsync(url);
        return (r.StatusCode, JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement);
    }

    [Fact]
    public async Task Projects_lists_both_and_price_flow_samples_and_sb_answer_cleanly()
    {
        var (status, projects) = await Get("/api/projects");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(["customerproject.dev", "price.dev"], projects.EnumerateArray().Select(p => p.GetProperty("id").GetString()).Order());
        var price = projects.EnumerateArray().Single(p => p.GetProperty("id").GetString() == "price.dev");
        Assert.Equal((4, 6), (price.GetProperty("appCount").GetInt32(), price.GetProperty("queueCount").GetInt32()));

        (status, var flow) = await Get("/api/projects/price.dev/flow?scope=all");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("stored", flow.GetProperty("layout").GetString()); // seeded layoutDefaults cover every id
        var blob = flow.GetProperty("nodes").EnumerateArray().Single(n => n.GetProperty("id").GetString() == "b:pricedumpfromoptimera");
        Assert.Equal("blobContainer", blob.GetProperty("kind").GetString());
        Assert.False(blob.TryGetProperty("sb", out _));
        Assert.Equal(new Dictionary<string, string?>
        {
            ["container:price.api"] = "app:api", ["container:price.api-batch"] = "app:api-batch",
            ["container:price.func-app"] = "app:functions", ["container:price.ingress-app"] = "app:ingress",
        }, flow.GetProperty("containers").EnumerateArray().ToDictionary(c => c.GetProperty("id").GetString()!, c => c.GetProperty("appId").GetString()));
        Assert.All(flow.GetProperty("containers").EnumerateArray(), c => Assert.Equal(JsonValueKind.Array, c.GetProperty("appIds").ValueKind));

        (status, var samples) = await Get("/api/projects/price.dev/samples");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.False(samples.GetProperty("available").GetBoolean());
        Assert.Equal(0, samples.GetProperty("presets").GetArrayLength());
        Assert.Contains("samples.catalog = none", samples.GetProperty("reason").GetString());

        (status, var err) = await Get("/api/projects/price.dev/sb/b:pricedumpfromoptimera/peek");
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("ENTITY_NOT_SERVICE_BUS", err.GetProperty("error").GetProperty("code").GetString());

        // customerproject still has its catalog.
        (status, var cpSamples) = await Get("/api/projects/customerproject.dev/samples");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(cpSamples.GetProperty("available").GetBoolean());
    }
}
