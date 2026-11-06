using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dsp.DevOverview.Api.Registration;
using Dsp.DevOverview.Api.ServiceBus;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Dsp.DevOverview.Api.Tests;

public class PurgePolicyTests
{
    private const string Emulator = "Endpoint=sb://localhost;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true";
    private const string Cloud = "Endpoint=sb://sb-870-dev-euw-dahl-shared.servicebus.windows.net/;SharedAccessKeyName=x;SharedAccessKey=y";

    [Fact]
    public void Purge_is_allowed_on_the_local_emulator()
    {
        PurgePolicy.EnsureAllowed(new ServiceBusOptions { ConnectionString = Emulator, Scope = SbScope.Local });
    }

    [Theory]
    [InlineData(Cloud, SbScope.Local)]
    [InlineData(Cloud, SbScope.Cloud)]
    [InlineData(Emulator, SbScope.Cloud)]
    [InlineData("", SbScope.Local)]
    public void Purge_is_forbidden_for_non_emulator(string connectionString, SbScope scope)
    {
        var ex = Assert.Throws<ApiException>(() => PurgePolicy.EnsureAllowed(new ServiceBusOptions { ConnectionString = connectionString, Scope = scope }));
        Assert.Equal(403, ex.Status);
        Assert.Equal("PURGE_CLOUD_FORBIDDEN", ex.Code);
    }

    [Fact]
    public void Sending_to_cloud_is_forbidden()
    {
        var ex = Assert.Throws<ApiException>(() => PurgePolicy.EnsureWritable(new ServiceBusOptions { ConnectionString = Cloud }));
        Assert.Equal("CLOUD_WRITE_FORBIDDEN", ex.Code);
    }

    [Fact]
    public void Emulator_detection_and_admin_count_default()
    {
        Assert.True(new ServiceBusOptions { ConnectionString = Emulator }.IsEmulator);
        Assert.False(new ServiceBusOptions { ConnectionString = Emulator }.AdminCountsEnabled); // emulator admin reports 0 counts
        Assert.True(new ServiceBusOptions { ConnectionString = Cloud }.AdminCountsEnabled);
    }
}

/// <summary>HTTP-level tests against a non-emulator connection string: nothing here may touch the network.</summary>
public class CloudConfiguredApiTests(CloudConfiguredApiTests.Factory factory) : IClassFixture<CloudConfiguredApiTests.Factory>
{
    public sealed class Factory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("Overview:ServiceBus:ConnectionString", "Endpoint=sb://sb-870-dev-euw-dahl-shared.servicebus.windows.net/;SharedAccessKeyName=x;SharedAccessKey=y");
            builder.UseSetting("Overview:ServiceBus:Scope", "Cloud");
        }
    }

    private readonly HttpClient _http = factory.CreateClient();

    private static async Task<string> ErrorCode(HttpResponseMessage r) =>
        JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.GetProperty("error").GetProperty("code").GetString()!;

    [Fact]
    public async Task Purge_returns_403_PURGE_CLOUD_FORBIDDEN()
    {
        var r = await _http.PostAsync("/api/projects/customerproject.dev/sb/purge", null);
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        Assert.Equal("PURGE_CLOUD_FORBIDDEN", await ErrorCode(r));
    }

    [Fact]
    public async Task Send_mode_purge_returns_403_PURGE_CLOUD_FORBIDDEN()
    {
        var r = await _http.PostAsJsonAsync("/api/projects/customerproject.dev/samples/send", new { mode = "purge" });
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        Assert.Equal("PURGE_CLOUD_FORBIDDEN", await ErrorCode(r));
    }

    [Fact]
    public async Task Send_returns_403_CLOUD_WRITE_FORBIDDEN()
    {
        var r = await _http.PostAsJsonAsync("/api/projects/customerproject.dev/samples/send", new { mode = "one", @event = "CustomerChanged" });
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        Assert.Equal("CLOUD_WRITE_FORBIDDEN", await ErrorCode(r));
    }

    [Fact]
    public async Task Samples_endpoint_serves_the_catalog()
    {
        var doc = JsonDocument.Parse(await _http.GetStringAsync("/api/projects/customerproject.dev/samples")).RootElement;
        Assert.Equal("customerproject.dev", doc.GetProperty("projectId").GetString());
        Assert.Equal(23, doc.GetProperty("messages").GetArrayLength());
        Assert.Equal(3, doc.GetProperty("sampleProjects").GetArrayLength());
        Assert.Equal("kit", doc.GetProperty("commandPropertyProfile").GetString());
        var command = doc.GetProperty("messages").EnumerateArray().First(m => m.GetProperty("presetId").GetString() == "commands");
        Assert.Equal(["Dsp_ShardingBucketNumber", "Dsp_CorrelationContextId"],
            command.GetProperty("runtimeApplicationProperties").EnumerateArray().Select(x => x.GetString()));
    }

    [Fact]
    public async Task Unknown_project_is_404_with_error_envelope()
    {
        var r = await _http.GetAsync("/api/projects/nope.dev/samples");
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
        Assert.Equal("PROJECT_NOT_FOUND", await ErrorCode(r));
    }
}

public class ServiceBusAuthModeTests
{
    private const string Emulator = "Endpoint=sb://localhost;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true";
    private const string Fqns = "sb-bjoo-local-noe-dahl-shared.servicebus.windows.net";

    private sealed class FakeCredential : Azure.Core.TokenCredential
    {
        public override Azure.Core.AccessToken GetToken(Azure.Core.TokenRequestContext c, CancellationToken ct) => new("x", DateTimeOffset.MaxValue);
        public override ValueTask<Azure.Core.AccessToken> GetTokenAsync(Azure.Core.TokenRequestContext c, CancellationToken ct) => new(GetToken(c, ct));
    }

    [Fact]
    public void Connection_string_wins_over_namespace()
    {
        var o = new ServiceBusOptions { ConnectionString = Emulator, FullyQualifiedNamespace = Fqns };
        Assert.Equal(SbAuthMode.ConnectionString, o.AuthMode);
        Assert.True(o.IsEmulator);
        Assert.Equal(SbScope.Local, o.EntityNameScope);
    }

    [Fact]
    public void Namespace_only_uses_credential_client()
    {
        var o = new ServiceBusOptions { Scope = SbScope.Cloud, FullyQualifiedNamespace = "sb://" + Fqns + "/" };
        Assert.Equal(SbAuthMode.Credential, o.AuthMode);
        Assert.False(o.IsEmulator);
        Assert.True(o.AdminCountsEnabled);
        Assert.Equal(SbHostKind.DeveloperLocalAzure, o.HostKind);
        var client = SbGateway.CreateClient(o, new FakeCredential());
        Assert.Equal(Fqns, client.FullyQualifiedNamespace);
    }

    [Fact]
    public void Nothing_configured_is_503()
    {
        var ex = Assert.Throws<ApiException>(() => SbGateway.CreateClient(new ServiceBusOptions(), new FakeCredential()));
        Assert.Equal("SB_NOT_CONFIGURED", ex.Code);
    }

    [Fact]
    public void Bjoo_namespace_is_developer_local_sandbox_with_derived_names()
    {
        var o = new ServiceBusOptions { Scope = SbScope.Local, FullyQualifiedNamespace = Fqns };
        Assert.Equal(SbHostKind.DeveloperLocalAzure, o.HostKind);
        Assert.Equal("bjoo", o.DeveloperName);
        PurgePolicy.EnsureWritable(o);
        PurgePolicy.EnsureAllowed(o);
        Assert.Equal("sbq-bjoo-local-noe-dahl-shared-customerproject",
            o.EntityName(new Dsp.DevOverview.Api.Registration.RegisteredEntity { Kind = "queue", LocalName = "customerproject", CloudName = "sbq-870-x" }));
        Assert.Equal("sbt-bjoo-local-noe-dahl-shared-customerproject",
            o.EntityName(new Dsp.DevOverview.Api.Registration.RegisteredEntity { Kind = "topic", LocalName = "customerproject" }));
    }

    [Fact]
    public void Cloud_dev_namespace_stays_peek_only()
    {
        var o = new ServiceBusOptions { Scope = SbScope.Local, FullyQualifiedNamespace = "sb-870-dev-euw-dahl-shared.servicebus.windows.net" };
        Assert.Equal(SbHostKind.CloudDev, o.HostKind);
        Assert.Equal("CLOUD_WRITE_FORBIDDEN", Assert.Throws<ApiException>(() => PurgePolicy.EnsureWritable(o)).Code);
        Assert.Equal("PURGE_CLOUD_FORBIDDEN", Assert.Throws<ApiException>(() => PurgePolicy.EnsureAllowed(o)).Code);
        Assert.Equal("q-cloud", o.EntityName(new Dsp.DevOverview.Api.Registration.RegisteredEntity { Kind = "queue", LocalName = "q", CloudName = "q-cloud" }));
    }
}
