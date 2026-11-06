using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Dsp.DevOverview.Api.Tests;

/// <summary>
/// Slice 6 POST /registration/refresh over HTTP, against the generator fixtures (project "demo") and a temp copy of
/// their current.json — the real registration and the read-only sources are never touched. No Service Bus (Production).
/// </summary>
public sealed class RegistrationRefreshTests : IClassFixture<RegistrationRefreshTests.Factory>
{
    public sealed class Factory : WebApplicationFactory<Program>
    {
        public static string Fixtures => Path.Combine(RepoPaths.Root, "test", "fixtures", "registration-generator");
        public readonly string Dir = Directory.CreateTempSubdirectory("dsp-registration-refresh-").FullName;
        public string RegistrationPath => Path.Combine(Dir, "demo.dev.json");
        public string DataDir => Path.Combine(Dir, "data");

        public Factory() => File.Copy(Path.Combine(Fixtures, "current.json"), RegistrationPath);

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production"); // appsettings.json: empty SB connection string → no network
            builder.UseSetting("Overview:Registrations:0", RegistrationPath);
            builder.UseSetting("Overview:DataDir", DataDir);
            builder.UseSetting("Overview:RegistrationGenerator:TerraformEnvDir", Path.Combine(Fixtures, "tf"));
            builder.UseSetting("Overview:RegistrationGenerator:ServiceRepo", Path.Combine(Fixtures, "repo"));
            builder.UseSetting("Overview:RegistrationGenerator:LocalDevDir", Path.Combine(Fixtures, "local"));
            builder.UseSetting("Overview:RegistrationGenerator:SamplesCatalog", RepoPaths.Catalog);
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (Directory.Exists(Dir)) Directory.Delete(Dir, recursive: true);
        }
    }

    private const string P = "/api/projects/demo.dev";
    private readonly Factory _factory;
    private readonly HttpClient _http;

    public RegistrationRefreshTests(Factory factory) => (_factory, _http) = (factory, factory.CreateClient());

    private async Task<(HttpStatusCode Status, JsonElement Body)> Post(string url)
    {
        var r = await _http.PostAsync(url, null);
        return (r.StatusCode, JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement);
    }

    private async Task<bool> FlowHasEdge(string from, string to)
    {
        var g = JsonDocument.Parse(await _http.GetStringAsync($"{P}/flow?scope=all")).RootElement;
        return g.GetProperty("edges").EnumerateArray().Any(e => e.GetProperty("from").GetString() == from && e.GetProperty("to").GetString() == to);
    }

    [Fact]
    public async Task Refresh_regenerates_writes_backs_up_and_hot_reloads()
    {
        var original = File.ReadAllText(_factory.RegistrationPath);
        // current.json is v1 with an edge from an unregistered app:api: the in-memory upgrade drops it.
        Assert.False(await FlowHasEdge("demo.api.http", "q:demo"));

        // Dry run: diff + gaps, nothing written or reloaded.
        var (status, dry) = await Post($"{P}/registration/refresh?dryRun=true");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("demo.dev", dry.GetProperty("id").GetString()); // ProjectSummary fields stay top-level (contract)
        Assert.Equal("2026-01-01T00:00:00+00:00", dry.GetProperty("registrationGeneratedAt").GetString());
        var info = dry.GetProperty("refresh");
        Assert.True(info.GetProperty("dryRun").GetBoolean());
        Assert.False(info.GetProperty("written").GetBoolean());
        Assert.Contains(info.GetProperty("diff").EnumerateArray(), l => l.GetString() == "  overrides preserved");
        Assert.Contains(info.GetProperty("gaps").EnumerateArray(), l => l.GetString()!.Contains("demo-to-demo-subscription"));
        Assert.Equal(original, File.ReadAllText(_factory.RegistrationPath));
        Assert.False(await FlowHasEdge("demo.api.http", "q:demo"));

        // Real refresh: file rewritten (overrides intact), previous copy in data/registrations/history, store hot-swapped.
        (status, var body) = await Post($"{P}/registration/refresh");
        Assert.Equal(HttpStatusCode.OK, status);
        info = body.GetProperty("refresh");
        Assert.True(info.GetProperty("written").GetBoolean());
        Assert.Equal(original, File.ReadAllText(info.GetProperty("backup").GetString()!));
        Assert.StartsWith(Path.Combine(_factory.DataDir, "registrations", "history"), info.GetProperty("backup").GetString());
        Assert.NotEqual("2026-01-01T00:00:00+00:00", body.GetProperty("registrationGeneratedAt").GetString());
        var written = JsonNode.Parse(File.ReadAllText(_factory.RegistrationPath))!;
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(original)!["overrides"], written["overrides"]));
        Assert.Equal(2, written["schemaVersion"]!.GetValue<int>());
        Assert.True(await FlowHasEdge("demo.api.http", "q:demo")); // code-attributed (DemoController.cs:14)
        Assert.False(await FlowHasEdge("demo.api.http", "q:demo-ingress")); // sender registered, never used (OQ-2)
        Assert.True(await FlowHasEdge("ext:partner", "q:demo-ingress")); // overrides.edges applied

        // source=file: reload whatever is on disk (e.g. after tools/Generate-Registration or a restore).
        File.WriteAllText(_factory.RegistrationPath, original);
        (status, body) = await Post($"{P}/registration/refresh?source=file");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("file", body.GetProperty("refresh").GetProperty("source").GetString());
        Assert.Equal("2026-01-01T00:00:00+00:00", body.GetProperty("registrationGeneratedAt").GetString());
        Assert.False(await FlowHasEdge("demo.api.http", "q:demo"));
    }

    [Fact]
    public async Task Invalid_file_is_rejected_and_the_loaded_registration_stays()
    {
        var before = (await Post($"{P}/registration/refresh?source=file&dryRun=true")).Body.GetProperty("registrationGeneratedAt").GetString();
        var original = File.ReadAllText(_factory.RegistrationPath);
        try
        {
            File.WriteAllText(_factory.RegistrationPath, original.Replace("\"env\": \"dev\"", "\"env\": \"prod\""));
            var (status, body) = await Post($"{P}/registration/refresh?source=file");
            Assert.Equal((HttpStatusCode)422, status);
            Assert.Equal("REGISTRATION_INVALID", body.GetProperty("error").GetProperty("code").GetString());
            var summary = JsonDocument.Parse(await _http.GetStringAsync("/api/projects")).RootElement.EnumerateArray().Single(x => x.GetProperty("id").GetString() == "demo.dev");
            Assert.Equal(before, summary.GetProperty("registrationGeneratedAt").GetString());
        }
        finally { File.WriteAllText(_factory.RegistrationPath, original); }
    }

    [Fact]
    public async Task Unknown_project_and_bad_source_are_client_errors()
    {
        var (status, body) = await Post("/api/projects/nope.dev/registration/refresh");
        Assert.Equal(HttpStatusCode.NotFound, status);
        Assert.Equal("PROJECT_NOT_FOUND", body.GetProperty("error").GetProperty("code").GetString());

        (status, body) = await Post($"{P}/registration/refresh?source=git");
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("INVALID_SOURCE", body.GetProperty("error").GetProperty("code").GetString());
    }
}
