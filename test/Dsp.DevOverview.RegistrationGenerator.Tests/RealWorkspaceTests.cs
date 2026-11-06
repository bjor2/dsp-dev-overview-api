using System.Text.Json.Nodes;
using Dsp.DevOverview.RegistrationGenerator;

namespace Dsp.DevOverview.RegistrationGenerator.Tests;

/// <summary>
/// The real read-only sources (Terraform, dsp-customerproject-localrun, local-dev) against the record the API loads.
/// Returns early when the workspace is not on this machine (CI without /workspace).
/// </summary>
public class RealWorkspaceTests
{
    private static bool Available =>
        Directory.Exists(Path.Combine(GeneratorInputs.DefaultTerraformEnvDir, "unprovisioned_app-customerproject"))
        && Directory.Exists("/workspace/dsp/dsp-customerproject-localrun/src") && Directory.Exists("/workspace/dsp/local-dev/customerproject");

    private static (GenerationResult Result, JsonObject Current) Generate()
    {
        var path = RegistrationWriter.LoadedRegistrationPath(Fixtures.OverviewRoot, "customerproject.dev");
        var current = RegistrationGenerator.ReadCurrent(path)!;
        var inputs = new GeneratorInputs { Project = "customerproject", OverviewRoot = Fixtures.OverviewRoot };
        return (RegistrationGenerator.Generate(inputs, current, Fixtures.Now), current);
    }

    [Fact]
    public void Customerproject_record_is_schema_valid_and_keeps_overrides()
    {
        if (!Available) return;
        var (r, current) = Generate();
        Assert.Empty(r.SchemaErrors);
        Assert.True(JsonNode.DeepEquals(current["overrides"], r.Record["overrides"]));
        Assert.True(JsonNode.DeepEquals(current["observability"], r.Record["observability"]));
    }

    [Fact]
    public void Customerproject_handlers_match_ADDENDA_008()
    {
        if (!Available) return;
        var (r, current) = Generate();
        Assert.Empty(r.Issues);
        var rec = r.Record;
        var triggers = rec["handlers"]!.AsArray().SelectMany(h => h!["triggers"]!.AsArray().Where(t => t!["kind"]!.ToString() == "servicebus").Select(t =>
            $"{h["id"]} {h["containerId"]} {t!["function"]} {t["entityId"]} {t["setting"]} {t["connection"]}")).Order().ToList();
        Assert.Equal([
            "customerproject.fn.commands container:customerproject.func-app CustomerProjectQueueHandler q:customerproject ServiceBusCustomerProjectQueue ServiceBusConnectionString",
            "customerproject.fn.domain container:customerproject.func-app CustomerProjectDomainQueueHandler q:customerproject-domain ServiceBusCustomerProjectDomainQueue ServiceBusConnectionString",
            "customerproject.fn.ingress container:customerproject.ingress-app IngressQueueHandler q:customerproject-ingress ServiceBusCustomerProjectIngressQueue ServiceBusNamespace",
            "customerproject.fn.snapshot container:customerproject.ingress-app SnapshotQueueHandler q:customerproject-snapshot ServiceBusCustomerProjectSnapshotQueue ServiceBusNamespace",
        ], triggers);
        Assert.Equal([5011, 7239, 7240], rec["apps"]!.AsArray().Select(a => a!["local"]!["port"]!.GetValue<int>()));
        // The adopted record is up to date with the sources (only generatedAt differs).
        var (a, b) = ((JsonObject)current.DeepClone(), (JsonObject)r.Record.DeepClone());
        a.Remove("generatedAt");
        b.Remove("generatedAt");
        Assert.True(JsonNode.DeepEquals(a, b), "architecture/registrations/customerproject.dev.json is stale: run tools/Generate-Registration");
    }

    [Fact]
    public void Customerproject_edges_are_code_attributed_per_handler()
    {
        if (!Available) return;
        var rec = Generate().Result.Record;
        Assert.Equal(9, rec["entities"]!.AsArray().Count);
        var publishes = rec["edges"]!.AsArray().Where(e => e!["kind"]!.ToString() == "publish")
            .Select(e => $"{e!["from"]} -> {e["to"]} {e["confidence"]}").Order().ToList();
        Assert.Equal([
            "customerproject.api.http -> q:customerproject code",
            "customerproject.api.http -> t:customerproject-topic code",
            "customerproject.fn.commands -> q:communication code",
            "customerproject.fn.commands -> t:customerproject-topic code",
            "customerproject.fn.domain -> q:customerproject code",
            "customerproject.fn.ingress -> q:customerproject-snapshot code",
            "customerproject.fn.snapshot -> q:customerproject-domain code",
        ], publishes); // OQ-2: no api.http → customerproject-ingress
        Assert.Contains(rec.Edge("customerproject.fn.ingress", "q:customerproject-snapshot", "publish")!["sources"]!.AsArray(),
            s => s!.ToString().EndsWith("Ingress.Application/Import/ChangeEventHandlerBase.cs:52"));
        var badge = rec.Item("handlers", "customerproject.fn.commands")["selfRequeue"]!.AsArray().Single()!;
        Assert.Equal("q:customerproject", badge["entityId"]!.ToString());
        Assert.Equal(15, rec["edges"]!.AsArray().Count(e => e!["scope"]!.ToString() != "cloud"));
        Assert.Equal(2, rec["edges"]!.AsArray().Count(e => e!["scope"]!.ToString() == "cloud"));
    }
}
