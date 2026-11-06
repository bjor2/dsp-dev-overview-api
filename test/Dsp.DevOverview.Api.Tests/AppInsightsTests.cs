using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Azure;
using Azure.Identity;
using Dsp.DevOverview.Api.AppInsights;
using Dsp.DevOverview.Api.Registration;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dsp.DevOverview.Api.Tests;

/// <summary>Records every query; returns canned rows or throws the configured exception. No Azure.</summary>
public sealed class FakeLogsQuery : ILogsQuery
{
    public readonly List<(string ResourceId, string Kql, DateTimeOffset From)> Calls = [];
    public IReadOnlyList<LogRow> Rows { get; set; } = [];
    public Exception? Throw { get; set; }

    public Task<IReadOnlyList<LogRow>> QueryResourceAsync(string resourceId, string kql, DateTimeOffset from, CancellationToken ct)
    {
        lock (Calls) Calls.Add((resourceId, kql, from));
        return Throw is { } ex ? Task.FromException<IReadOnlyList<LogRow>>(ex) : Task.FromResult(Rows);
    }

    public static LogRow Row(params (string Column, object? Value)[] values) => new(values.ToDictionary(v => v.Column, v => v.Value));
}

public static class AiFixture
{
    public const string DevSubscriptionId = "2562d3de-c461-4bd9-81a0-2b8aba52c0eb";
    public static readonly DateTimeOffset Now = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);

    public static readonly AzureProfile Profile = new([
        new("d19b2706-86aa-47b7-ac8a-303a30d06f18", "870Z-DAHL-DigitalServicePlatform-West-Europe-PPR", "tenant-1", true),
        new(DevSubscriptionId, "870Z-DAHL-DigitalServicePlatform-West-Europe-DEV", "tenant-1", false),
    ]);

    public static readonly string[] Roles =
        ["web-870-dev-euw-dahl-customerproject", "func-870-dev-euw-dahl-customerproject", "func-870-dev-euw-dahl-customerproject-ingress"];

    public static OverviewProject Project() =>
        new(new RegistrationLoader(RepoPaths.Schema).Load(RepoPaths.Registration), null, null, null);

    public static LogsService Service(FakeLogsQuery fake, AppInsightsOptions? options = null, OverviewProject? project = null) =>
        new(new ProjectStore([project ?? Project()]), fake, options ?? new AppInsightsOptions(), Profile,
            NullLogger<LogsService>.Instance, new FixedClock(Now));

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}

public class KqlBuilderTests
{
    private static readonly AppInsightsOptions Options = new();

    [Fact]
    public void Logs_query_unions_the_four_tables_filters_time_and_roles_and_projects_the_LogEvent_columns()
    {
        var kql = KqlBuilder.BuildLogsQuery(AiFixture.Roles, new DateTimeOffset(2026, 11, 2, 11, 41, 0, TimeSpan.Zero), null, 200);
        var lines = kql.Split('\n');

        Assert.Equal("union withsource=SourceTable requests, traces, exceptions, dependencies", lines[0]);
        Assert.Equal("| where timestamp > datetime(2026-11-02T11:41:00.0000000Z)", lines[1]);
        Assert.Equal("| where cloud_RoleName in (\"web-870-dev-euw-dahl-customerproject\", \"func-870-dev-euw-dahl-customerproject\", \"func-870-dev-euw-dahl-customerproject-ingress\")", lines[2]);
        Assert.Equal("| top 200 by timestamp desc", lines[3]);
        Assert.StartsWith("| project timestamp, SourceTable, cloud_RoleName, severityLevel = column_ifexists(\"severityLevel\", int(null))", lines[4]);
        Assert.Equal("| order by timestamp asc", lines[^1]);
    }

    [Fact]
    public void No_column_or_identifier_is_named_last()
    {
        var kql = KqlBuilder.BuildLogsQuery(AiFixture.Roles, AiFixture.Now, "where severityLevel >= 3", 10) + KqlBuilder.BuildRolesQuery(AiFixture.Now);
        Assert.DoesNotMatch(new Regex(@"\blast\b", RegexOptions.IgnoreCase), kql);
    }

    [Fact]
    public void Fragment_goes_after_the_role_filter_and_before_top()
    {
        var lines = KqlBuilder.BuildLogsQuery(AiFixture.Roles, AiFixture.Now, "where severityLevel >= 3", 50).Split('\n');
        Assert.StartsWith("| where cloud_RoleName in", lines[2]);
        Assert.Equal("| where severityLevel >= 3", lines[3]);
        Assert.Equal("| top 50 by timestamp desc", lines[4]);
    }

    [Fact]
    public void Roles_query_is_the_ADR_confirmation_query()
    {
        Assert.Equal(
            "union requests, traces, exceptions, dependencies\n| where timestamp > datetime(2026-11-01T12:00:00.0000000Z)\n| summarize itemCount = count() by cloud_RoleName\n| order by cloud_RoleName asc",
            KqlBuilder.BuildRolesQuery(AiFixture.Now.AddDays(-1)));
    }

    [Theory]
    [InlineData("2026-11-02T11:41:00Z", "2026-11-02T11:41:00.0000000Z")]
    [InlineData("2026-11-02T12:41:00+01:00", "2026-11-02T11:41:00.0000000Z")]   // offset → UTC
    [InlineData("2026-11-02T11:41:00", "2026-11-02T11:41:00.0000000Z")]         // no offset = UTC
    [InlineData("2026-11-02T11:41:00.1234567Z", "2026-11-02T11:41:00.1234567Z")] // tail cursor keeps full precision
    [InlineData("30m", "2026-11-02T11:30:00.0000000Z")]
    [InlineData("2h", "2026-11-02T10:00:00.0000000Z")]
    [InlineData("1d", "2026-11-01T12:00:00.0000000Z")]
    public void Since_parses_to_a_utc_datetime_literal(string since, string expected)
    {
        var from = KqlBuilder.ParseSince(since, AiFixture.Now, Options);
        Assert.Equal($"datetime({expected})", KqlBuilder.DateTimeLiteral(from));
        Assert.Contains($"timestamp > datetime({expected})", KqlBuilder.BuildLogsQuery(AiFixture.Roles, from, null, 1));
    }

    [Fact]
    public void Since_defaults_to_the_configured_lookback()
    {
        Assert.Equal(AiFixture.Now.AddMinutes(-30), KqlBuilder.ParseSince(null, AiFixture.Now, Options));
        Assert.Equal(AiFixture.Now.AddMinutes(-5), KqlBuilder.ParseSince("", AiFixture.Now, new AppInsightsOptions { DefaultLookbackMinutes = 5 }));
    }

    [Theory]
    [InlineData("yesterday")]
    [InlineData("2026-11-02T11:41:00Z) | take 1")]  // injection attempt: not a date, never reaches the query
    [InlineData("8d")]                               // older than MaxLookbackDays (7)
    [InlineData("2026-11-03T00:00:00Z")]             // future
    public void Invalid_since_is_400_INVALID_SINCE(string since)
    {
        var ex = Assert.Throws<ApiException>(() => KqlBuilder.ParseSince(since, AiFixture.Now, Options));
        Assert.Equal((400, "INVALID_SINCE"), (ex.Status, ex.Code));
    }

    [Fact]
    public void Roles_default_to_all_registered_and_accept_a_comma_separated_subset()
    {
        Assert.Equal(AiFixture.Roles, KqlBuilder.ParseRoles(null, AiFixture.Roles));
        Assert.Equal(AiFixture.Roles, KqlBuilder.ParseRoles(" , ", AiFixture.Roles));
        Assert.Equal([AiFixture.Roles[2], AiFixture.Roles[0]], KqlBuilder.ParseRoles($" {AiFixture.Roles[2]} ,{AiFixture.Roles[0]},{AiFixture.Roles[2]}", AiFixture.Roles));
    }

    [Theory]
    [InlineData("web-870\") or true or (\"x", "INVALID_ROLE")]
    [InlineData("web 870", "INVALID_ROLE")]
    [InlineData("-leading-dash", "INVALID_ROLE")]
    [InlineData("func-870-dev-euw-dahl-otherproject", "UNKNOWN_ROLE")]
    public void Bad_roles_are_rejected(string roles, string code)
    {
        var ex = Assert.Throws<ApiException>(() => KqlBuilder.ParseRoles(roles, AiFixture.Roles));
        Assert.Equal((400, code), (ex.Status, ex.Code));
    }

    [Theory]
    [InlineData("where severityLevel >= 3", "where severityLevel >= 3")]
    [InlineData("  | where operation_Id == \"4bf9\"  ", "where operation_Id == \"4bf9\"")]
    [InlineData("where customDimensions.Category == \"Function\"", "where customDimensions.Category == \"Function\"")]
    [InlineData("where duration > 0.5 and url has \"app\"", "where duration > 0.5 and url has \"app\"")]
    [InlineData("where message has \"https://x\" // comment", "where message has \"https://x\" // comment")]
    public void Safe_fragments_pass_through(string input, string expected) =>
        Assert.Equal(expected, KqlBuilder.SanitizeFragment(input));

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    [InlineData("|")]
    public void Empty_fragment_is_null(string? input) => Assert.Null(KqlBuilder.SanitizeFragment(input));

    [Theory]
    [InlineData("take 1; .drop table requests")]
    [InlineData("where 1 == 1\n| take 5")]
    [InlineData("where 1 == 1\r| take 5")]
    [InlineData(".set-or-append t <| requests")]
    [InlineData("where x == 1 | .drop table traces")]
    [InlineData("where x == 1 (.show tables)")]
    [InlineData("union *")]
    [InlineData("join kind=inner (requests) on operation_Id")]
    [InlineData("where x in (workspace(\"other\").AppTraces | project x)")]
    [InlineData("where x in (app ('other').traces | project x)")]
    [InlineData("evaluate http_request('https://example.com')")]
    [InlineData("where message == ```multi```")]
    public void Dangerous_fragments_are_400_KQL_REJECTED(string input)
    {
        var ex = Assert.Throws<ApiException>(() => KqlBuilder.SanitizeFragment(input));
        Assert.Equal((400, "KQL_REJECTED"), (ex.Status, ex.Code));
    }

    [Fact]
    public void Overlong_fragment_is_rejected()
    {
        var ex = Assert.Throws<ApiException>(() => KqlBuilder.SanitizeFragment("where message has \"" + new string('x', 1000) + "\""));
        Assert.Equal("KQL_REJECTED", ex.Code);
    }
}

public class AppInsightsErrorMappingTests
{
    public static TheoryData<Exception, bool, int, string, bool> Cases => new()
    {
        { new CredentialUnavailableException("DefaultAzureCredential failed to retrieve a token"), false, 503, "AI_AUTH_FAILED", false },
        { new AuthenticationFailedException("AADSTS700082: refresh token expired"), false, 503, "AI_AUTH_FAILED", false },
        { new RequestFailedException(401, "InvalidAuthenticationToken"), false, 502, "AI_AUTH_FAILED", false },
        { new RequestFailedException(403, "AuthorizationFailed"), false, 502, "AI_FORBIDDEN", false },
        { new RequestFailedException(404, "ResourceNotFound"), false, 502, "AI_RESOURCE_NOT_FOUND", false },
        { new RequestFailedException(400, "SemanticError"), true, 400, "KQL_INVALID", false },
        { new RequestFailedException(400, "SemanticError"), false, 502, "AI_QUERY_FAILED", false },
        { new RequestFailedException(429, "TooManyRequests"), false, 503, "AI_THROTTLED", true },
        { new RequestFailedException(503, "ServiceUnavailable"), false, 502, "AI_UPSTREAM_ERROR", true },
        { new RequestFailedException("Name or service not known"), false, 503, "AI_UNREACHABLE", true },
        { new HttpRequestException("Connection refused"), false, 503, "AI_UNREACHABLE", true },
        { new TaskCanceledException("timeout"), false, 503, "AI_UNREACHABLE", true },
        { new InvalidOperationException("boom"), false, 502, "AI_QUERY_FAILED", false },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Azure_failures_map_to_the_error_envelope(Exception ex, bool userKql, int status, string code, bool retryable)
    {
        var mapped = AppInsightsErrors.Map(ex, userKql);
        Assert.Equal((status, code, retryable), (mapped.Status, mapped.Code, mapped.Retryable));
        Assert.False(string.IsNullOrWhiteSpace(mapped.Message));
    }

    [Fact]
    public void Auth_failures_tell_the_developer_how_to_sign_in()
    {
        Assert.Contains("az login", AppInsightsErrors.Map(new CredentialUnavailableException("x"), false).Message);
    }
}

public class LogEventMapperTests
{
    private static readonly DateTimeOffset T = new(2026, 11, 2, 11, 41, 0, 123, TimeSpan.Zero);

    [Fact]
    public void Trace_row_maps_message_severity_and_operation()
    {
        var e = LogEventMapper.Map(FakeLogsQuery.Row(("timestamp", T.AddTicks(4567)), ("SourceTable", "traces"),
            ("cloud_RoleName", "func-870-dev-euw-dahl-customerproject"), ("severityLevel", 1L),
            ("message", "Executing 'Functions.CustomerProjectQueueHandler'"), ("operation_Id", "4bf9"), ("name", ""), ("itemCount", 1L)));

        Assert.Equal("2026-11-02T11:41:00.1234567Z", e.Timestamp);
        Assert.Equal(("traces", "func-870-dev-euw-dahl-customerproject", 1), (e.Table, e.CloudRoleName, e.Severity));
        Assert.Equal("Executing 'Functions.CustomerProjectQueueHandler'", e.Message);
        Assert.Equal("4bf9", e.OperationId);
        Assert.Null(e.Name);       // empty string → omitted
        Assert.Null(e.Details);    // itemCount 1 is not interesting
    }

    [Fact]
    public void Exception_row_uses_short_type_and_outer_message_and_keeps_full_type_in_details()
    {
        var e = LogEventMapper.Map(FakeLogsQuery.Row(("timestamp", T), ("SourceTable", "exceptions"),
            ("cloud_RoleName", "func-870-dev-euw-dahl-customerproject-ingress"), ("severityLevel", 3L),
            ("type", "Microsoft.Data.SqlClient.SqlException"), ("outerMessage", "Login failed"), ("message", ""),
            ("problemId", "SqlException at Foo"), ("itemCount", 8L)));

        Assert.Equal("SqlException: Login failed", e.Message);
        Assert.Equal(3, e.Severity);
        Assert.Equal("Microsoft.Data.SqlClient.SqlException", e.Details!["type"]);
        Assert.Equal("SqlException at Foo", e.Details["problemId"]);
        Assert.Equal(8L, e.Details["itemCount"]); // 12.5 % sampling weight
    }

    [Fact]
    public void Request_row_maps_name_result_duration_and_success()
    {
        var e = LogEventMapper.Map(FakeLogsQuery.Row(("timestamp", T), ("SourceTable", "requests"),
            ("cloud_RoleName", "web-870-dev-euw-dahl-customerproject"), ("name", "GET /api/projects"), ("resultCode", "500"),
            ("duration", 12.5), ("success", false), ("url", "https://web/api/projects"), ("severityLevel", null)));

        Assert.Equal("GET /api/projects → 500", e.Message);
        Assert.Equal(("GET /api/projects", 12.5, false), (e.Name, e.DurationMs, e.Success));
        Assert.Null(e.Severity);
        Assert.Equal("https://web/api/projects", e.Details!["url"]);
        Assert.Equal("500", e.Details["resultCode"]);
    }

    [Fact]
    public void Dependency_row_maps_type_target_and_string_success()
    {
        var dims = JsonDocument.Parse("""{"Category":"Host.Results"}""").RootElement;
        var e = LogEventMapper.Map(FakeLogsQuery.Row(("timestamp", T), ("SourceTable", "dependencies"),
            ("cloud_RoleName", "func-870-dev-euw-dahl-customerproject"), ("type", "Azure Service Bus"),
            ("target", "sb-870-dev-euw-dahl-shared"), ("name", "Send"), ("resultCode", "0"), ("success", "True"),
            ("data", "sbq-870-dev-euw-dahl-shared-customerproject"), ("customDimensions", dims)));

        Assert.Equal("[Azure Service Bus] sb-870-dev-euw-dahl-shared Send → 0", e.Message);
        Assert.True(e.Success);
        Assert.Equal("Azure Service Bus", e.Details!["type"]);
        Assert.Equal("sbq-870-dev-euw-dahl-shared-customerproject", e.Details["data"]);
        Assert.Equal("Host.Results", ((JsonElement)e.Details["customDimensions"]!).GetProperty("Category").GetString());
    }

    [Fact]
    public void Serialized_LogEvent_matches_the_contract_shape()
    {
        var e = LogEventMapper.Map(FakeLogsQuery.Row(("timestamp", T), ("SourceTable", "exceptions"),
            ("cloud_RoleName", "func-870-dev-euw-dahl-customerproject-ingress"), ("severityLevel", 3L),
            ("type", "Microsoft.Data.SqlClient.SqlException"), ("outerMessage", "..."), ("operation_Id", "4bf9..")));
        var json = JsonSerializer.Serialize(e, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal("""{"timestamp":"2026-11-02T11:41:00.1230000Z","table":"exceptions","cloudRoleName":"func-870-dev-euw-dahl-customerproject-ingress","severity":3,"message":"SqlException: ...","operationId":"4bf9..","details":{"type":"Microsoft.Data.SqlClient.SqlException"}}""", json);
    }
}

public class LogsServiceTests
{
    private const string ResourceId = "/subscriptions/" + AiFixture.DevSubscriptionId +
        "/resourceGroups/rg-870-dev-euw-dahl-shared/providers/Microsoft.Insights/components/appin-870-dev-euw-dahl-shared";

    [Fact]
    public void Resource_id_comes_from_the_registration_with_the_subscription_name_resolved_via_the_az_profile()
    {
        Assert.Equal(ResourceId, AiFixture.Service(new FakeLogsQuery()).ResourceId(AiFixture.Project()));
    }

    [Fact]
    public void Config_overrides_the_registration_target()
    {
        var s = AiFixture.Service(new FakeLogsQuery(), new AppInsightsOptions { SubscriptionId = "00000000-0000-0000-0000-000000000001", ResourceGroup = "rg-x", Resource = "appin-x" });
        Assert.Equal("/subscriptions/00000000-0000-0000-0000-000000000001/resourceGroups/rg-x/providers/Microsoft.Insights/components/appin-x", s.ResourceId(AiFixture.Project()));
    }

    [Fact]
    public void Unknown_subscription_name_is_503_AI_NOT_CONFIGURED()
    {
        var s = new LogsService(new ProjectStore([AiFixture.Project()]), new FakeLogsQuery(), new AppInsightsOptions(), new AzureProfile([]), NullLogger<LogsService>.Instance);
        var ex = Assert.Throws<ApiException>(() => s.ResourceId(AiFixture.Project()));
        Assert.Equal((503, "AI_NOT_CONFIGURED"), (ex.Status, ex.Code));
    }

    [Fact]
    public async Task Logs_query_uses_registered_roles_and_maps_rows()
    {
        var fake = new FakeLogsQuery
        {
            Rows = [FakeLogsQuery.Row(("timestamp", AiFixture.Now.AddMinutes(-1)), ("SourceTable", "traces"), ("cloud_RoleName", AiFixture.Roles[1]), ("message", "hello"), ("severityLevel", 1L))],
        };
        var events = await AiFixture.Service(fake).LogsAsync("customerproject.dev", null, "2026-11-02T11:41:00Z", "where severityLevel >= 1", null, default);

        var call = Assert.Single(fake.Calls);
        Assert.Equal(ResourceId, call.ResourceId);
        Assert.Equal(new DateTimeOffset(2026, 11, 2, 11, 41, 0, TimeSpan.Zero), call.From);
        Assert.Contains("| where cloud_RoleName in (\"web-870-dev-euw-dahl-customerproject\", \"func-870-dev-euw-dahl-customerproject\", \"func-870-dev-euw-dahl-customerproject-ingress\")", call.Kql);
        Assert.Contains("| where severityLevel >= 1\n", call.Kql);
        Assert.Contains("| top 200 by timestamp desc", call.Kql);
        var e = Assert.Single(events);
        Assert.Equal(("traces", "hello", AiFixture.Roles[1]), (e.Table, e.Message, e.CloudRoleName));
    }

    [Fact]
    public async Task Bad_input_is_rejected_before_any_query()
    {
        var fake = new FakeLogsQuery();
        var s = AiFixture.Service(fake);
        Assert.Equal("KQL_REJECTED", (await Assert.ThrowsAsync<ApiException>(() => s.LogsAsync("customerproject.dev", null, null, "take 1; .drop table x", null, default))).Code);
        Assert.Equal("UNKNOWN_ROLE", (await Assert.ThrowsAsync<ApiException>(() => s.LogsAsync("customerproject.dev", "web-other", null, null, null, default))).Code);
        Assert.Equal("INVALID_MAX", (await Assert.ThrowsAsync<ApiException>(() => s.LogsAsync("customerproject.dev", null, null, null, 5000, default))).Code);
        Assert.Equal("PROJECT_NOT_FOUND", (await Assert.ThrowsAsync<ApiException>(() => s.LogsAsync("customerproject", null, null, null, null, default))).Code);
        Assert.Empty(fake.Calls);
    }

    [Fact]
    public async Task Azure_failure_surfaces_as_an_error_never_as_an_empty_list()
    {
        var fake = new FakeLogsQuery { Throw = new CredentialUnavailableException("no az") };
        var ex = await Assert.ThrowsAsync<ApiException>(() => AiFixture.Service(fake).LogsAsync("customerproject.dev", null, null, null, null, default));
        Assert.Equal((503, "AI_AUTH_FAILED"), (ex.Status, ex.Code));
    }

    [Fact]
    public async Task Service_400_with_a_user_fragment_is_KQL_INVALID()
    {
        var fake = new FakeLogsQuery { Throw = new RequestFailedException(400, "SemanticError: 'nope' not found") };
        var ex = await Assert.ThrowsAsync<ApiException>(() => AiFixture.Service(fake).LogsAsync("customerproject.dev", null, null, "where nope == 1", null, default));
        Assert.Equal((400, "KQL_INVALID"), (ex.Status, ex.Code));
    }

    [Fact]
    public async Task Role_confirmation_flags_registered_missing_and_unregistered_names()
    {
        var fake = new FakeLogsQuery
        {
            Rows =
            [
                FakeLogsQuery.Row(("cloud_RoleName", "func-870-dev-euw-dahl-custprin"), ("itemCount", 12L)),
                FakeLogsQuery.Row(("cloud_RoleName", "func-870-dev-euw-dahl-customerproject"), ("itemCount", 40L)),
                FakeLogsQuery.Row(("cloud_RoleName", "func-870-dev-euw-dahl-customerproject-ingressx"), ("itemCount", 3L)),
                FakeLogsQuery.Row(("cloud_RoleName", "web-870-dev-euw-dahl-order"), ("itemCount", 99L)),
            ],
        };
        var r = await AiFixture.Service(fake).ConfirmRolesAsync("customerproject.dev", null, default);

        Assert.Contains("summarize itemCount = count() by cloud_RoleName", Assert.Single(fake.Calls).Kql);
        Assert.Equal("datetime(2026-11-01T12:00:00.0000000Z)", r.Since);
        Assert.Equal(4, r.Observed.Count);
        Assert.True(r.Observed.Single(o => o.CloudRoleName == "func-870-dev-euw-dahl-customerproject").Registered);
        Assert.Equal(["web-870-dev-euw-dahl-customerproject", "func-870-dev-euw-dahl-customerproject-ingress"], r.RegisteredMissing);
        Assert.Equal(["func-870-dev-euw-dahl-customerproject-ingressx"], r.Unregistered);
    }

    [Fact]
    public async Task Probe_reports_ok_only_after_a_successful_query()
    {
        var fake = new FakeLogsQuery { Throw = new CredentialUnavailableException("no az") };
        var s = AiFixture.Service(fake);
        var p = AiFixture.Project();

        Assert.False(await s.ProbeNowAsync(p));
        Assert.Equal(KqlBuilder.ProbeQuery, fake.Calls.Last().Kql);
        Assert.False(s.IsQueryOk(p));

        fake.Throw = null;
        Assert.True(await s.ProbeNowAsync(p));
        Assert.True(s.IsQueryOk(p)); // cached (TTL) — no extra query
        Assert.False(AiFixture.Service(fake, new AppInsightsOptions { ProbeEnabled = false }).IsQueryOk(p));
    }
}

/// <summary>HTTP level with the Azure seam replaced: routes, error envelope, query string binding.</summary>
public sealed class LogsApiTests : IClassFixture<LogsApiTests.Factory>
{
    public sealed class Factory : WebApplicationFactory<Program>
    {
        public readonly FakeLogsQuery Fake = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("Overview:AppInsights:ProbeEnabled", "false");
            builder.ConfigureTestServices(s =>
            {
                s.AddSingleton<ILogsQuery>(Fake);
                s.AddSingleton(AiFixture.Profile);
            });
        }
    }

    private const string P = "/api/projects/customerproject.dev";
    private readonly Factory _factory;
    private readonly HttpClient _http;

    public LogsApiTests(Factory factory) => (_factory, _http) = (factory, factory.CreateClient());

    private static async Task<(HttpStatusCode, string)> Error(HttpResponseMessage r) =>
        (r.StatusCode, JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.GetProperty("error").GetProperty("code").GetString()!);

    [Fact]
    public async Task Get_logs_returns_LogEvent_array_and_errors_use_the_envelope()
    {
        lock (_factory.Fake.Calls)
        {
            _factory.Fake.Throw = null;
            _factory.Fake.Rows = [FakeLogsQuery.Row(("timestamp", AiFixture.Now), ("SourceTable", "requests"), ("cloud_RoleName", AiFixture.Roles[0]), ("name", "GET /"), ("resultCode", "200"), ("success", true))];
        }
        var events = await _http.GetFromJsonAsync<JsonElement>($"{P}/logs?roles={AiFixture.Roles[0]}&since=15m&kql=where%20resultCode%20%3D%3D%20%22200%22");
        var e = Assert.Single(events.EnumerateArray());
        Assert.Equal("GET / → 200", e.GetProperty("message").GetString());
        Assert.Equal("requests", e.GetProperty("table").GetString());
        Assert.Contains("| where resultCode == \"200\"", _factory.Fake.Calls.Last().Kql);

        Assert.Equal((HttpStatusCode.BadRequest, "KQL_REJECTED"), await Error(await _http.GetAsync($"{P}/logs?kql=.drop%20table%20x")));
        Assert.Equal((HttpStatusCode.BadRequest, "INVALID_ROLE"), await Error(await _http.GetAsync($"{P}/logs?roles=a%22b")));
        Assert.Equal((HttpStatusCode.BadRequest, "INVALID_SINCE"), await Error(await _http.GetAsync($"{P}/logs?since=nope")));
    }

    [Fact]
    public async Task Get_logs_without_azure_auth_is_503_AI_AUTH_FAILED()
    {
        var fake = new FakeLogsQuery { Throw = new CredentialUnavailableException("DefaultAzureCredential failed") };
        using var factory = _factory.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton<ILogsQuery>(fake)));
        var http = factory.CreateClient();

        Assert.Equal((HttpStatusCode.ServiceUnavailable, "AI_AUTH_FAILED"), await Error(await http.GetAsync($"{P}/logs")));
        Assert.Equal((HttpStatusCode.ServiceUnavailable, "AI_AUTH_FAILED"), await Error(await http.GetAsync($"{P}/logs/roles")));
    }
}
