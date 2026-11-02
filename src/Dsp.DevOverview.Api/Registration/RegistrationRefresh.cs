using System.Text.Json;
using System.Text.Json.Nodes;
using Dsp.DevOverview.RegistrationGenerator;
using Generator = Dsp.DevOverview.RegistrationGenerator.RegistrationGenerator;

namespace Dsp.DevOverview.Api.Registration;

/// <summary>Overview:RegistrationGenerator — inputs for in-process regeneration. Null paths follow the workspace conventions (GeneratorInputs).</summary>
public sealed class RegistrationRefreshOptions
{
    public string TerraformEnvDir { get; set; } = GeneratorInputs.DefaultTerraformEnvDir;
    public string? ServiceRepo { get; set; }
    public string? LocalDevDir { get; set; }
    public string? SamplesCatalog { get; set; }
    public bool IncludeExternalSubscriptions { get; set; }
    /// <summary>Previous records are copied here before a refresh overwrites them. Relative to Overview:DataDir.</summary>
    public string HistoryDir { get; set; } = "registrations/history";
}

/// <summary>What a refresh did; returned next to the ProjectSummary fields as <c>refresh</c>.</summary>
public sealed record RegistrationRefreshInfo(
    string Source, bool DryRun, bool Written, string Path, string? Backup, string GeneratedAt,
    int Apps, int Entities, int Edges, IReadOnlyList<string> Diff, IReadOnlyList<string> Gaps);

/// <summary>
/// Slice 6: POST /api/projects/{id}/registration/refresh. source=generate (default) runs the generator library in-process
/// against the read-only sources, merges into the current record (overrides preserved), validates against the schema,
/// writes the file the API loaded (previous copy in data/registrations/history) and hot-swaps the project in the
/// ProjectStore. source=file only re-reads the file (after tools/Generate-Registration wrote it). Invalid output is never
/// written or loaded. Refreshes are serialized.
/// </summary>
public sealed class RegistrationRefreshService(
    ProjectStore store, OverviewOptions options, string root, OverviewService overview, ILogger<RegistrationRefreshService> log)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<JsonObject> RefreshAsync(string id, string? source, bool dryRun, bool? includeExternal, CancellationToken ct)
    {
        var mode = (source ?? "generate").ToLowerInvariant();
        if (mode is not ("generate" or "file"))
            throw new ApiException(StatusCodes.Status400BadRequest, "INVALID_SOURCE", $"source '{source}' is not generate|file.");

        RegistrationRefreshInfo info;
        await _gate.WaitAsync(ct);
        try
        {
            var project = overview.Project(id);
            var path = project.Registration.SourcePath;
            info = mode == "file" ? ReloadFile(project, path, dryRun) : Regenerate(project, path, dryRun, includeExternal);
        }
        finally { _gate.Release(); }

        var summary = (await overview.ProjectsAsync(ct)).First(s => s.Id == id);
        var result = JsonSerializer.SerializeToNode(summary, JsonSerializerOptions.Web)!.AsObject();
        result["refresh"] = JsonSerializer.SerializeToNode(info, JsonSerializerOptions.Web);
        return result;
    }

    private RegistrationLoader Loader() => new(Path.Combine(root, options.RegistrationSchema));

    private RegistrationRefreshInfo ReloadFile(OverviewProject project, string path, bool dryRun)
    {
        var r = Load(path, dryRun).Registration;
        return new RegistrationRefreshInfo("file", dryRun, false, path, null, r.GeneratedAt.ToString("yyyy-MM-ddTHH:mm:sszzz"),
            r.Apps.Count, r.Entities.Count, r.Edges.Count,
            [$"reloaded {path} (generatedAt {project.Registration.GeneratedAt:yyyy-MM-ddTHH:mm:sszzz} → {r.GeneratedAt:yyyy-MM-ddTHH:mm:sszzz})"], []);
    }

    private RegistrationRefreshInfo Regenerate(OverviewProject project, string path, bool dryRun, bool? includeExternal)
    {
        var g = options.RegistrationGenerator;
        var inputs = new GeneratorInputs
        {
            Project = project.Registration.Project, Env = project.Registration.Env, OverviewRoot = root,
            TerraformEnvDir = g.TerraformEnvDir, ServiceRepo = g.ServiceRepo, LocalDevDir = g.LocalDevDir, SamplesCatalog = g.SamplesCatalog,
            IncludeExternalSubscriptions = includeExternal ?? g.IncludeExternalSubscriptions,
        };
        GenerationResult result;
        try
        {
            result = Generator.Generate(inputs, Generator.ReadCurrent(path), DateTimeOffset.Now);
        }
        catch (Exception ex) when (ex is GeneratorInputException or FormatException or IOException)
        {
            throw new ApiException(StatusCodes.Status422UnprocessableEntity, "REGISTRATION_SOURCES_MISSING", ex.Message);
        }
        if (!result.IsValid)
            throw new ApiException(StatusCodes.Status422UnprocessableEntity, "REGISTRATION_INVALID",
                "Generated registration fails registration.schema.json (nothing written): " + string.Join("; ", result.SchemaErrors.Take(5)));

        string? backup = null;
        if (!dryRun)
        {
            var history = Path.Combine(Path.IsPathRooted(options.DataDir) ? options.DataDir : Path.Combine(root, options.DataDir), g.HistoryDir);
            backup = RegistrationWriter.Write(path, result, history, DateTimeOffset.Now);
            Load(path, dryRun: false);
            log.LogInformation("Registration {Id} regenerated into {Path} (backup {Backup}); {Gaps} gap(s)", project.Id, path, backup ?? "none", result.Gaps.Count);
        }
        var rec = result.Record;
        return new RegistrationRefreshInfo("generate", dryRun, !dryRun, path, backup, rec["generatedAt"]!.ToString(),
            (rec["apps"] as JsonArray)?.Count ?? 0, (rec["entities"] as JsonArray)?.Count ?? 0, (rec["edges"] as JsonArray)?.Count ?? 0,
            result.Diff, result.Gaps);
    }

    /// <summary>Validates and (unless dry run) swaps the project into the store.</summary>
    private OverviewProject Load(string path, bool dryRun)
    {
        OverviewProject loaded;
        try { loaded = ProjectStore.LoadProject(Loader(), path, root, log); }
        catch (RegistrationValidationException ex)
        {
            throw new ApiException(StatusCodes.Status422UnprocessableEntity, "REGISTRATION_INVALID", ex.Message);
        }
        if (!dryRun) store.Replace(loaded);
        return loaded;
    }
}

public static class RegistrationEndpoints
{
    public static void MapRegistration(this RouteGroupBuilder api)
    {
        // Contract (HANDOFF-ui-ux-data-contracts.md): returns ProjectSummary; the extra `refresh` field carries diff/gaps.
        api.MapPost("/projects/{id}/registration/refresh",
            async (string id, string? source, bool? dryRun, bool? includeExternal, RegistrationRefreshService s, CancellationToken ct) =>
                Results.Json(await s.RefreshAsync(id, source, dryRun ?? false, includeExternal, ct)));
    }
}
