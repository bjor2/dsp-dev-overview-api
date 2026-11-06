using System.Text.Json.Nodes;
using Dsp.DevOverview.RegistrationGenerator;

namespace Dsp.DevOverview.RegistrationGenerator.Tests;

internal static class Fixtures
{
    public static readonly string OverviewRoot = FindRoot();
    public static string Dir => Path.Combine(OverviewRoot, "test", "fixtures", "registration-generator");
    public static string Tf => Path.Combine(Dir, "tf");
    public static string Repo => Path.Combine(Dir, "repo");
    public static string Local => Path.Combine(Dir, "local");
    public static string Current => Path.Combine(Dir, "current.json");
    public static readonly DateTimeOffset Now = new(2026, 11, 2, 15, 0, 0, TimeSpan.FromHours(1));

    public static GeneratorInputs Inputs(bool external = false) => new()
    {
        Project = "demo", OverviewRoot = OverviewRoot, TerraformEnvDir = Tf, ServiceRepo = Repo, LocalDevDir = Local,
        SamplesCatalog = Path.Combine(Dir, "catalog", "demo.samples.json"), IncludeExternalSubscriptions = external,
    };

    public static GenerationResult Generate(bool external = false, bool withCurrent = true) =>
        RegistrationGenerator.Generate(Inputs(external), withCurrent ? RegistrationGenerator.ReadCurrent(Current) : null, Now);

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Dsp.DevOverview.sln"))) return dir.FullName;
        throw new DirectoryNotFoundException("Dsp.DevOverview.sln not found");
    }

    public static JsonObject Item(this JsonObject record, string section, string id) =>
        (record[section] as JsonArray)!.OfType<JsonObject>().Single(o => o["id"]?.ToString() == id);

    public static JsonObject? Edge(this JsonObject record, string from, string to, string kind) =>
        (record["edges"] as JsonArray)!.OfType<JsonObject>().SingleOrDefault(e => RegistrationGenerator.EdgeKey(e) == $"{from}|{to}|{kind}");
}

public class GeneratorTests
{
    [Fact]
    public void Output_is_valid_against_registration_schema_json()
    {
        var r = Fixtures.Generate();
        Assert.Empty(r.SchemaErrors);
        Assert.Empty(Fixtures.Generate(withCurrent: false).SchemaErrors);
        Assert.Empty(Fixtures.Generate(external: true).SchemaErrors);
        // And the schema really rejects a broken record (the check is not a no-op).
        var broken = (JsonObject)r.Record.DeepClone();
        broken["env"] = "prod";
        Assert.NotEmpty(RegistrationSchema.Load(Fixtures.Inputs().SchemaPath).Validate(broken));
    }

    [Fact]
    public void Apps_come_from_run_scripts_with_terraform_names_and_scanned_triggers()
    {
        var rec = Fixtures.Generate(withCurrent: false).Record;
        var api = rec.Item("apps", "app:api");
        Assert.Equal(("Api", "web", "web-870-dev-euw-dahl-demo"), (api["name"]!.ToString(), api["kind"]!.ToString(), api["azureResource"]!.ToString()));
        Assert.Equal("https://localhost:6011/swagger", api["local"]!["url"]!.ToString());
        Assert.Equal(6011, api["local"]!["port"]!.GetValue<int>());

        var ingress = rec.Item("apps", "app:ingress");
        Assert.Equal("func-ingress", ingress["kind"]!.ToString());
        Assert.Equal("func-870-dev-euw-dahl-demo-ingress", ingress["azureResource"]!.ToString()); // no current record → default naming values
        Assert.All(rec["apps"]!.AsArray(), a => Assert.Null(a!["triggers"])); // v2: triggers live on handlers[]

        // One handler per [Function] (two Functions in one class stay two handlers) + the web app's HTTP surface.
        Assert.Equal(2, rec["schemaVersion"]!.GetValue<int>());
        Assert.Equal(["demo.api.http", "demo.fn.demo", "demo.fn.ingress", "demo.fn.snapshot"], rec["handlers"]!.AsArray().Select(h => h!["id"]!.ToString()));
        var snapshot = rec.Item("handlers", "demo.fn.snapshot");
        Assert.Equal(("SNAPSHOT", "app:ingress", "container:demo.ingress-app"), (snapshot["label"]!.ToString(), snapshot["appId"]!.ToString(), snapshot["containerId"]!.ToString()));
        var t = snapshot["triggers"]![0]!;
        Assert.Equal(("servicebus", "SnapshotQueueHandler", "q:demo-snapshot", "src/Demo.Ingress.Functions/Functions/IngressHandlers.cs"),
            (t["kind"]!.ToString(), t["function"]!.ToString(), t["entityId"]!.ToString(), t["sourceFile"]!.ToString()));
        var fn = rec.Item("handlers", "demo.fn.demo")["triggers"]![0]!;
        Assert.Equal(("q:demo", "ServiceBusDemoQueue", "ServiceBusConnectionString"), (fn["entityId"]!.ToString(), fn["setting"]!.ToString(), fn["connection"]!.ToString()));
        var http = rec.Item("handlers", "demo.api.http");
        Assert.Equal(("HTTP controllers", "http", "src/Demo.Api"), (http["label"]!.ToString(), http["triggers"]![0]!["kind"]!.ToString(), http["triggers"]![0]!["sourceFile"]!.ToString()));

        // One resource container per app.
        Assert.Equal(["container:demo.api", "container:demo.func-app", "container:demo.ingress-app"], rec["containers"]!.AsArray().Select(c => c!["id"]!.ToString()));
        var box = rec.Item("containers", "container:demo.func-app");
        Assert.Equal(("Functions :7101", "resource", "app:functions"), (box["label"]!.ToString(), box["kind"]!.ToString(), box["resourceRefs"]![0]!.ToString()));
    }

    [Fact]
    public void Entities_merge_terraform_and_local_config()
    {
        var rec = Fixtures.Generate(withCurrent: false).Record;
        Assert.Equal(["q:demo", "q:demo-ingress", "q:demo-snapshot", "q:notification", "q:demo-topic-inspect", "t:demo-topic",
                      "s:demo-topic/local-inspect", "s:demo-topic/demo-to-demo"],
            rec["entities"]!.AsArray().Select(e => e!["id"]!.ToString()));

        var ingress = rec.Item("entities", "q:demo-ingress");
        Assert.Equal(("both", "sbq-870-dev-euw-dahl-shared-demo-ingress", 10, 5, "project"),
            (ingress["scope"]!.ToString(), ingress["cloudName"]!.ToString(), ingress["maxDeliveryCount"]!.GetValue<int>(),
             ingress["cloudMaxDeliveryCount"]!.GetValue<int>(), ingress["owner"]!.ToString()));
        Assert.Equal("shared", rec.Item("entities", "q:notification")["owner"]!.ToString());

        var inspect = rec.Item("entities", "q:demo-topic-inspect");
        Assert.Equal(("local", null), (inspect["scope"]!.ToString(), inspect["cloudName"]));

        var topic = rec.Item("entities", "t:demo-topic"); // local name from env.linux.ps1, cloud name from Terraform
        Assert.Equal(("demo-topic", "sbt-870-dev-euw-dahl-shared-demo", "both"), (topic["localName"]!.ToString(), topic["cloudName"]!.ToString(), topic["scope"]!.ToString()));

        var local = rec.Item("entities", "s:demo-topic/local-inspect");
        Assert.Equal(("t:demo-topic", "q:demo-topic-inspect", "local"), (local["topic"]!.ToString(), local["forwardTo"]!.ToString(), local["scope"]!.ToString()));

        var cloud = rec.Item("entities", "s:demo-topic/demo-to-demo");
        Assert.Equal(("demo-to-demo", "q:demo", "cloud", "n/a", 3), (cloud["cloudName"]!.ToString(), cloud["forwardTo"]!.ToString(),
            cloud["scope"]!.ToString(), cloud["localName"]!.ToString(), cloud["cloudMaxDeliveryCount"]!.GetValue<int>()));
        Assert.Equal("(sys.label = 'DemoReferenceChanged' OR sys.Label = 'DemoMoved')", cloud["sqlFilter"]!.ToString());
    }

    [Fact]
    public void Edges_triggers_publishers_and_subscriptions()
    {
        var rec = Fixtures.Generate(withCurrent: false).Record;
        Assert.Equal("code", rec.Edge("q:demo", "demo.fn.demo", "trigger")!["confidence"]!.ToString());
        Assert.Equal("DemoQueueHandler", rec.Edge("q:demo", "demo.fn.demo", "trigger")!["label"]!.ToString());

        // Code-attributed: send sites reachable from the Function method / the controllers, with file:line.
        var topic = rec.Edge("demo.fn.demo", "t:demo-topic", "publish")!;
        Assert.Equal(("code", "src/Demo.Application/DemoService.cs:23", "DemoChanged"),
            (topic["confidence"]!.ToString(), topic["sources"]![0]!.ToString(), topic["messages"]![0]!.ToString()));
        Assert.Equal("src/Demo.Api/Controllers/DemoController.cs:14", rec.Edge("demo.api.http", "q:demo", "publish")!["sources"]![0]!.ToString());
        // No sender class for the setting → app-level fallback; sends exist but unreachable from the app → fallback too.
        Assert.Equal("inferred-from-settings", rec.Edge("demo.fn.demo", "q:notification", "publish")!["confidence"]!.ToString());
        Assert.Equal("inferred-from-settings", rec.Edge("demo.api.http", "t:demo-topic", "publish")!["confidence"]!.ToString());
        // Sender class registered but never used to send → no edge (OQ-2); trigger-bound settings never fall back.
        Assert.Null(rec.Edge("demo.api.http", "q:demo-ingress", "publish"));
        Assert.Null(rec.Edge("demo.fn.ingress", "q:demo-snapshot", "publish"));
        // Self-send in code → selfRequeue badge, not an edge (I-1). The retry client given to the base is not drawn (OQ-4).
        Assert.Null(rec.Edge("demo.fn.demo", "q:demo", "publish"));
        var badge = rec.Item("handlers", "demo.fn.demo")["selfRequeue"]![0]!;
        Assert.Equal(("q:demo", "DemoFollowUp", "src/Demo.Application/DemoService.cs:26", true),
            (badge["entityId"]!.ToString(), badge["messages"]![0]!.ToString(), badge["sources"]![0]!.ToString(), badge["generated"]!.GetValue<bool>()));
        Assert.DoesNotContain(rec["edges"]!.AsArray(), e => e!["from"]!.ToString().StartsWith("app:") || e["to"]!.ToString().StartsWith("app:"));

        Assert.Equal("manual", rec.Edge("t:demo-topic", "s:demo-topic/local-inspect", "subscription")!["confidence"]!.ToString());
        Assert.Equal("local", rec.Edge("s:demo-topic/local-inspect", "q:demo-topic-inspect", "forward")!["scope"]!.ToString());
        var fwd = rec.Edge("s:demo-topic/demo-to-demo", "q:demo", "forward")!;
        Assert.Equal(("cloud", "terraform", "DemoReferenceChanged, DemoMoved"), (fwd["scope"]!.ToString(), fwd["confidence"]!.ToString(), fwd["label"]!.ToString()));
        Assert.Equal(11, rec["edges"]!.AsArray().Count); // 3 trigger + 4 publish + 2 local + 2 cloud subscription edges
    }

    [Fact]
    public void Current_record_overrides_manual_edges_and_extras_are_preserved()
    {
        var current = RegistrationGenerator.ReadCurrent(Fixtures.Current)!;
        var rec = Fixtures.Generate().Record;

        // overrides: byte-identical, and its structured entries are applied.
        Assert.True(JsonNode.DeepEquals(current["overrides"], rec["overrides"]));
        Assert.Equal("project", rec.Item("entities", "q:notification")["owner"]!.ToString());
        Assert.NotNull(rec.Edge("ext:partner", "q:demo-ingress", "inject"));

        // Manual edge kept; a hand label on a v1 app edge carries over to that app's handler edge; app edges are gone.
        Assert.Equal("seed kit", rec.Edge("ext:kit", "q:demo-ingress", "inject")!["label"]!.ToString());
        Assert.Equal("commands", rec.Edge("demo.api.http", "q:demo", "publish")!["label"]!.ToString());
        Assert.Null(rec.Edge("app:ingress", "q:demo-snapshot", "publish")); // v1 trigger-bound hack: not backed by code
        Assert.Null(rec.Edge("app:retired", "q:demo", "publish")); // derived edge no longer backed by sources

        // App extras: confirmed cloudRoleName and namingNote kept, derived fields refreshed.
        var ingress = rec.Item("apps", "app:ingress");
        Assert.Equal(("confirmed-role-from-ai", "hand note that must survive", "func-123-dev-nwe-acme-demo-ingress", 7102),
            (ingress["cloudRoleName"]!.ToString(), ingress["namingNote"]!.ToString(), ingress["azureResource"]!.ToString(), ingress["local"]!["port"]!.GetValue<int>()));
        Assert.DoesNotContain(rec["apps"]!.AsArray(), a => a!["id"]!.ToString() == "app:retired");

        // Entity extras kept, owned fields refreshed (naming.values from the record: 123/nwe/acme).
        var demo = rec.Item("entities", "q:demo");
        Assert.Equal(("kept", "sbq-123-dev-nwe-acme-shared-demo", 10), (demo["comment"]!.ToString(), demo["cloudName"]!.ToString(), demo["maxDeliveryCount"]!.GetValue<int>()));

        // Sections the generator does not own survive; sources are a union.
        Assert.Equal("appin-hand-picked", rec["observability"]!["appInsights"]!["resource"]!.ToString());
        Assert.Equal(3, rec["samples"]!["counts"]!["all"]!.GetValue<int>());
        Assert.EndsWith("demo.samples.json", rec["samples"]!["catalog"]!.ToString());
        Assert.Contains(rec["sources"]!.AsArray(), s => s!["path"]!.ToString() == "/somewhere/unprovisioned_shared-misc");
        Assert.Contains(rec["sources"]!.AsArray(), s => s!["kind"]!.ToString() == "run-script");
        Assert.Equal("123", rec["naming"]!["values"]!["project_code"]!.ToString());
        Assert.Equal("2026-11-02T15:00:00+01:00", rec["generatedAt"]!.ToString());
    }

    [Fact]
    public void Diff_summarises_changes_against_the_current_record()
    {
        var diff = Fixtures.Generate().Diff;
        Assert.Contains(diff, l => l.StartsWith("apps: 3 (+2 -1 ~1)"));
        Assert.Contains(diff, l => l.Contains("- app:retired"));
        Assert.Contains(diff, l => l.Contains("- q:gone"));
        Assert.Contains(diff, l => l.Contains("~ q:demo:") && l.Contains("cloudName \"stale\" → \"sbq-123-dev-nwe-acme-shared-demo\""));
        Assert.Contains(diff, l => l.Contains("+ demo.api.http -publish-> q:demo [both/code]"));
        Assert.Contains(diff, l => l.Contains("- app:api -publish-> q:demo"));
        Assert.Contains(diff, l => l.StartsWith("handlers: 4 (+4 -0 ~0)"));
        Assert.Contains(diff, l => l.StartsWith("containers: 3 (+3 -0 ~0)"));
        Assert.Contains("  overrides preserved", diff);
    }

    [Fact]
    public void Gaps_report_what_terraform_could_not_back()
    {
        var gaps = Fixtures.Generate().Gaps;
        Assert.Contains(gaps, g => g.Contains("ServiceBusDemoSubscription = 'demo-to-demo-subscription' matches no Terraform subscription name (Terraform has v2 'demo-to-demo')"));
        Assert.Contains(gaps, g => g.Contains("3 cloud subscription(s) link the project to other services"));
        Assert.Contains(gaps, g => g.Contains("app:ingress") && g.Contains("unconfirmed"));
        Assert.Contains(gaps, g => g.Contains("ServiceBusDemoIngressQueue → q:demo-ingress has a sender class but no send call") && g.Contains("OQ-2"));
        Assert.Contains(gaps, g => g.StartsWith("demo.fn.demo: IDemoQueueClient") && g.Contains("not drawn (OQ-4)"));
        Assert.Contains(gaps, g => g.Contains("none is reachable from app:api"));
        Assert.Equal(gaps.Count, gaps.Distinct().Count());
    }

    [Fact]
    public void External_subscriptions_are_opt_in()
    {
        var rec = Fixtures.Generate(external: true).Record;
        var consumer = rec.Item("entities", "s:demo-topic/other-service-to-demo"); // v1: other-service consumes the demo topic
        Assert.Equal("other-service-to-demo-subscription", consumer["cloudName"]!.ToString());
        Assert.Equal("consumer: other-service (v1)", rec.Edge("t:demo-topic", "s:demo-topic/other-service-to-demo", "subscription")!["label"]!.ToString());

        var foreignTopic = rec.Item("entities", "t:other-service");
        Assert.Equal(("n/a", "cloud", "shared"), (foreignTopic["localName"]!.ToString(), foreignTopic["scope"]!.ToString(), foreignTopic["owner"]!.ToString()));
        Assert.NotNull(rec.Edge("s:other-service/other-service-to-demo-ingr", "q:demo-ingress", "forward"));

        var sink = rec.Item("entities", "q:sink");
        Assert.Equal(("sbq-123-dev-nwe-acme-shared-sink", "cloud"), (sink["cloudName"]!.ToString(), sink["scope"]!.ToString()));
        Assert.Equal("sink", rec.Item("entities", "s:demo-topic/demo-to-sink")["cloudName"]!.ToString()); // v3 name = to
        Assert.DoesNotContain(rec["entities"]!.AsArray(), e => e!["id"]!.ToString().Contains("unrelated"));
    }

    [Fact]
    public void Generation_is_deterministic_and_idempotent()
    {
        var first = Fixtures.Generate();
        var again = RegistrationGenerator.Generate(Fixtures.Inputs(), first.Record, Fixtures.Now);
        Assert.Equal(first.ToJson(), again.ToJson());
        Assert.Contains(again.Diff, l => l == "apps: 3 (+0 -0 ~0)");
        Assert.Contains(again.Diff, l => l == "edges: 13 (+0 -0 ~0)");
        Assert.Contains(again.Diff, l => l == "handlers: 4 (+0 -0 ~0)");
    }

    [Fact]
    public void Missing_inputs_fail_with_a_clear_message()
    {
        var bad = new GeneratorInputs { Project = "nope", OverviewRoot = Fixtures.OverviewRoot, TerraformEnvDir = Fixtures.Tf };
        var ex = Assert.Throws<GeneratorInputException>(() => RegistrationGenerator.Generate(bad, null, Fixtures.Now));
        Assert.Contains("unprovisioned_app-nope", ex.Message);
    }

    [Fact]
    public void Writer_backs_up_the_previous_record_and_skips_identical_writes()
    {
        var dir = Directory.CreateTempSubdirectory("reggen-").FullName;
        try
        {
            var path = Path.Combine(dir, "demo.dev.json");
            File.WriteAllText(path, "{\"old\":true}");
            var result = Fixtures.Generate();
            var backup = RegistrationWriter.Write(path, result, Path.Combine(dir, "history"), Fixtures.Now);
            Assert.Equal("{\"old\":true}", File.ReadAllText(backup!));
            Assert.Equal(result.ToJson(), File.ReadAllText(path));
            Assert.Null(RegistrationWriter.Write(path, result, Path.Combine(dir, "history"), Fixtures.Now)); // unchanged → no backup
            Assert.Single(Directory.GetFiles(Path.Combine(dir, "history")));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Loaded_registration_path_comes_from_the_api_appsettings() =>
        Assert.Equal(Path.Combine(Fixtures.OverviewRoot, "architecture", "registrations", "customerproject.dev.json"),
            RegistrationWriter.LoadedRegistrationPath(Fixtures.OverviewRoot, "customerproject.dev"));
}

/// <summary>ADDENDA-008 validators on generator output (V-1..V-5, I-1 per §11) and the v1 → v2 upgrade.</summary>
public class ValidatorTests
{
    private static JsonObject WithOverride(Action<JsonObject> edit)
    {
        var current = RegistrationGenerator.ReadCurrent(Fixtures.Current)!;
        edit((JsonObject)current["overrides"]!);
        return current;
    }

    private static JsonObject InferredSelfSend() => new()
    {
        ["from"] = "demo.fn.snapshot", ["to"] = "q:demo-snapshot", ["kind"] = "publish", ["scope"] = "both", ["confidence"] = "inferred-from-settings",
    };

    [Fact]
    public void Inferred_self_send_fails_with_I1_UNRESOLVED_instead_of_becoming_a_badge()
    {
        var current = WithOverride(o => ((JsonArray)o["edges"]!).Add(InferredSelfSend()));
        var r = RegistrationGenerator.Generate(Fixtures.Inputs(), current, Fixtures.Now);
        Assert.False(r.IsValid);
        var issue = Assert.Single(r.Issues);
        Assert.Equal("I1_UNRESOLVED", issue.Code);
        Assert.Contains("demo.fn.snapshot", issue.Message);
        Assert.Null(r.Record.Item("handlers", "demo.fn.snapshot")["selfRequeue"]); // never a false badge
    }

    [Fact]
    public void AllowBidirectional_is_the_only_escape_hatch()
    {
        var current = WithOverride(o =>
        {
            ((JsonArray)o["edges"]!).Add(InferredSelfSend());
            o["allowBidirectional"] = new JsonArray(new JsonObject { ["entity"] = "q:demo-snapshot", ["node"] = "demo.fn.snapshot" });
        });
        var r = RegistrationGenerator.Generate(Fixtures.Inputs(), current, Fixtures.Now);
        Assert.Empty(r.Issues);
        Assert.NotNull(r.Record.Edge("demo.fn.snapshot", "q:demo-snapshot", "publish"));
    }

    [Theory]
    [InlineData("container:demo.func-app")]
    [InlineData("app:functions")]
    public void Edge_to_a_container_or_app_is_rejected(string target)
    {
        var current = WithOverride(o => ((JsonArray)o["edges"]!).Add(new JsonObject
        {
            ["from"] = "demo.fn.demo", ["to"] = target, ["kind"] = "publish", ["scope"] = "both", ["confidence"] = "manual",
        }));
        var r = RegistrationGenerator.Generate(Fixtures.Inputs(), current, Fixtures.Now);
        var issue = Assert.Single(r.Issues);
        Assert.Equal("V2_EDGE_ENDPOINT", issue.Code);
        Assert.Contains(target, issue.Message);
    }

    [Fact]
    public void V1_V3_V5_catch_broken_handlers()
    {
        var rec = Fixtures.Generate().Record;
        var h = rec.Item("handlers", "demo.fn.ingress");
        h["containerId"] = "container:demo.nope";
        rec.Item("handlers", "demo.fn.snapshot")["appId"] = "app:functions";
        var edges = rec["edges"]!.AsArray();
        edges.Remove(edges.OfType<JsonObject>().Single(e => RegistrationGenerator.EdgeKey(e) == "q:demo|demo.fn.demo|trigger"));
        var codes = RegistrationValidator.Check(rec).Select(i => i.Code).Order().ToList();
        Assert.Equal(["V1_UNKNOWN_CONTAINER", "V3_APP_NOT_IN_CONTAINER", "V5_MISSING_TRIGGER_EDGE"], codes);
    }

    [Fact]
    public void Logical_container_takes_its_members_out_of_the_resource_container()
    {
        var current = WithOverride(o => o["logicalContainers"] = new JsonArray(new JsonObject
        {
            ["id"] = "container:demo.domain", ["label"] = "Domain", ["resourceRefs"] = new JsonArray("app:functions", "app:ingress"),
            ["members"] = new JsonArray("demo.fn.demo"),
        }));
        var r = RegistrationGenerator.Generate(Fixtures.Inputs(), current, Fixtures.Now);
        Assert.Empty(r.Issues);
        Assert.Empty(r.SchemaErrors);
        Assert.Equal("container:demo.domain", r.Record.Item("handlers", "demo.fn.demo")["containerId"]!.ToString());
        Assert.Equal("logical", r.Record.Item("containers", "container:demo.domain")["kind"]!.ToString());
    }

    [Fact]
    public void V1_record_upgrades_without_false_self_requeues()
    {
        var v1 = RegistrationGenerator.ReadCurrent(Path.Combine(Fixtures.OverviewRoot, "test", "fixtures", "registration-v1", "customerproject.dev.v1.json"))!;
        var (v2, notes) = RegistrationUpgrade.ToV2(v1);
        Assert.Equal(2, v2["schemaVersion"]!.GetValue<int>());
        Assert.Empty(RegistrationValidator.Check(v2));
        Assert.Contains(notes, n => n.Contains("app:ingress → q:customerproject-snapshot") && n.Contains("customerproject.fn.snapshot"));
        Assert.All(v2["handlers"]!.AsArray(), h => Assert.Null(h!["selfRequeue"]));
        Assert.Null(v2.Edge("customerproject.fn.snapshot", "q:customerproject-snapshot", "publish"));
        Assert.Equal("inferred-from-settings", v2.Edge("customerproject.fn.snapshot", "q:customerproject-domain", "publish")!["confidence"]!.ToString());
    }

    [Fact]
    public void Handler_and_container_ids_follow_ADDENDA_008_section_3()
    {
        Assert.Equal("customerproject.fn.domain", HandlerNaming.HandlerId("customerproject", "CustomerProjectDomainQueueHandler", null));
        Assert.Equal("customerproject.fn.customer-project", HandlerNaming.HandlerId("customerproject", "CustomerProjectQueueHandler", null));
        Assert.Equal("customerproject.fn.commands", HandlerNaming.HandlerId("customerproject", "CustomerProjectQueueHandler",
            new JsonObject { ["handlerIds"] = new JsonObject { ["CustomerProjectQueueHandler"] = "customerproject.fn.commands" } }));
        Assert.Equal("customerproject.api.http", HandlerNaming.HttpHandlerId("customerproject", "app:api"));
        (string, string)[] apps = [("app:api", "web"), ("app:functions", "func"), ("app:ingress", "func-ingress")];
        Assert.Equal(["container:customerproject.api", "container:customerproject.func-app", "container:customerproject.ingress-app"],
            apps.Select(a => HandlerNaming.ContainerId("customerproject", a.Item1, a.Item2, apps)));
    }
}

/// <summary>The read-only C# index behind handler-level attribution (ADDENDA-008 §9.2).</summary>
public class CodeIndexTests
{
    [Fact]
    public void Same_class_name_in_two_namespaces_resolves_through_usings()
    {
        var index = CodeIndex.FromSources(new Dictionary<string, string>
        {
            ["src/M/Clients.cs"] = """
                namespace M;
                public class Settings { public string A { get; set; } public string B { get; set; } }
                static class Setup { static void Go(IConfiguration configuration) { new Settings { A = configuration.GetValue<string>("KeyA"), B = configuration.GetValue<string>("KeyB") }; } }
                public interface IAClient { } public class AClient : QueueClientBase, IAClient { public AClient(Settings settings) : base(f, settings.A) { } }
                public interface IBClient { } public class BClient : QueueClientBase, IBClient { public BClient(Settings settings) : base(f, settings.B) { } }
                """,
            ["src/One/Handler.cs"] = """
                using M;
                namespace One;
                public class Worker(IAClient a) { public async Task Handle() { await a.SendAsync(new FromOne()); } }
                """,
            ["src/Two/Handler.cs"] = """
                using M;
                namespace Two;
                public class Worker(IBClient b)
                {
                    public async Task Handle()
                    {
                        // await b.SendAsync(new Commented());
                        var s = "b.SendAsync(new InString())";
                        await b.SendAsync(new FromTwo());
                    }
                }
                """,
            ["src/Fn/Function.cs"] = """
                using One;
                namespace Fn;
                public class Fn(Lazy<Worker> worker)
                {
                    [Function("Fn")]
                    public async Task Run() => await worker.Value.Handle();
                }
                """,
        });
        var root = index.FunctionRoot("src/Fn/Function.cs", "Fn")!.Value;
        var site = Assert.Single(index.SitesFrom(root.Class, root.Method));
        Assert.Equal(("KeyA", "FromOne", "src/One/Handler.cs:3"), (site.ConfigKey, site.Message, $"{site.File}:{site.Line}"));
        Assert.Equal(["src/One/Handler.cs:3", "src/Two/Handler.cs:9"], index.AllSites.Select(s => $"{s.File}:{s.Line}").Order());
    }
}

/// <summary>Option C: layoutDefaults is never generator-owned; regeneration keeps it byte-for-byte.</summary>
public class LayoutDefaultsGeneratorTests
{
    [Fact]
    public void Regeneration_preserves_layoutDefaults_and_warns_on_unknown_ids()
    {
        var current = RegistrationGenerator.ReadCurrent(Fixtures.Current)!;
        var first = RegistrationGenerator.Generate(Fixtures.Inputs(), current, Fixtures.Now).Record;
        var withLayout = LayoutDefaults.Apply(first, [
            KeyValuePair.Create("container:demo.func-app", (400.4, 80.0)),
            KeyValuePair.Create("demo.fn.demo", (16.0, 40.0)),
            KeyValuePair.Create("app:gone", (1.0, 1.0)),
        ]);
        var again = RegistrationGenerator.Generate(Fixtures.Inputs(), withLayout, Fixtures.Now);
        Assert.True(again.IsValid);
        Assert.True(JsonNode.DeepEquals(withLayout["layoutDefaults"], again.Record["layoutDefaults"]));
        Assert.Equal(RegistrationGenerator.Serialize(withLayout), again.ToJson()); // no diff at all
        Assert.Contains(again.Gaps, g => g.Contains("layoutDefaults.positions: app:gone"));
        Assert.Equal(400, again.Record["layoutDefaults"]!["positions"]!["container:demo.func-app"]!["x"]!.GetValue<long>()); // whole pixels
        var keys = again.Record.Select(kv => kv.Key).ToList();
        Assert.Equal(keys.IndexOf("edges") + 1, keys.IndexOf("layoutDefaults"));
    }

    [Fact]
    public void Regeneration_preserves_container_appIds_like_other_hand_owned_fields()
    {
        // ADDENDA-010 §9 #26: containers[].appIds is hand-authored; the generator owns only id/label/kind/resourceRefs/scope.
        var current = RegistrationGenerator.ReadCurrent(Fixtures.Current)!;
        var first = RegistrationGenerator.Generate(Fixtures.Inputs(), current, Fixtures.Now).Record;
        var containers = first["containers"]!.AsArray().OfType<JsonObject>().ToList();
        Assert.NotEmpty(containers);
        containers[0]["appIds"] = new JsonArray("app:one", "app:two");          // multi-app
        if (containers.Count > 1) containers[1]["appIds"] = new JsonArray();     // explicitly none

        var again = RegistrationGenerator.Generate(Fixtures.Inputs(), first, Fixtures.Now);

        Assert.True(again.IsValid, string.Join("; ", again.SchemaErrors.Concat(again.Issues.Select(i => i.ToString()))));
        var regenerated = again.Record["containers"]!.AsArray().OfType<JsonObject>().ToList();
        Assert.True(JsonNode.DeepEquals(new JsonArray("app:one", "app:two"), regenerated[0]["appIds"]));
        if (containers.Count > 1) Assert.True(JsonNode.DeepEquals(new JsonArray(), regenerated[1]["appIds"]));
        Assert.Equal(RegistrationGenerator.Serialize(first), again.ToJson()); // no diff at all
    }

    [Fact]
    public void Apply_sorts_keys_and_is_idempotent()
    {
        var rec = new JsonObject { ["id"] = "x.dev", ["edges"] = new JsonArray(), ["observability"] = new JsonObject() };
        var once = LayoutDefaults.Apply(rec, [KeyValuePair.Create("q:b", (1.0, 2.0)), KeyValuePair.Create("q:a", (3.0, 4.0))]);
        Assert.Equal(["q:a", "q:b"], once["layoutDefaults"]!["positions"]!.AsObject().Select(kv => kv.Key));
        Assert.Equal(["id", "edges", "layoutDefaults", "observability"], once.Select(kv => kv.Key));
        var twice = LayoutDefaults.Apply(once, [KeyValuePair.Create("q:a", (3.0, 4.0)), KeyValuePair.Create("q:b", (1.0, 2.0))]);
        Assert.Equal(RegistrationGenerator.Serialize(once), RegistrationGenerator.Serialize(twice));
    }
}
