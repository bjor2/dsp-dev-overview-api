using System.Text.Json;
using System.Text.Json.Nodes;
using Dsp.DevOverview.Api.Registration;

namespace Dsp.DevOverview.Api.Tests;

public class RegistrationTests
{
    private readonly RegistrationLoader _loader = new(RepoPaths.Schema);

    [Fact]
    public void Customerproject_registration_loads_and_is_schema_valid()
    {
        var r = _loader.Load(RepoPaths.Registration);

        Assert.Equal("customerproject.dev", r.Id);
        Assert.Equal("customerproject", r.Project);
        Assert.Equal(["app:api", "app:functions", "app:ingress"], r.Apps.Select(a => a.Id));
        Assert.Equal(
            ["q:customerproject", "q:customerproject-ingress", "q:customerproject-snapshot", "q:customerproject-domain",
             "q:communication", "q:customerproject-topic-inspect", "t:customerproject-topic",
             "s:customerproject-topic/local-inspect", "s:customerproject-topic/customerproject-to-customerproject"],
            r.Entities.Select(e => e.Id));
    }

    [Fact]
    public void Local_scope_lists_emulator_entities_and_cloud_names_are_informational()
    {
        var r = _loader.Load(RepoPaths.Registration);
        var local = r.Entities.Where(e => e.ExistsIn(SbScope.Local)).Select(e => e.LocalName).ToList();

        Assert.Equal(["customerproject", "customerproject-ingress", "customerproject-snapshot", "customerproject-domain",
            "communication", "customerproject-topic-inspect", "customerproject-topic", "local-inspect"], local);
        Assert.Equal("sbq-870-dev-euw-dahl-shared-customerproject", r.FindEntity("q:customerproject")!.NameFor(SbScope.Cloud));
        Assert.Equal("customerproject", r.FindEntity("q:customerproject")!.NameFor(SbScope.Local));
    }

    [Theory]
    [InlineData("q:customerproject", 10, 3)]
    [InlineData("q:customerproject-ingress", 10, 3)]
    [InlineData("q:customerproject-topic-inspect", 10, 3)] // local-only: no cloudMaxDeliveryCount → cloud default 3
    [InlineData("s:customerproject-topic/local-inspect", 10, 3)]
    public void MaxDeliveryCount_is_scope_aware(string entityId, int local, int cloud)
    {
        var e = _loader.Load(RepoPaths.Registration).FindEntity(entityId)!;
        Assert.Equal(local, e.MaxDeliveryCountFor(SbScope.Local));
        Assert.Equal(cloud, e.MaxDeliveryCountFor(SbScope.Cloud));
    }

    [Fact]
    public void Registration_missing_required_field_is_rejected()
    {
        var node = JsonNode.Parse(File.ReadAllText(RepoPaths.Registration))!.AsObject();
        node.Remove("apps");
        node["env"] = "prod";
        using var doc = JsonDocument.Parse(node.ToJsonString());

        var errors = _loader.Validate(doc.RootElement);

        Assert.NotEmpty(errors);
        Assert.Contains(errors, e => e.Contains("apps") || e.Contains("required"));
    }

    [Fact]
    public void Loader_does_not_rewrite_the_architect_registration()
    {
        var before = File.ReadAllBytes(RepoPaths.Registration);
        _loader.Load(RepoPaths.Registration);
        Assert.Equal(before, File.ReadAllBytes(RepoPaths.Registration));
    }
}

/// <summary>ADDENDA-008: v2 loader — v1 files upgrade in memory, cross-field validators reject bad records.</summary>
public class RegistrationV2LoaderTests
{
    private readonly RegistrationLoader _loader = new(RepoPaths.Schema);
    private static string V1 => Path.Combine(RepoPaths.Root, "test", "fixtures", "registration-v1", "customerproject.dev.v1.json");

    private static string Temp(JsonObject record)
    {
        var path = Path.Combine(Path.GetTempPath(), $"reg-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, record.ToJsonString());
        return path;
    }

    private static JsonObject Current() => JsonNode.Parse(File.ReadAllText(RepoPaths.Registration))!.AsObject();

    [Fact]
    public void V2_registration_has_handlers_containers_and_no_app_triggers()
    {
        var r = _loader.Load(RepoPaths.Registration);
        Assert.Equal(2, r.SchemaVersion);
        Assert.Empty(r.UpgradeNotes);
        Assert.Equal(5, r.Handlers.Count);
        Assert.Equal(3, r.Containers.Count);
        Assert.All(r.Handlers, h => Assert.Contains(h.AppId, r.Containers.Single(c => c.Id == h.ContainerId).ResourceRefs));
        Assert.Equal(["CreateProjectsFromErpReferences", "HandleCustomerAddressChanged", "PopulateOrderReferences", "SendNotification", "ValidateOrder"],
            r.Handlers.Single(h => h.Id == "customerproject.fn.commands").SelfRequeue.Single().Messages);
        Assert.DoesNotContain("\"triggers\": []", File.ReadAllText(RepoPaths.Registration)); // apps[].triggers removed in v2
    }

    [Fact]
    public void V1_registration_is_accepted_and_upgraded_in_memory()
    {
        var before = File.ReadAllBytes(V1);
        var r = _loader.Load(V1);
        Assert.Equal(before, File.ReadAllBytes(V1));
        Assert.Equal(2, r.SchemaVersion);
        Assert.NotEmpty(r.UpgradeNotes);
        Assert.Equal(["customerproject.api.http", "customerproject.fn.commands", "customerproject.fn.domain", "customerproject.fn.ingress", "customerproject.fn.snapshot"],
            r.Handlers.Select(h => h.Id).Order()); // overrides.handlerIds is absent in v1: COMMANDS id comes from the function name
        Assert.DoesNotContain(r.Edges, e => e.From.StartsWith("app:") || e.To.StartsWith("app:"));
        // app:ingress → q:customerproject-snapshot can't tell which handler sent it: only INGRESS gets it, SNAPSHOT (its consumer) not.
        Assert.Contains(r.Edges, e => e.From == "customerproject.fn.ingress" && e.To == "q:customerproject-snapshot");
        Assert.DoesNotContain(r.Edges, e => e.From == "customerproject.fn.snapshot" && e.To == "q:customerproject-snapshot");
        var g = Dsp.DevOverview.Api.Flow.FlowGraphBuilder.Build(r, Dsp.DevOverview.Api.Flow.FlowScope.Local, "LR", SbScope.Local,
            new Dictionary<string, Dsp.DevOverview.Api.ServiceBus.EntityCounts>(), new Dictionary<string, Dsp.DevOverview.Api.Flow.FlowPosition>(), []);
        Assert.Contains(g.Warnings, w => w.StartsWith("REGISTRATION_V1_UPGRADED"));
    }

    [Fact]
    public void Edge_to_a_container_or_app_is_rejected()
    {
        var rec = Current();
        rec["edges"]!.AsArray().Add(new JsonObject { ["from"] = "customerproject.fn.domain", ["to"] = "container:customerproject.func-app", ["kind"] = "publish", ["scope"] = "both", ["confidence"] = "code" });
        var path = Temp(rec);
        try
        {
            var ex = Assert.Throws<RegistrationValidationException>(() => _loader.Load(path));
            Assert.Contains(ex.Errors, e => e.StartsWith("V2_EDGE_ENDPOINT") && e.Contains("container:customerproject.func-app"));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void I1_violation_is_rejected_by_the_loader()
    {
        var rec = Current();
        rec["edges"]!.AsArray().Add(new JsonObject { ["from"] = "customerproject.fn.commands", ["to"] = "q:customerproject", ["kind"] = "publish", ["scope"] = "both", ["confidence"] = "code" });
        var path = Temp(rec);
        try
        {
            var ex = Assert.Throws<RegistrationValidationException>(() => _loader.Load(path));
            Assert.Contains(ex.Errors, e => e.StartsWith("I1_VIOLATION"));
        }
        finally { File.Delete(path); }
    }
}
