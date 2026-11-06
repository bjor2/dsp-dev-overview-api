using Dsp.DevOverview.RegistrationGenerator;

namespace Dsp.DevOverview.RegistrationGenerator.Tests;

public class HclTests
{
    [Fact]
    public void Parses_literals_objects_tuples_comments_and_keeps_expressions_as_text()
    {
        var doc = Hcl.Parse("""
            # header comment
            locals {
              a = "x-${module.naming.servicebus_queue.name}-\"q\""   // trailing
              n = 42
              f = 1.5
              b = false
              z = null
              list = ["1", 2, { k = "v" },]
              call = format("%s/%s", data.x.outputs["k"].uri, "y") # comment after expression
              cond = var.enabled ? "a" : "b"
              /* block */ nested = { inner = { deep = true } }
              "quoted-key" = "q"
              doc = <<-EOT
                line one
                line two
              EOT
            }
            locals {
              second = "merged"
            }
            """);
        var l = (Dictionary<string, object?>)doc["locals"]!;
        Assert.Equal("x-${module.naming.servicebus_queue.name}-\"q\"", l["a"]);
        Assert.IsType<long>(l["n"]);
        Assert.Equal(42L, l["n"]);
        Assert.Equal(1.5, l["f"]);
        Assert.Equal(false, l["b"]);
        Assert.Null(l["z"]);
        var list = (List<object?>)l["list"]!;
        Assert.Equal(3, list.Count);
        Assert.Equal("v", ((Dictionary<string, object?>)list[2]!)["k"]);
        Assert.Equal(new HclExpr("format(\"%s/%s\", data.x.outputs[\"k\"].uri, \"y\")"), l["call"]);
        Assert.Equal(new HclExpr("var.enabled ? \"a\" : \"b\""), l["cond"]);
        Assert.Equal(true, ((Dictionary<string, object?>)((Dictionary<string, object?>)l["nested"]!)["inner"]!)["deep"]);
        Assert.Equal("q", l["quoted-key"]);
        Assert.Contains("line two", (string)l["doc"]!);
        Assert.Equal("merged", l["second"]);
    }

    [Fact]
    public void Labeled_blocks_nest_by_label()
    {
        var doc = Hcl.Parse("""module "app" { source = "git::x" }""");
        Assert.Equal("git::x", ((Dictionary<string, object?>)((Dictionary<string, object?>)doc["module"]!)["app"]!)["source"]);
    }

    [Fact]
    public void Reports_the_line_of_a_syntax_error()
    {
        var ex = Assert.Throws<FormatException>(() => Hcl.Parse("locals {\n  a = \"x\"\n  b \n}"));
        Assert.Contains("line 3", ex.Message);
    }
}

public class TerraformReaderTests
{
    private static readonly TfNaming Naming = new("123", "dev", "nwe", "acme");

    [Fact]
    public void App_settings_are_read_from_the_named_local()
    {
        var web = TerraformReader.ReadAppSettings(Path.Combine(Fixtures.Tf, "unprovisioned_app-demo", "locals-web.tf"), "web_app");
        Assert.Equal("${module.naming.servicebus_queue.name}-shared-demo", web["ServiceBusDemoQueue"]);
        Assert.StartsWith("format(", web["ServiceBusConnectionString"]);
        Assert.Equal(6, web.Count);
        Assert.Throws<FormatException>(() => TerraformReader.ReadAppSettings(Path.Combine(Fixtures.Tf, "unprovisioned_app-demo", "locals-web.tf"), "function_app"));
    }

    [Fact]
    public void ServiceBus_settings_resolve_to_short_and_cloud_names()
    {
        var settings = TerraformReader.ServiceBusSettings(
            TerraformReader.ReadAppSettings(Path.Combine(Fixtures.Tf, "unprovisioned_app-demo", "locals-function.tf"), "function_app"), Naming)
            .ToDictionary(s => s.Key);
        Assert.Equal(5, settings.Count); // only ServiceBus* keys
        Assert.Equal((SettingKind.Queue, "demo", "sbq-123-dev-nwe-acme-shared-demo"),
            (settings["ServiceBusDemoQueue"].Kind, settings["ServiceBusDemoQueue"].ShortName, settings["ServiceBusDemoQueue"].CloudName));
        Assert.Equal((SettingKind.Topic, "demo", "sbt-123-dev-nwe-acme-shared-demo"),
            (settings["ServiceBusDemoTopic"].Kind, settings["ServiceBusDemoTopic"].ShortName, settings["ServiceBusDemoTopic"].CloudName));
        Assert.Equal((SettingKind.Subscription, "demo-to-demo-subscription"),
            (settings["ServiceBusDemoSubscription"].Kind, settings["ServiceBusDemoSubscription"].ShortName));
        Assert.Equal(SettingKind.Connection, settings["ServiceBusConnectionString"].Kind);
        Assert.Equal("notification", settings["ServiceBusNotificationQueue"].ShortName);
    }

    [Fact]
    public void Shared_message_queues_topics_and_subscriptions_follow_r_servicebus_entities()
    {
        var sb = TerraformReader.ReadServiceBus(Path.Combine(Fixtures.Tf, "unprovisioned_shared-message"));
        Assert.Equal(["demo", "demo-ingress", "demo-snapshot", "notification", "sink"], sb.Queues.Select(q => q.Name));
        Assert.Equal(3, sb.Queue("demo")!.MaxDeliveryCount);         // try(max_delivery_count, 3)
        Assert.Equal(5, sb.Queue("demo-ingress")!.MaxDeliveryCount);
        Assert.True(sb.HasTopic("other-service"));
        Assert.Equal(5, sb.Subscriptions.Count);

        var v2 = sb.Subscriptions[0];
        Assert.Equal((2, "demo-to-demo", "demo", "demo"), (v2.Version, v2.Name, v2.Topic, v2.ForwardToQueue));
        var v1 = sb.Subscriptions[1];
        Assert.Equal((1, "other-service-to-demo-subscription", "demo"), (v1.Version, v1.Name, v1.Topic)); // v1 lives on topic `to`
        var v3 = sb.Subscriptions[3];
        Assert.Equal((3, "sink", "demo"), (v3.Version, v3.Name, v3.Topic));
        Assert.Equal("DataSource = '${local.some_filter}'", sb.Subscriptions[2].SqlFilter);
    }

    [Theory]
    [InlineData("(sys.label = 'A' OR sys.Label = 'B')", "A, B")]
    [InlineData("sys.Label = 'ItemCampaignsChanged'", "ItemCampaignsChanged")]
    [InlineData("Dsp_IsM3Cloud = true", null)]
    [InlineData(null, null)]
    public void Edge_labels_come_from_label_filters(string? filter, string? expected) =>
        Assert.Equal(expected, TerraformReader.LabelsFromFilter(filter));

    [Fact]
    public void Naming_formula_matches_ADDENDA_001()
    {
        var n = new TfNaming();
        Assert.Equal("sbq-870-dev-euw-dahl-shared-customerproject", n.QueueName("customerproject"));
        Assert.Equal("sbt-870-dev-euw-dahl-shared-customerproject", n.TopicName("customerproject"));
        Assert.Equal("web-870-dev-euw-dahl-customerproject", n.AppResource("web", "customerproject"));
    }
}

public class TriggerScannerTests
{
    [Fact]
    public void Scans_only_Functions_folders_and_pairs_each_trigger_with_its_function()
    {
        var functions = TriggerScanner.Scan(Path.Combine(Fixtures.Repo, "src", "Demo.Functions"), Fixtures.Repo);
        var t = Assert.Single(functions); // Other/NotScanned.cs is outside a Functions folder; Helper.cs has none
        Assert.Equal(new FoundTrigger("DemoQueueHandler", "ServiceBusDemoQueue", null, "ServiceBusConnectionString",
            "src/Demo.Functions/Functions/DemoQueueHandler.cs"), t);

        var ingress = TriggerScanner.Scan(Path.Combine(Fixtures.Repo, "src", "Demo.Ingress.Functions"), Fixtures.Repo);
        Assert.Equal(["IngressQueueHandler", "SnapshotQueueHandler"], ingress.Select(x => x.Function)); // [Function(nameof(...))] too
        Assert.Equal(["ServiceBusDemoIngressQueue", "ServiceBusDemoSnapshotQueue"], ingress.Select(x => x.Setting)); // multi-line attribute
        Assert.All(ingress, x => Assert.Equal("ServiceBusNamespace", x.Connection));
    }

    [Fact]
    public void Literal_queue_names_and_missing_function_attributes()
    {
        var found = TriggerScanner.ScanSource("""
            public class LiteralHandler
            {
                public void Run([ServiceBusTrigger("my-queue")] string body) { }
            }
            """, "x.cs").Single();
        Assert.Equal(("LiteralHandler", null, "my-queue", null), (found.Function, found.Setting, found.LiteralName, found.Connection));
    }

    [Fact]
    public void Missing_project_folder_scans_nothing() =>
        Assert.Empty(TriggerScanner.Scan(Path.Combine(Fixtures.Repo, "src", "Nope"), Fixtures.Repo));
}

public class LocalDevReaderTests
{
    [Fact]
    public void Config_json_queues_topics_and_subscriptions()
    {
        var c = LocalDevReader.ReadConfig(Path.Combine(Fixtures.Local, "Config.json"));
        Assert.Equal(5, c.Queues.Count);
        Assert.Equal(7, c.Queues.Single(q => q.Name == "notification").MaxDeliveryCount);
        var sub = Assert.Single(Assert.Single(c.Topics).Subscriptions);
        Assert.Equal(("local-inspect", "demo-topic-inspect", 10), (sub.Name, sub.ForwardTo, sub.MaxDeliveryCount));
    }

    [Fact]
    public void Env_script_maps_settings_to_emulator_names()
    {
        var env = LocalDevReader.ReadEnvSettings(Path.Combine(Fixtures.Local, "env.linux.ps1"));
        Assert.Equal("demo-topic", env["ServiceBusDemoTopic"]);
        Assert.False(env.ContainsKey("ServiceBusConnectionString")); // $sb is not a literal
        Assert.Empty(LocalDevReader.ReadEnvSettings(Path.Combine(Fixtures.Local, "missing.ps1")));
    }

    [Fact]
    public void Run_scripts_give_apps_ports_and_urls_linux_variant_first()
    {
        var apps = LocalDevReader.ReadRunScripts(Fixtures.Local, Fixtures.Repo);
        Assert.Equal(["api", "functions", "ingress"], apps.Select(a => a.Name));
        var api = apps[0];
        Assert.Equal(("Demo.Api", false, 6011, "https://localhost:6011/swagger"), (api.ProjectFolder, api.IsFunctionHost, api.Port, api.Url));
        Assert.EndsWith("run-api.linux.ps1", api.RunScript);
        Assert.Equal(("Demo.Functions", true, 7101), (apps[1].ProjectFolder, apps[1].IsFunctionHost, apps[1].Port));
        Assert.Equal(7102, apps[2].Port);
    }
}
