using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dsp.DevOverview.Api.Flow;
using Dsp.DevOverview.Api.Registration;
using Dsp.DevOverview.Api.ServiceBus;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Dsp.DevOverview.Api.Tests;

public class FlowGraphBuilderTests
{
    private readonly ProjectRegistration _r = new RegistrationLoader(RepoPaths.Schema).Load(RepoPaths.Registration);

    private FlowGraph Build(FlowScope scope, IReadOnlyDictionary<string, EntityCounts>? depths = null, IReadOnlyDictionary<string, FlowPosition>? positions = null) =>
        FlowGraphBuilder.Build(_r, scope, "LR", SbScope.Local, depths ?? new Dictionary<string, EntityCounts>(), positions ?? new Dictionary<string, FlowPosition>(), []);

    [Fact]
    public void Local_and_all_node_and_edge_counts()
    {
        var local = Build(FlowScope.Local);
        var all = Build(FlowScope.All);

        // 5 handlers + 8 local/both entities + ext:seed-messages; all adds the cloud-only customerproject-to-customerproject sub.
        Assert.Equal(14, local.Nodes.Count);
        Assert.Equal(15, all.Nodes.Count);
        Assert.Equal(3, local.Containers.Count);
        Assert.Equal(15, local.Edges.Count);
        Assert.Equal(17, all.Edges.Count);
        Assert.Equal(2, all.Edges.Count(e => e.Scope == "cloud"));
    }

    [Fact]
    public void Customerproject_has_five_handlers_in_three_containers_and_no_app_nodes()
    {
        var g = Build(FlowScope.All);
        Assert.Equal(2, g.SchemaVersion);
        Assert.Equal(new FlowLayoutHints(16, "LR"), g.LayoutHints);
        Assert.DoesNotContain(g.Nodes, n => n.Kind == "app" || n.Id.StartsWith("app:"));

        var handlers = g.Nodes.Where(n => n.Kind == "handler").ToDictionary(n => n.Id);
        Assert.Equal(new Dictionary<string, (string, string?)>
        {
            ["customerproject.api.http"] = ("container:customerproject.api", null),
            ["customerproject.fn.domain"] = ("container:customerproject.func-app", "CustomerProjectDomainQueueHandler"),
            ["customerproject.fn.commands"] = ("container:customerproject.func-app", "CustomerProjectQueueHandler"),
            ["customerproject.fn.ingress"] = ("container:customerproject.ingress-app", "IngressQueueHandler"),
            ["customerproject.fn.snapshot"] = ("container:customerproject.ingress-app", "SnapshotQueueHandler"),
        }, handlers.ToDictionary(kv => kv.Key, kv => (kv.Value.ContainerId!, kv.Value.Handler!.Function)));
        Assert.Equal(["INGRESS", "SNAPSHOT", "DOMAIN", "COMMANDS"],
            new[] { "ingress", "snapshot", "domain", "commands" }.Select(s => handlers[$"customerproject.fn.{s}"].Label));

        var api = g.Containers.Single(c => c.Id == "container:customerproject.api");
        Assert.Equal(("Api :5011", "resource", "app:api"), (api.Label, api.Kind, api.ResourceRefs.Single()));
        Assert.Equal(new FlowAppInfo("web", 5011, "web-870-dev-euw-dahl-customerproject", false), api.App);
        Assert.Equal(["container:customerproject.api", "container:customerproject.func-app", "container:customerproject.ingress-app"],
            g.Containers.Select(c => c.Id).Order());

        // COMMANDS re-sends to its own trigger queue: a badge (OQ-1), never an edge (I-1).
        var commands = handlers["customerproject.fn.commands"].Handler!;
        Assert.Equal(["q:customerproject"], commands.TriggerEntityIds);
        var badge = Assert.Single(commands.SelfRequeue);
        Assert.Equal("q:customerproject", badge.EntityId);
        Assert.Equal(["CreateProjectsFromErpReferences", "HandleCustomerAddressChanged", "PopulateOrderReferences", "SendNotification", "ValidateOrder"], badge.Messages);
        Assert.Contains(badge.Sources, s => s.Contains("CreateMissingProjectsHandler.cs:59"));
        Assert.DoesNotContain(g.Edges, e => e.From == "customerproject.fn.commands" && e.To == "q:customerproject");
        Assert.All(handlers.Values.Where(h => h.Id != "customerproject.fn.commands"), h => Assert.Empty(h.Handler!.SelfRequeue));

        // Edges only between handlers/entities/ext; OQ-2: no api.http → customerproject-ingress.
        Assert.All(g.Edges, e => Assert.False(e.From.StartsWith("container:") || e.To.StartsWith("container:") || e.From.StartsWith("app:") || e.To.StartsWith("app:")));
        Assert.DoesNotContain(g.Edges, e => e.From == "customerproject.api.http" && e.To == "q:customerproject-ingress");
        var domain = Assert.Single(g.Edges, e => e.From == "customerproject.fn.domain");
        Assert.Equal(("q:customerproject", "publish", "code"), (domain.To, domain.Kind, domain.Confidence));
        Assert.Contains(domain.Sources!, s => s.EndsWith("UpdateCustomerAddressFromSnapshotHandler.cs:60"));
        Assert.All(g.Edges.Where(e => e.Kind == "publish" && e.From.Contains(".fn.") || e.From.EndsWith(".http")), e => Assert.Equal("code", e.Confidence));

        // I-1 holds for every queue/subscription: no node both sends to and is triggered by it.
        foreach (var t in g.Edges.Where(e => e.Kind == "trigger"))
            Assert.DoesNotContain(g.Edges, e => e.From == t.To && e.To == t.From);
    }

#pragma warning disable CS0618 // appId is the deprecated alias under test
    [Fact]
    public void Containers_carry_appIds_from_the_registration_and_appId_as_deprecated_alias()
    {
        var g = Build(FlowScope.All);
        Assert.Equal(new Dictionary<string, (string, string?)>
        {
            ["container:customerproject.api"] = ("app:api", "app:api"),
            ["container:customerproject.func-app"] = ("app:functions", "app:functions"),
            ["container:customerproject.ingress-app"] = ("app:ingress", "app:ingress"),
        }, g.Containers.ToDictionary(c => c.Id, c => (string.Join(",", c.AppIds), c.AppId)));
    }

    [Fact]
    public void Container_appIds_fall_back_to_resourceRefs_and_appId_is_null_unless_exactly_one()
    {
        var r = new RegistrationLoader(RepoPaths.Schema).Load(RepoPaths.Registration);
        var api = r.Containers.Single(c => c.Id == "container:customerproject.api");
        var func = r.Containers.Single(c => c.Id == "container:customerproject.func-app");
        var ingress = r.Containers.Single(c => c.Id == "container:customerproject.ingress-app");
        api.AppIds = null;                                  // absent: fall back to resourceRefs
        func.AppIds = ["app:functions", "app:ingress"];     // multi-app container
        ingress.AppIds = [];                                // explicitly none
        var g = FlowGraphBuilder.Build(r, FlowScope.All, "LR", SbScope.Local, new Dictionary<string, EntityCounts>(), new Dictionary<string, FlowPosition>(), []);
        var byId = g.Containers.ToDictionary(c => c.Id);

        string? Show(string id) => string.Join(",", byId[id].AppIds) + "|" + byId[id].AppId;
        Assert.Equal("app:api|app:api", Show(api.Id));
        Assert.Equal("app:functions,app:ingress|", Show(func.Id));
        Assert.Equal("|", Show(ingress.Id));
    }

    [Fact]
    public void Container_appIds_and_appId_are_always_serialized()
    {
        var web = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var none = JsonSerializer.Serialize(new FlowContainer("container:x", "X", "logical", [], null, "both", null), web);
        Assert.Contains("\"appIds\":[]", none);
        Assert.Contains("\"appId\":null", none);
        var one = JsonSerializer.Serialize(new FlowContainer("container:y", "Y", "resource", ["app:api"], null, "both", null, AppIds: ["app:api"]), web);
        Assert.Contains("\"appIds\":[\"app:api\"]", one);
        Assert.Contains("\"appId\":\"app:api\"", one);
        var two = JsonSerializer.Serialize(new FlowContainer("container:z", "Z", "logical", [], null, "both", null, AppIds: ["app:a", "app:b"]), web);
        Assert.Contains("\"appId\":null", two);
        // `with` (positions) keeps appIds.
        Assert.Equal(["app:api"], (new FlowContainer("container:y", "Y", "resource", [], null, "both", null, AppIds: ["app:api"]) with { Position = new FlowPosition(1, 2) }).AppIds);
    }
#pragma warning restore CS0618

    [Fact]
    public void Local_scope_filters_cloud_edges_and_nodes()
    {
        var local = Build(FlowScope.Local);
        Assert.DoesNotContain(local.Edges, e => e.Scope == "cloud");
        Assert.DoesNotContain(local.Nodes, n => n.Scope == "cloud");
        Assert.DoesNotContain(local.Nodes, n => n.Id == "s:customerproject-topic/customerproject-to-customerproject");

        var all = Build(FlowScope.All);
        var forward = Assert.Single(all.Edges, e => e is { Kind: "forward", Scope: "cloud" });
        Assert.Equal(("s:customerproject-topic/customerproject-to-customerproject", "q:customerproject", "CustomerProjectOrderReferenceChanged", "terraform"),
            (forward.From, forward.To, forward.Label, forward.Confidence));
        // Edge ids follow registration order, so they are the same in both scopes.
        Assert.All(local.Edges, e => Assert.Contains(all.Edges, a => a.Id == e.Id && a.From == e.From && a.To == e.To));
    }

    [Fact]
    public void Nodes_follow_ADR_003()
    {
        var g = Build(FlowScope.Local);
        Assert.Equal(("customerproject.dev", "auto", "LR"), (g.ProjectId, g.Layout, g.Direction));

        var ingress = Assert.Single(g.Nodes, n => n.Id == "customerproject.fn.ingress");
        Assert.Equal(("handler", "INGRESS", "both", "container:customerproject.ingress-app"), (ingress.Kind, ingress.Label, ingress.Scope, ingress.ContainerId));
        Assert.Null(ingress.Sb);
        Assert.Null(ingress.App);

        var seed = Assert.Single(g.Nodes, n => n.Id == "ext:seed-messages");
        Assert.Equal(("external", "seed-messages kit", "local"), (seed.Kind, seed.Label, seed.Scope));
        Assert.Null(seed.ContainerId); // entities and externals are never parented

        var sub = Assert.Single(g.Nodes, n => n.Id == "s:customerproject-topic/local-inspect");
        Assert.Equal(("subscription", "local-inspect", "local"), (sub.Kind, sub.Label, sub.Scope));

        Assert.Equal("shared", g.Nodes.Single(n => n.Id == "q:communication").Sb!.Owner);
        Assert.DoesNotContain(g.Nodes, n => n.Id.Contains("DeadLetter", StringComparison.OrdinalIgnoreCase) || n.Id.Contains("dlq", StringComparison.OrdinalIgnoreCase));

        var trigger = Assert.Single(g.Edges, e => e.Label == "SnapshotQueueHandler");
        Assert.Equal(("q:customerproject-snapshot", "customerproject.fn.snapshot", "trigger", "both", "code"), (trigger.From, trigger.To, trigger.Kind, trigger.Scope, trigger.Confidence));
    }

    [Fact]
    public void Depths_and_dlq_hot_with_scope_aware_max_delivery()
    {
        var g = Build(FlowScope.All, new Dictionary<string, EntityCounts>
        {
            ["q:customerproject-ingress"] = new(0, 2, "peek"),
            ["q:customerproject"] = new(5, 0, "peek"),
        });
        var ingress = g.Nodes.Single(n => n.Id == "q:customerproject-ingress").Sb!;
        Assert.Equal(new FlowSbInfo(0, 2, true, "project", 10), ingress);
        Assert.False(g.Nodes.Single(n => n.Id == "q:customerproject").Sb!.DlqHot);

        var unknown = g.Nodes.Single(n => n.Id == "q:customerproject-domain").Sb!; // no depth = unknown, never 0
        Assert.Equal((null, null, false), (unknown.ActiveCount, unknown.DeadLetterCount, unknown.DlqHot));

        Assert.Null(g.Nodes.Single(n => n.Id == "t:customerproject-topic").Sb!.MaxDeliveryCount);
        Assert.Equal(3, g.Nodes.Single(n => n.Id == "s:customerproject-topic/customerproject-to-customerproject").Sb!.MaxDeliveryCount); // cloud-only: cloud value
    }

    [Fact]
    public void Stored_positions_attach_per_node_and_layout_is_partial()
    {
        var g = Build(FlowScope.Local, positions: new Dictionary<string, FlowPosition>
        {
            ["container:customerproject.func-app"] = new(420, 80),   // absolute
            ["customerproject.fn.domain"] = new(24, 40),             // relative to func-app
            ["q:customerproject"] = new(760, 120),
        });
        Assert.Equal("partial", g.Layout);
        Assert.Equal(new FlowPosition(420, 80), g.Containers.Single(c => c.Id == "container:customerproject.func-app").Position);
        Assert.Equal(new FlowPosition(24, 40), g.Nodes.Single(n => n.Id == "customerproject.fn.domain").Position);
        Assert.Null(g.Nodes.Single(n => n.Id == "customerproject.fn.commands").Position); // auto-laid inside its container
        Assert.Null(g.Containers.Single(c => c.Id == "container:customerproject.api").Position);

        // Everything visible stored → "stored"; a position only for a hidden (cloud) node doesn't count.
        var all = Build(FlowScope.Local);
        var full = all.Nodes.Select(n => n.Id).Concat(all.Containers.Select(c => c.Id)).ToDictionary(id => id, _ => new FlowPosition(1, 1));
        Assert.Equal("stored", Build(FlowScope.Local, positions: full).Layout);
        var cloudOnly = Build(FlowScope.Local, positions: new Dictionary<string, FlowPosition> { ["s:customerproject-topic/customerproject-to-customerproject"] = new(1, 1) });
        Assert.Equal("auto", cloudOnly.Layout);
    }

    [Fact]
    public void All_node_ids_include_containers_but_not_apps()
    {
        var ids = FlowGraphBuilder.AllNodeIds(_r);
        Assert.Contains("container:customerproject.ingress-app", ids);
        Assert.Contains("customerproject.fn.snapshot", ids);
        Assert.Contains("s:customerproject-topic/customerproject-to-customerproject", ids); // cloud-only counts too
        Assert.DoesNotContain("app:ingress", ids);
        Assert.Equal(15 + 3, ids.Count);
    }

    [Theory]
    [InlineData(null, FlowScope.Local)]
    [InlineData("LOCAL", FlowScope.Local)]
    [InlineData("all", FlowScope.All)]
    public void Scope_parsing(string? value, FlowScope expected) => Assert.Equal(expected, FlowGraphBuilder.ParseScope(value));

    [Fact]
    public void Bad_scope_or_direction_is_400()
    {
        Assert.Equal("INVALID_SCOPE", Assert.Throws<ApiException>(() => FlowGraphBuilder.ParseScope("cloud")).Code);
        Assert.Equal("INVALID_DIRECTION", Assert.Throws<ApiException>(() => FlowGraphBuilder.ParseDirection("RL")).Code);
        Assert.Equal("TB", FlowGraphBuilder.ParseDirection("tb"));
        Assert.Equal("LR", FlowGraphBuilder.ParseDirection(null));
    }
}

/// <summary>Option C: registration layoutDefaults (base) ← runtime positions (override), per key; seeded customerproject layout.</summary>
public class LayoutDefaultsTests
{
    // ui/app/src/pages/flow/layout.ts: node W×H, container header, plus layoutHints.containerPadding.
    private const double W = 176, H = 40, Hdr = 24, Pad = 16;
    private readonly ProjectRegistration _r = new RegistrationLoader(RepoPaths.Schema).Load(RepoPaths.Registration);

    private FlowGraph Graph(ProjectRegistration r, IReadOnlyDictionary<string, FlowPosition> stored, FlowScope scope = FlowScope.Local)
    {
        var (positions, sources) = FlowGraphBuilder.EffectivePositions(r, stored);
        return FlowGraphBuilder.Build(r, scope, "LR", SbScope.Local, new Dictionary<string, EntityCounts>(), positions, [], null, sources);
    }

    [Fact]
    public void Defaults_are_served_when_the_store_is_empty()
    {
        var g = Graph(_r, new Dictionary<string, FlowPosition>());
        Assert.Equal("stored", g.Layout);
        Assert.All(g.Nodes, n => Assert.Equal("default", n.PositionSource));
        Assert.All(g.Containers, c => Assert.Equal("default", c.PositionSource));
        Assert.Equal(new FlowPosition(16, 136), g.Nodes.Single(n => n.Id == "customerproject.fn.commands").Position); // relative
        Assert.Empty(g.Warnings);
    }

    [Fact]
    public void The_store_overrides_per_key()
    {
        var g = Graph(_r, new Dictionary<string, FlowPosition> { ["q:customerproject"] = new(1, 2), ["container:customerproject.api"] = new(3, 4) });
        Assert.Equal((new FlowPosition(1, 2), "stored"), (g.Nodes.Single(n => n.Id == "q:customerproject").Position!, g.Nodes.Single(n => n.Id == "q:customerproject").PositionSource!));
        Assert.Equal("stored", g.Containers.Single(c => c.Id == "container:customerproject.api").PositionSource);
        Assert.Equal((new FlowPosition(720, 256), "default"), (g.Nodes.Single(n => n.Id == "q:customerproject-domain").Position!, g.Nodes.Single(n => n.Id == "q:customerproject-domain").PositionSource!));
    }

    [Fact]
    public void Layout_flag_counts_both_layers()
    {
        var partialDefaults = Clone(_r);
        foreach (var id in partialDefaults.LayoutDefaults!.Positions.Keys.Where(k => k.StartsWith("q:")).ToList()) partialDefaults.LayoutDefaults.Positions.Remove(id);
        Assert.Equal("partial", Graph(partialDefaults, new Dictionary<string, FlowPosition>()).Layout);
        // The runtime layer fills the gaps → stored.
        var fill = _r.LayoutDefaults!.Positions.Where(kv => kv.Key.StartsWith("q:")).ToDictionary(kv => kv.Key, kv => new FlowPosition(kv.Value.X, kv.Value.Y));
        Assert.Equal("stored", Graph(partialDefaults, fill).Layout);

        var none = Clone(_r);
        none.LayoutDefaults = null;
        Assert.Equal("auto", Graph(none, new Dictionary<string, FlowPosition>()).Layout);
        Assert.Equal("partial", Graph(none, new Dictionary<string, FlowPosition> { ["q:customerproject"] = new(1, 1) }).Layout);
    }

    [Fact]
    public void Unknown_default_ids_are_ignored_with_a_warning()
    {
        var node = JsonNode.Parse(File.ReadAllText(RepoPaths.Registration))!.AsObject();
        node["layoutDefaults"]!["positions"]!.AsObject()["app:gone"] = new JsonObject { ["x"] = 1, ["y"] = 2 };
        var path = Path.Combine(Path.GetTempPath(), $"reg-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, node.ToJsonString());
        try
        {
            var r = new RegistrationLoader(RepoPaths.Schema).Load(path); // not fatal
            var g = Graph(r, new Dictionary<string, FlowPosition>());
            Assert.Contains(g.Warnings, w => w.StartsWith("LAYOUT_DEFAULTS_UNKNOWN_ID") && w.Contains("app:gone"));
            Assert.DoesNotContain(g.Nodes, n => n.Id == "app:gone");
            Assert.Equal("stored", g.Layout);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("customerproject.dev.json")]
    [InlineData("price.dev.json")]
    public void Seeded_defaults_cover_every_id_and_nothing_overlaps(string file)
    {
        var r = new RegistrationLoader(RepoPaths.Schema).Load(Path.Combine(RepoPaths.Root, "architecture", "registrations", file));
        var defaults = r.LayoutDefaults!.Positions;
        Assert.Equal(FlowGraphBuilder.AllNodeIds(r).Order(), defaults.Keys.Order());
        Assert.Empty(r.LayoutWarnings);

        var g = Graph(r, new Dictionary<string, FlowPosition>(), FlowScope.All);
        var boxes = new Dictionary<string, (double X, double Y, double W, double H)>();
        foreach (var c in g.Containers)
        {
            var kids = g.Nodes.Where(n => n.ContainerId == c.Id).Select(n => n.Position!).ToList();
            foreach (var k in kids) Assert.True(k.X >= Pad && k.Y >= Hdr + Pad, $"{c.Id}: child at {k} is above/left of the inner padding");
            // Same rule as layout.ts containerSize: children bbox + padding, never smaller than one slot.
            var w = Math.Max(W + Pad * 2, kids.Max(k => k.X + W + Pad));
            var h = Math.Max(Hdr + H + Pad * 2, kids.Max(k => k.Y + H + Pad));
            boxes[c.Id] = (c.Position!.X, c.Position.Y, w, h);
            // Children never overlap each other.
            foreach (var a in kids) foreach (var b in kids.Where(b => b != a))
                Assert.False(Overlap((a.X, a.Y, W, H), (b.X, b.Y, W, H)), $"{c.Id}: children overlap");
        }
        var top = boxes.Select(kv => (kv.Key, kv.Value))
            .Concat(g.Nodes.Where(n => n.ContainerId is null).Select(n => (n.Id, (n.Position!.X, n.Position.Y, W, H)))).ToList();
        foreach (var a in top) foreach (var b in top.Where(b => b.Item1 != a.Item1))
            Assert.False(Overlap(a.Item2, b.Item2), $"{a.Item1} overlaps {b.Item1}");
        if (file != "customerproject.dev.json") return;
        // LR reading order of the main path (§8).
        double X(string id) => top.Single(t => t.Item1 == id).Item2.Item1;
        Assert.True(X("ext:seed-messages") < X("q:customerproject-ingress") && X("q:customerproject-ingress") < X("container:customerproject.ingress-app")
            && X("container:customerproject.ingress-app") < X("q:customerproject-domain") && X("q:customerproject-domain") < X("container:customerproject.func-app")
            && X("container:customerproject.func-app") < X("q:customerproject") && X("q:customerproject") < X("t:customerproject-topic"));
    }

    private static bool Overlap((double X, double Y, double W, double H) a, (double X, double Y, double W, double H) b) =>
        a.X < b.X + b.W && b.X < a.X + a.W && a.Y < b.Y + b.H && b.Y < a.Y + a.H;

    private static ProjectRegistration Clone(ProjectRegistration r)
    {
        var copy = System.Text.Json.JsonSerializer.Deserialize<ProjectRegistration>(System.Text.Json.JsonSerializer.Serialize(r, RegistrationJson.Options), RegistrationJson.Options)!;
        return copy;
    }
}

/// <summary>ADDENDA-008 §7/§11 positions v2: env-scoped pos: keys, relative inner nodes, v1 migration, unknown keys kept.</summary>
public sealed class FlowPositionStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dsp-flow-positions-" + Guid.NewGuid().ToString("N"), "flow-positions");
    private readonly FlowPositionContext _ctx = new("customerproject", "dev",
        new Dictionary<string, string> { ["app:api"] = "container:customerproject.api", ["app:functions"] = "container:customerproject.func-app" },
        new Dictionary<string, string> { ["customerproject.fn.domain"] = "container:customerproject.func-app" });
    private const string Id = "customerproject.dev";

    public void Dispose()
    {
        var parent = Path.GetDirectoryName(_dir)!;
        if (Directory.Exists(parent)) Directory.Delete(parent, recursive: true);
    }

    private FlowPositionStore Store() => new(_dir);
    private JsonObject File() => JsonNode.Parse(System.IO.File.ReadAllText(Store().PathFor(Id)))!.AsObject();
    private void WriteFile(string json) { Directory.CreateDirectory(_dir); System.IO.File.WriteAllText(Store().PathFor(Id), json); }

    [Fact]
    public async Task Partial_merge_round_trips_and_reset_clears_only_this_env()
    {
        var store = Store();
        Assert.Empty(store.Load(Id, _ctx)); // directory doesn't exist yet
        Assert.Empty(await store.MergeAsync(Id, new Dictionary<string, FlowPosition>(), _ctx, default)); // reset before anything was saved

        await store.MergeAsync(Id, new Dictionary<string, FlowPosition> { ["container:customerproject.func-app"] = new(420, 80), ["q:customerproject"] = new(3, 4) }, _ctx, default);
        var saved = await store.MergeAsync(Id, new Dictionary<string, FlowPosition> { ["customerproject.fn.domain"] = new(24, 40) }, _ctx, default); // UI sends only the dragged key

        Assert.Equal(new Dictionary<string, FlowPosition>
        {
            ["container:customerproject.func-app"] = new(420, 80), ["q:customerproject"] = new(3, 4), ["customerproject.fn.domain"] = new(24, 40),
        }, saved);
        Assert.Equal(saved, store.Load(Id, _ctx));
        var file = File();
        Assert.Equal(2, file["version"]!.GetValue<int>());
        Assert.Equal("container:customerproject.func-app", file["positions"]!["pos:customerproject:dev:customerproject.fn.domain"]!["parent"]!.ToString());
        Assert.NotNull(file["positions"]!["pos:customerproject:dev:container:customerproject.func-app"]);
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp", SearchOption.AllDirectories)); // atomic write left no temp file

        // Another env's key survives a reset of dev; the file is rewritten, not deleted.
        var other = _ctx with { Env = "test" };
        await store.MergeAsync(Id, new Dictionary<string, FlowPosition> { ["q:customerproject"] = new(7, 7) }, other, default);
        Assert.Empty(await store.MergeAsync(Id, new Dictionary<string, FlowPosition>(), _ctx, default));
        Assert.True(System.IO.File.Exists(store.PathFor(Id)));
        Assert.Empty(store.Load(Id, _ctx));
        Assert.Equal(new FlowPosition(7, 7), store.Load(Id, other)["q:customerproject"]);
    }

    [Fact]
    public async Task V1_file_migrates_in_memory_and_is_written_as_v2_on_the_next_put()
    {
        const string v1 = """{ "app:api": { "x": 10, "y": 20 }, "q:customerproject": { "x": 30, "y": 40 }, "app:retired": { "x": 1, "y": 2 } }""";
        WriteFile(v1);
        var store = Store();

        Assert.Equal(new Dictionary<string, FlowPosition> { ["container:customerproject.api"] = new(10, 20), ["q:customerproject"] = new(30, 40) }, store.Load(Id, _ctx));
        Assert.Equal(v1, System.IO.File.ReadAllText(store.PathFor(Id))); // GET never writes

        await store.MergeAsync(Id, new Dictionary<string, FlowPosition> { ["q:customerproject"] = new(31, 41) }, _ctx, default);
        var positions = File()["positions"]!.AsObject();
        Assert.Equal(["app:retired", "pos:customerproject:dev:container:customerproject.api", "pos:customerproject:dev:q:customerproject"],
            positions.Select(kv => kv.Key).Order(StringComparer.Ordinal));
        Assert.Equal(1, positions["app:retired"]!["x"]!.GetValue<double>()); // no container for it: kept verbatim, hidden
    }

    [Fact]
    public void Migration_is_idempotent()
    {
        var v1 = JsonNode.Parse("""{ "app:functions": { "x": 1, "y": 2 }, "ext:seed-messages": { "x": 3, "y": 4 } }""")!.AsObject();
        var once = FlowPositionStore.Migrate(v1, _ctx);
        var twice = FlowPositionStore.Migrate(new JsonObject { ["version"] = 2, ["positions"] = once.DeepClone() }, _ctx);
        Assert.True(JsonNode.DeepEquals(once, twice));
        Assert.Equal(["pos:customerproject:dev:container:customerproject.func-app", "pos:customerproject:dev:ext:seed-messages"], once.Select(kv => kv.Key).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Unknown_keys_survive_writes_verbatim()
    {
        WriteFile("""
            { "version": 2, "positions": {
              "pos:customerproject:dev:q:gone-after-regen": { "x": 5, "y": 6, "note": "kept" },
              "pos:otherproject:dev:q:x": { "x": 1, "y": 1 },
              "something-else": [1, 2, 3] } }
            """);
        var store = Store();
        await store.MergeAsync(Id, new Dictionary<string, FlowPosition> { ["q:customerproject"] = new(1, 2) }, _ctx, default);
        var positions = File()["positions"]!.AsObject();
        Assert.Equal("kept", positions["pos:customerproject:dev:q:gone-after-regen"]!["note"]!.ToString());
        Assert.Equal(3, positions["something-else"]!.AsArray().Count);
        Assert.NotNull(positions["pos:otherproject:dev:q:x"]);
    }

    [Fact]
    public async Task Handler_moved_to_another_container_loses_its_relative_position_but_the_key_is_kept()
    {
        var store = Store();
        await store.MergeAsync(Id, new Dictionary<string, FlowPosition> { ["customerproject.fn.domain"] = new(24, 40) }, _ctx, default);
        var moved = _ctx with { Parents = new Dictionary<string, string> { ["customerproject.fn.domain"] = "container:customerproject.domain-logical" } };
        Assert.Empty(store.Load(Id, moved));
        Assert.NotNull(File()["positions"]!["pos:customerproject:dev:customerproject.fn.domain"]);
    }
}

/// <summary>HTTP round trip with no Service Bus configured: the graph must still render (null depths + warning). No network.</summary>
public sealed class FlowApiTests : IClassFixture<FlowApiTests.Factory>
{
    public sealed class Factory : WebApplicationFactory<Program>
    {
        public readonly string DataDir = Path.Combine(Path.GetTempPath(), "dsp-flow-api-" + Guid.NewGuid().ToString("N"));

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production"); // appsettings.json: empty SB connection string
            builder.UseSetting("Overview:DataDir", DataDir);
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (Directory.Exists(DataDir)) Directory.Delete(DataDir, recursive: true);
        }
    }

    private const string P = "/api/projects/customerproject.dev";
    private readonly Factory _factory;
    private readonly HttpClient _http;

    public FlowApiTests(Factory factory) => (_factory, _http) = (factory, factory.CreateClient());

    private async Task<JsonElement> Get(string url)
    {
        var r = await _http.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;
    }

    private static JsonElement Node(JsonElement g, string id) => g.GetProperty("nodes").EnumerateArray().Single(n => n.GetProperty("id").GetString() == id);

    [Fact]
    public async Task Flow_renders_without_service_bus_and_positions_round_trip()
    {
        await _http.PutAsJsonAsync($"{P}/flow/positions", new { }); // start without runtime positions
        var g = await Get($"{P}/flow");
        // Checked-in layoutDefaults cover every node and container: "stored" with source "default" before anything is dragged.
        Assert.Equal(("stored", "LR"), (g.GetProperty("layout").GetString(), g.GetProperty("direction").GetString()));
        Assert.Equal(2, g.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(14, g.GetProperty("nodes").GetArrayLength());
        Assert.Equal(3, g.GetProperty("containers").GetArrayLength());
        Assert.Equal(15, g.GetProperty("edges").GetArrayLength());
        Assert.Equal(["app:api", "app:functions", "app:ingress"],
            g.GetProperty("containers").EnumerateArray().Select(c => c.GetProperty("appId").GetString()).Order());
        Assert.All(g.GetProperty("containers").EnumerateArray(), c =>
            Assert.Equal(c.GetProperty("appId").GetString(), c.GetProperty("appIds").EnumerateArray().Single().GetString()));
        Assert.Equal(16, g.GetProperty("layoutHints").GetProperty("containerPadding").GetInt32());
        Assert.Contains(g.GetProperty("warnings").EnumerateArray(), w => w.GetString()!.StartsWith("SB_UNREACHABLE"));
        var sb = Node(g, "q:customerproject-ingress").GetProperty("sb");
        Assert.Equal(JsonValueKind.Null, sb.GetProperty("activeCount").ValueKind);
        Assert.Equal(JsonValueKind.Null, sb.GetProperty("deadLetterCount").ValueKind);
        Assert.Equal("default", Node(g, "customerproject.fn.domain").GetProperty("positionSource").GetString());
        Assert.Equal((16.0, 40.0), (Node(g, "customerproject.fn.domain").GetProperty("position").GetProperty("x").GetDouble(),
            Node(g, "customerproject.fn.domain").GetProperty("position").GetProperty("y").GetDouble()));
        var commands = Node(g, "customerproject.fn.commands");
        Assert.Equal(("handler", "container:customerproject.func-app", "CustomerProjectQueueHandler"),
            (commands.GetProperty("kind").GetString(), commands.GetProperty("containerId").GetString(), commands.GetProperty("handler").GetProperty("function").GetString()));
        Assert.Equal("q:customerproject", commands.GetProperty("handler").GetProperty("selfRequeue")[0].GetProperty("entityId").GetString());

        var all = await Get($"{P}/flow?scope=all&direction=TB");
        Assert.Equal((15, 17, "TB"), (all.GetProperty("nodes").GetArrayLength(), all.GetProperty("edges").GetArrayLength(), all.GetProperty("direction").GetString()));

        // Container absolute + inner handler relative; unknown ids (stale app:*) are ignored, not a 400.
        var put = await _http.PutAsJsonAsync($"{P}/flow/positions", new Dictionary<string, object>
        {
            ["container:customerproject.func-app"] = new { x = 120.5, y = 40 }, ["app:bogus"] = new { x = 1, y = 1 }, ["app:api"] = new { x = 1, y = 1 },
        });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        put = await _http.PutAsJsonAsync($"{P}/flow/positions", new Dictionary<string, object> { ["customerproject.fn.domain"] = new { x = 24, y = 40 } });
        var saved = JsonDocument.Parse(await put.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(["container:customerproject.func-app", "customerproject.fn.domain"], saved.EnumerateObject().Select(p => p.Name).Order());
        var path = Path.Combine(_factory.DataDir, "flow-positions", "customerproject.dev.json");
        Assert.Contains("pos:customerproject:dev:container:customerproject.func-app", File.ReadAllText(path));

        var stored = await Get($"{P}/flow");
        Assert.Equal("stored", stored.GetProperty("layout").GetString());
        var funcApp = stored.GetProperty("containers").EnumerateArray().Single(c => c.GetProperty("id").GetString() == "container:customerproject.func-app");
        var box = funcApp.GetProperty("position");
        Assert.Equal((120.5, 40.0), (box.GetProperty("x").GetDouble(), box.GetProperty("y").GetDouble()));
        Assert.Equal("stored", funcApp.GetProperty("positionSource").GetString());
        Assert.Equal("default", Node(stored, "customerproject.fn.commands").GetProperty("positionSource").GetString()); // per key
        var inner = Node(stored, "customerproject.fn.domain").GetProperty("position");
        Assert.Equal((24.0, 40.0), (inner.GetProperty("x").GetDouble(), inner.GetProperty("y").GetDouble()));

        var reset = await _http.PutAsJsonAsync($"{P}/flow/positions", new { });
        Assert.Equal("{}", (await reset.Content.ReadAsStringAsync()).Trim());
        var afterReset = await Get($"{P}/flow");
        Assert.Equal("stored", afterReset.GetProperty("layout").GetString()); // defaults again, not empty
        var back = afterReset.GetProperty("containers").EnumerateArray().Single(c => c.GetProperty("id").GetString() == "container:customerproject.func-app");
        Assert.Equal(("default", 960.0), (back.GetProperty("positionSource").GetString(), back.GetProperty("position").GetProperty("x").GetDouble()));
        Assert.True(File.Exists(path)); // reset clears this env's keys, the file stays
    }

    [Fact]
    public async Task Export_returns_the_effective_map_and_round_trips_into_the_registration()
    {
        await _http.PutAsJsonAsync($"{P}/flow/positions", new { });
        var registrationText = File.ReadAllText(RepoPaths.Registration);
        var record = JsonNode.Parse(registrationText)!.AsObject();

        // Store empty: export = layoutDefaults; written back it is byte-identical (idempotent, stable key order).
        var export = JsonNode.Parse(await _http.GetStringAsync($"{P}/flow/positions/export"))!.AsObject();
        Assert.Equal(18, export["positions"]!.AsObject().Count);
        Assert.Equal(0, export["fromStored"]!.GetValue<int>());
        Assert.True(JsonNode.DeepEquals(record["layoutDefaults"]!["positions"], export["positions"]));
        Assert.Equal(registrationText, RegistrationGenerator.RegistrationGenerator.Serialize(Apply(record, export)));

        // A runtime drag overrides one key; the export carries it (rounded), everything else stays default.
        await _http.PutAsJsonAsync($"{P}/flow/positions", new Dictionary<string, object> { ["q:customerproject"] = new { x = 1210.6, y = 300.2 } });
        export = JsonNode.Parse(await _http.GetStringAsync($"{P}/flow/positions/export"))!.AsObject();
        Assert.Equal((1211.0, 300.0), (export["positions"]!["q:customerproject"]!["x"]!.GetValue<double>(), export["positions"]!["q:customerproject"]!["y"]!.GetValue<double>()));
        Assert.Equal(1, export["fromStored"]!.GetValue<int>());
        var once = Apply(record, export);
        var twice = Apply(once, export);
        Assert.Equal(RegistrationGenerator.RegistrationGenerator.Serialize(once), RegistrationGenerator.RegistrationGenerator.Serialize(twice));
        Assert.Equal(1211, once["layoutDefaults"]!["positions"]!["q:customerproject"]!["x"]!.GetValue<long>());
        Assert.Equal(record["layoutDefaults"]!["note"]!.ToString(), once["layoutDefaults"]!["note"]!.ToString()); // note kept
        Assert.Equal(HttpStatusCode.NotFound, (await _http.GetAsync("/api/projects/nope/flow/positions/export")).StatusCode);
        await _http.PutAsJsonAsync($"{P}/flow/positions", new { });
    }

    private static JsonObject Apply(JsonObject record, JsonObject export) =>
        RegistrationGenerator.LayoutDefaults.Apply(record, export["positions"]!.AsObject().Select(kv =>
            KeyValuePair.Create(kv.Key, (kv.Value!["x"]!.GetValue<double>(), kv.Value!["y"]!.GetValue<double>()))));

    [Fact]
    public async Task Unknown_project_is_404_and_bad_input_is_400()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await _http.GetAsync("/api/projects/nope/flow")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _http.PutAsJsonAsync("/api/projects/nope/flow/positions", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _http.GetAsync($"{P}/flow?scope=cloud")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _http.GetAsync($"{P}/flow?direction=RL")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _http.PutAsJsonAsync($"{P}/flow/positions", new Dictionary<string, object> { ["container:customerproject.api"] = new { x = 1 } })).StatusCode);
    }
}
