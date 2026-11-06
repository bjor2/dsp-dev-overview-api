using System.Net;
using System.Text;
using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Dsp.DevOverview.Api.Health;
using Dsp.DevOverview.Api.ServiceBus;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;

namespace Dsp.DevOverview.Api.Tests;

/// <summary>ADDENDA-005: host-aware SB error copy, SB_AUTH_FAILED, option (a) credential wiring.</summary>
public class SbHostingCopyTests
{
    private const string Fqns = "sb-bjoo-local-noe-dahl-shared.servicebus.windows.net";

    [Theory]
    [InlineData("Render", null, true)]
    [InlineData(null, "true", true)]
    [InlineData("render", "", true)]
    [InlineData(null, null, false)]
    [InlineData("Box", "", false)]
    public void DetectRender(string? hosting, string? renderEnv, bool expected) =>
        Assert.Equal(expected, OverviewOptions.DetectRender(hosting, renderEnv));

    [Fact]
    public void Render_not_configured_points_at_cloud_api_config()
    {
        var o = new ServiceBusOptions { HostedOnRender = true };
        foreach (var m in new[] { o.UnreachableMessage, o.NotConfiguredMessage })
        {
            Assert.Contains("Render-hosted Overview API has no Service Bus credential configured", m);
            Assert.Contains("AZURE_CLIENT_SECRET", m);
            Assert.DoesNotContain("is the shared stack up", m);
        }
    }

    [Fact]
    public void Render_with_fqns_unreachable_and_auth_copy_is_cloud_side()
    {
        var o = new ServiceBusOptions { HostedOnRender = true, FullyQualifiedNamespace = Fqns };
        Assert.Contains("Render-hosted Overview API cannot reach", o.UnreachableMessage);
        Assert.DoesNotContain("is the shared stack up", o.UnreachableMessage);
        Assert.Contains("AZURE_CLIENT_ID", o.AuthFailedMessage);
        Assert.Contains("Data Owner", o.AuthFailedMessage);
    }

    [Fact]
    public async Task Render_unconfigured_entities_is_SB_NOT_CONFIGURED()
    {
        Environment.SetEnvironmentVariable("RENDER", null);
        using var f = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Production");
            b.UseSetting("Overview:AppInsights:ProbeEnabled", "false");
            b.UseSetting("Overview:Hosting", "Render");
        });
        var r = await f.CreateClient().GetAsync("/api/projects/customerproject.dev/sb/entities");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, r.StatusCode);
        using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
        var e = doc.RootElement.GetProperty("error");
        Assert.Equal("SB_NOT_CONFIGURED", e.GetProperty("code").GetString());
        Assert.StartsWith("Render-hosted Overview API has no Service Bus credential configured", e.GetProperty("message").GetString());
    }

    [Theory]
    // ADDENDA-004: <short> comes from the cloud name; emulator localName (customerproject-topic) differs for the topic.
    [InlineData("topic", "customerproject-topic", "sbt-870-dev-euw-dahl-shared-customerproject", "sbt-bjoo-local-noe-dahl-shared-customerproject")]
    [InlineData("queue", "customerproject-ingress", "sbq-870-dev-euw-dahl-shared-customerproject-ingress", "sbq-bjoo-local-noe-dahl-shared-customerproject-ingress")]
    [InlineData("queue", "customerproject-topic-inspect", null, "sbq-bjoo-local-noe-dahl-shared-customerproject-topic-inspect")]
    [InlineData("subscription", "local-inspect", null, "local-inspect")]
    public void Developer_local_names(string kind, string local, string? cloud, string expected) =>
        Assert.Equal(expected, new ServiceBusOptions { FullyQualifiedNamespace = Fqns }
            .EntityName(new Dsp.DevOverview.Api.Registration.RegisteredEntity { Kind = kind, LocalName = local, CloudName = cloud }));

    [Fact]
    public void Emulator_and_cloud_names_unchanged()
    {
        var topic = new Dsp.DevOverview.Api.Registration.RegisteredEntity { Kind = "topic", LocalName = "customerproject-topic", CloudName = "sbt-870-dev-euw-dahl-shared-customerproject" };
        Assert.Equal("customerproject-topic", new ServiceBusOptions { ConnectionString = "Endpoint=sb://localhost;UseDevelopmentEmulator=true;" }.EntityName(topic));
        Assert.Equal("sbt-870-dev-euw-dahl-shared-customerproject", new ServiceBusOptions { FullyQualifiedNamespace = "sb-870-dev-euw-dahl-shared.servicebus.windows.net" }.EntityName(topic));
    }

    [Theory]
    [InlineData("Endpoint=sb://localhost;UseDevelopmentEmulator=true;", null, "emulator")]
    [InlineData("", Fqns, "developer-local-azure")]
    [InlineData("", "sb-870-dev-euw-dahl-shared.servicebus.windows.net", "cloud-dev")]
    public void Host_kind_label(string cs, string? fqns, string expected) =>
        Assert.Equal(expected, new ServiceBusOptions { ConnectionString = cs, FullyQualifiedNamespace = fqns }.HostKindLabel);

    [Fact]
    public void Local_copy_unchanged()
    {
        Assert.Contains("is the shared stack up", new ServiceBusOptions { ConnectionString = "Endpoint=sb://localhost;UseDevelopmentEmulator=true;" }.UnreachableMessage);
        Assert.Contains("az login", new ServiceBusOptions { FullyQualifiedNamespace = Fqns }.UnreachableMessage);
        Assert.Contains("az login", new ServiceBusOptions { FullyQualifiedNamespace = Fqns }.AuthFailedMessage);
        Assert.Contains("ConnectionString", new ServiceBusOptions().NotConfiguredMessage);
    }

    [Fact]
    public void Auth_failures_classified_separately()
    {
        Assert.True(SbGateway.IsAuthFailure(new Azure.Identity.AuthenticationFailedException("ClientSecretCredential authentication failed")));
        Assert.True(SbGateway.IsAuthFailure(new Azure.Identity.CredentialUnavailableException("EnvironmentCredential unavailable")));
        Assert.True(SbGateway.IsAuthFailure(new UnauthorizedAccessException("Unauthorized access. 'Listen' claim(s) are required")));
        Assert.True(SbGateway.IsAuthFailure(new Azure.RequestFailedException(403, "Forbidden")));
        Assert.True(SbGateway.IsAuthFailure(new InvalidOperationException("wrap", new UnauthorizedAccessException("x"))));
        Assert.False(SbGateway.IsAuthFailure(new ServiceBusException("comm", ServiceBusFailureReason.ServiceCommunicationProblem)));
        Assert.False(SbGateway.IsAuthFailure(new TimeoutException()));
    }

    [Fact]
    public void Option_a_fqns_uses_credential_and_websockets()
    {
        var o = new ServiceBusOptions { FullyQualifiedNamespace = "sb://" + Fqns + "/" };
        Assert.Equal(SbAuthMode.Credential, o.AuthMode);
        Assert.Equal(SbHostKind.DeveloperLocalAzure, o.HostKind);
        Assert.Equal(ServiceBusTransportType.AmqpWebSockets, o.EffectiveTransport);
        // DefaultAzureCredential tries EnvironmentCredential first (AZURE_TENANT_ID/AZURE_CLIENT_ID/AZURE_CLIENT_SECRET).
        var client = SbGateway.CreateClient(o, new Azure.Identity.DefaultAzureCredential());
        Assert.Equal(Fqns, client.FullyQualifiedNamespace);
        Assert.Equal(ServiceBusTransportType.AmqpWebSockets, client.TransportType);
    }

    [Fact]
    public void Environment_credential_reads_client_secret_vars()
    {
        var keys = new[] { "AZURE_TENANT_ID", "AZURE_CLIENT_ID", "AZURE_CLIENT_SECRET" };
        var saved = keys.ToDictionary(k => k, Environment.GetEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable("AZURE_TENANT_ID", "00000000-0000-0000-0000-000000000001");
            Environment.SetEnvironmentVariable("AZURE_CLIENT_ID", "00000000-0000-0000-0000-000000000002");
            Environment.SetEnvironmentVariable("AZURE_CLIENT_SECRET", "not-a-real-secret");
            // EnvironmentCredential wraps a ClientSecretCredential when all three are present (no network until GetToken).
            var cred = new Azure.Identity.EnvironmentCredential();
            var inner = typeof(Azure.Identity.EnvironmentCredential).GetProperty("Credential",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.GetValue(cred);
            Assert.IsType<Azure.Identity.ClientSecretCredential>(inner);
        }
        finally { foreach (var (k, v) in saved) Environment.SetEnvironmentVariable(k, v); }
    }
}

/// <summary>Option (b): Overview:ServiceBus:ProxyUpstream forwards SB endpoints; off by default.</summary>
public class SbProxyTests
{
    [Theory]
    [InlineData("/api/projects/customerproject/sb/entities", true)]
    [InlineData("/api/projects/customerproject/sb/q:x/peek", true)]
    [InlineData("/api/projects/customerproject/sb/s:t/sub/dlq/retry", true)]
    [InlineData("/api/projects/customerproject/sb/purge", true)]
    [InlineData("/api/projects/customerproject/samples/send", true)]
    [InlineData("/api/projects/customerproject/samples", false)]
    [InlineData("/api/projects/customerproject/flow", false)]
    [InlineData("/api/projects", false)]
    public void Proxied_paths(string path, bool expected) => Assert.Equal(expected, SbProxy.IsProxied(path));

    [Fact]
    public void Parse_upstream_health()
    {
        using var doc = JsonDocument.Parse("""[{"id":"a","healthDetail":{"sbReachable":true},"dlqTotal":3},{"id":"b","healthDetail":{"sbReachable":false},"dlqTotal":9}]""");
        var r = UpstreamSbHealthProbe.Parse(doc.RootElement);
        Assert.Equal(new SbHealth(true, 3), r["a"]);
        Assert.Equal(new SbHealth(false, null), r["b"]);
    }

    private sealed class Upstream(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(string Method, string Url, string? Body)> Calls { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            Calls.Add((r.Method.Method, r.RequestUri!.ToString(), r.Content is null ? null : await r.Content.ReadAsStringAsync(ct)));
            return respond(r);
        }
    }

    private static WebApplicationFactory<Program> Factory(Upstream upstream, string? proxy = "http://box.test") =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Production");
            b.UseSetting("Overview:AppInsights:ProbeEnabled", "false");
            b.UseSetting("Overview:Health:SbProbeCacheSeconds", "0");
            if (proxy is not null) b.UseSetting("Overview:ServiceBus:ProxyUpstream", proxy);
            b.ConfigureTestServices(s => s.AddHttpClient(SbProxy.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => upstream));
        });

    private static HttpResponseMessage Json(HttpStatusCode code, string body) => new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task Forwards_status_and_body()
    {
        var upstream = new Upstream(r => r.RequestUri!.AbsolutePath.EndsWith("/peek")
            ? Json(HttpStatusCode.OK, """{"entityId":"q:x"}""")
            : Json(HttpStatusCode.Forbidden, """{"error":{"code":"CLOUD_WRITE_FORBIDDEN","message":"no","retryable":false}}"""));
        using var f = Factory(upstream);
        var client = f.CreateClient();

        var peek = await client.GetAsync("/api/projects/customerproject/sb/q:x/peek?max=5");
        Assert.Equal(HttpStatusCode.OK, peek.StatusCode);
        Assert.Equal("""{"entityId":"q:x"}""", await peek.Content.ReadAsStringAsync());
        Assert.Contains(upstream.Calls, c => c.Url == "http://box.test/api/projects/customerproject/sb/q:x/peek?max=5");

        var send = await client.PostAsync("/api/projects/customerproject/samples/send", new StringContent("""{"mode":"single"}""", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Forbidden, send.StatusCode);
        Assert.Contains("CLOUD_WRITE_FORBIDDEN", await send.Content.ReadAsStringAsync());
        Assert.Contains(upstream.Calls, c => c.Method == "POST" && c.Body == """{"mode":"single"}""");
    }

    [Fact]
    public async Task Upstream_down_is_502_envelope_and_health_reads_upstream()
    {
        var upstream = new Upstream(r => r.RequestUri!.AbsolutePath == "/api/projects"
            ? Json(HttpStatusCode.OK, """[{"id":"customerproject.dev","healthDetail":{"sbReachable":true},"dlqTotal":2}]""")
            : throw new HttpRequestException("connection refused"));
        using var f = Factory(upstream);
        var client = f.CreateClient();

        var entities = await client.GetAsync("/api/projects/customerproject.dev/sb/entities");
        Assert.Equal(HttpStatusCode.BadGateway, entities.StatusCode);
        Assert.Contains("SB_PROXY_UNREACHABLE", await entities.Content.ReadAsStringAsync());

        using var projects = JsonDocument.Parse(await client.GetStringAsync("/api/projects"));
        var p = projects.RootElement[0];
        Assert.True(p.GetProperty("healthDetail").GetProperty("sbReachable").GetBoolean());
        Assert.Equal(2, p.GetProperty("dlqTotal").GetInt32());
    }

    [Fact]
    public async Task Off_by_default_no_upstream_calls()
    {
        var upstream = new Upstream(_ => Json(HttpStatusCode.OK, "{}"));
        using var f = Factory(upstream, proxy: null);
        var r = await f.CreateClient().GetAsync("/api/projects/customerproject.dev/sb/entities");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, r.StatusCode); // local gateway, not configured
        Assert.Contains("\"SB_NOT_CONFIGURED\"", await r.Content.ReadAsStringAsync());
        Assert.Empty(upstream.Calls);
        Assert.Equal("ok", await f.CreateClient().GetStringAsync("/healthz"));
    }
}
