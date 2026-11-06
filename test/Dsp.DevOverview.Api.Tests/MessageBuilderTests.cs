using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dsp.DevOverview.Api.ServiceBus;
using Dsp.DevOverview.Catalog;

namespace Dsp.DevOverview.Api.Tests;

public class MessageBuilderTests
{
    private static readonly SampleCatalog Catalog = SampleCatalog.Load(RepoPaths.Catalog);
    private static readonly DateTime Now = new(2026, 11, 2, 10, 15, 30, DateTimeKind.Utc);

    private static SampleMessageBuilder Builder(string? profile = null) =>
        new(Catalog, () => Now, shardingBucket: () => 7, correlationId: () => "corr-1", commandPropertyProfile: profile);

    private static JsonElement Body(BuiltMessage m) => JsonDocument.Parse(m.Body).RootElement;
    private static Dictionary<string, object> Props(BuiltMessage m) => m.ApplicationProperties.ToDictionary(p => p.Key, p => p.Value);

    [Theory]
    [InlineData("ingress", 21)]
    [InlineData("commands", 2)]
    [InlineData("all", 23)]
    [InlineData("one", 1)]
    public void Mode_counts_match_the_kit(string mode, int count)
    {
        var built = Builder().Build(new SampleSendRequest(mode, Event: "CustomerChanged"));
        Assert.Equal(count, built.Count);
    }

    [Fact]
    public void All_is_ingress_then_commands()
    {
        var all = Builder().Build(new SampleSendRequest("all"));
        Assert.All(all.Take(21), m => Assert.Equal("q:customerproject-ingress", m.TargetEntityId));
        Assert.All(all.Skip(21), m => Assert.Equal("q:customerproject", m.TargetEntityId));
    }

    [Fact]
    public void Ingress_properties_are_DataSource_plus_injected_marker()
    {
        foreach (var m in Builder().Build(new SampleSendRequest("ingress")))
        {
            Assert.Equal("application/json", m.ContentType);
            Assert.Equal(["DataSource", SampleMessageBuilder.InjectedProperty], m.ApplicationProperties.Select(p => p.Key));
            Assert.Equal("M3Cloud", Props(m)["DataSource"]);
            Assert.Equal(true, Props(m)["x-overview-injected"]);
        }
    }

    [Fact]
    public void Commands_kit_profile_has_exactly_the_QueueClientBase_properties_of_the_kits_Core_Messaging_2_0_1()
    {
        var commands = Builder().Build(new SampleSendRequest("commands"));

        Assert.Equal(["SyncProjectDates", "CreateMissingProjects"], commands.Select(m => m.Subject));
        foreach (var m in commands)
        {
            Assert.Null(m.ContentType);
            Assert.Equal(["Dsp_ShardingBucketNumber", "Dsp_CorrelationContextId", "Dsp_IsM3Cloud", "DataSource", "x-overview-injected"],
                m.ApplicationProperties.Select(p => p.Key));
            var props = Props(m);
            Assert.Equal(7, props["Dsp_ShardingBucketNumber"]);
            Assert.Equal("corr-1", props["Dsp_CorrelationContextId"]);
            Assert.Equal(true, props["Dsp_IsM3Cloud"]);
            Assert.Equal("M3Cloud", props["DataSource"]);
            Assert.Equal(true, props["x-overview-injected"]);
            Assert.Equal("{\"Tenant\":2}", System.Text.Encoding.UTF8.GetString(m.Body));
        }
    }

    [Fact]
    public void Commands_addenda_profile_adds_Tenant_bools_and_OriginalEnqueuedTime()
    {
        var m = Builder("addenda").Build(new SampleSendRequest("commands"))[0];
        var props = Props(m);

        string[] tenants = ["TenantDahl", "TenantOptimera", "TenantFlisekompaniet", "TenantMonter", "TenantHywer", "TenantRencon", "TenantDahlDK", "TenantDahlFI", "TenantOptimeraSE"];
        Assert.Equal([.. tenants, "Dsp_ShardingBucketNumber", "Dsp_CorrelationContextId", "Dsp_IsM3Cloud", "DataSource", "Dsp_OriginalEnqueuedTime", "x-overview-injected"],
            m.ApplicationProperties.Select(p => p.Key));
        Assert.Equal(["TenantOptimera"], tenants.Where(t => (bool)props[t]));
        Assert.Equal(new DateTimeOffset(Now), DateTimeOffset.Parse((string)props["Dsp_OriginalEnqueuedTime"], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));
    }

    [Fact]
    public void Default_runtime_sharding_bucket_is_1_to_12_and_correlation_is_a_guid()
    {
        var builder = new SampleMessageBuilder(Catalog);
        for (var i = 0; i < 50; i++)
        {
            var props = Props(builder.Build(new SampleSendRequest("commands"))[0]);
            Assert.InRange((int)props["Dsp_ShardingBucketNumber"], 1, 12);
            Assert.True(Guid.TryParse((string)props["Dsp_CorrelationContextId"], out _));
        }
    }

    [Fact]
    public void Ingress_dataVersion_drives_envelope_version_and_LastModifiedUtc()
    {
        var built = Builder().Build(new SampleSendRequest("ingress", DataVersion: 5));

        var first = Body(built[0]).GetProperty("Envelope");
        Assert.Equal(5, first.GetProperty("DataVersion").GetInt64());
        Assert.Equal(Now.AddSeconds(5), first.GetProperty("LastModifiedUtc").GetDateTime());

        // renamed v6, stale v5, duplicate v6 with byte-identical body
        Assert.Equal([6L, 5L, 6L], built.Skip(18).Select(m => Body(m).GetProperty("Envelope").GetProperty("DataVersion").GetInt64()));
        Assert.Equal(built[18].Body, built[20].Body);
        Assert.Equal("Bygg og Montasje Nordre Aker AS", Body(built[19]).GetProperty("Name").GetString());
    }

    [Fact]
    public void Date_fields_are_relative_to_now()
    {
        var address = Builder().Build(new SampleSendRequest("one", "CustomerAddressChanged"))[0];
        Assert.Equal(SampleMessageBuilder.YyyyMmDd(Now.AddMonths(-6)), Body(address).GetProperty("ValidFrom").GetInt32());
        Assert.Equal(SampleMessageBuilder.YyyyMmDd(Now.AddMonths(18)), Body(address).GetProperty("ValidTo").GetInt32());
    }

    [Fact]
    public void One_suffixes_name_and_project_name_with_the_version_for_the_chosen_project()
    {
        var customer = Builder().Build(new SampleSendRequest("one", "CustomerChanged", 9, 2)).Single();
        Assert.Equal("CustomerChanged", customer.Subject);
        Assert.Equal("Fjordbyen Rør og Varme AS (v9)", Body(customer).GetProperty("Name").GetString());
        Assert.Equal(9, Body(customer).GetProperty("Envelope").GetProperty("DataVersion").GetInt64());

        var address = Builder().Build(new SampleSendRequest("one", "CustomerAddressChanged", 9, 3)).Single();
        Assert.Equal("Lade Allé 3, rehab (v9)", Body(address).GetProperty("YourReference1").GetString());

        var header = Builder().Build(new SampleSendRequest("one", "HeaderFileChanged", 9, 1)).Single();
        Assert.Equal("100|H1000231871", Body(header).GetProperty("Envelope").GetProperty("Id").GetString());
    }

    [Fact]
    public void ApplicationPropertiesOverride_merges_over_kit_props_and_injected_marker_stays()
    {
        var m = Builder().Build(new SampleSendRequest("one", "CustomerChanged",
            ApplicationPropertiesOverride: new Dictionary<string, object> { ["DataSource"] = "IonCloud", ["Extra"] = 42 })).Single();

        Assert.Equal(["DataSource", "Extra", "x-overview-injected"], m.ApplicationProperties.Select(p => p.Key));
        Assert.Equal("IonCloud", Props(m)["DataSource"]);
        Assert.Equal(42, Props(m)["Extra"]);
    }

    [Fact]
    public void BodyOverride_replaces_body_only()
    {
        var m = Builder().Build(new SampleSendRequest("one", "CustomerChanged", BodyOverride: JsonNode.Parse("""{"hello":"world"}"""))).Single();
        Assert.Equal("""{"hello":"world"}""", System.Text.Encoding.UTF8.GetString(m.Body));
        Assert.Equal("CustomerChanged", m.Subject);
        Assert.Equal("M3Cloud", Props(m)["DataSource"]);
    }

    [Fact]
    public void TargetEntityId_override_retargets()
    {
        var m = Builder().Build(new SampleSendRequest("one", "CustomerChanged", TargetEntityId: "q:customerproject-topic-inspect")).Single();
        Assert.Equal("q:customerproject-topic-inspect", m.TargetEntityId);
    }

    [Theory]
    [InlineData("bogus", null, null, null, "INVALID_MODE")]
    [InlineData("one", "NotAnEvent", null, null, "UNKNOWN_EVENT")]
    [InlineData("one", "CustomerChanged", 0, null, "INVALID_DATA_VERSION")]
    [InlineData("one", "CustomerChanged", 1, 4, "INVALID_PROJECT_INDEX")]
    [InlineData("purge", null, null, null, "PURGE_NOT_A_SEND")]
    public void Invalid_requests_are_rejected_with_a_code(string mode, string? ev, int? dv, int? project, string code)
    {
        var ex = Assert.Throws<SampleRequestException>(() => Builder().Build(new SampleSendRequest(mode, ev, dv, project)));
        Assert.Equal(code, ex.Code);
    }

    [Fact]
    public void BodyOverride_on_a_multi_message_mode_is_rejected()
    {
        var ex = Assert.Throws<SampleRequestException>(() => Builder().Build(new SampleSendRequest("ingress", BodyOverride: JsonNode.Parse("{}"))));
        Assert.Equal("BODY_OVERRIDE_REQUIRES_SINGLE", ex.Code);
    }

    [Fact]
    public void ServiceBusMessage_mapping_keeps_subject_contenttype_props_and_bytes()
    {
        var built = Builder().Build(new SampleSendRequest("all"));
        var ingress = SbGateway.ToServiceBusMessage(built[0]);
        Assert.Equal("CustomerChanged", ingress.Subject);
        Assert.Equal("application/json", ingress.ContentType);
        Assert.Equal(built[0].Body, ingress.Body.ToArray());
        Assert.Equal(true, ingress.ApplicationProperties["x-overview-injected"]);

        var command = SbGateway.ToServiceBusMessage(built[21]);
        Assert.Null(command.ContentType);
        Assert.Equal(true, command.ApplicationProperties["Dsp_IsM3Cloud"]);
    }
}
