using Dsp.DevOverview.Api.AppConsole;
using Dsp.DevOverview.Api.Registration;
using Dsp.DevOverview.Catalog;

namespace Dsp.DevOverview.Api;

public sealed class OverviewOptions
{
    /// <summary>dev-overview root. Empty = walk up from the content root to the folder holding Dsp.DevOverview.sln.</summary>
    public string? Root { get; set; }
    public string RegistrationSchema { get; set; } = "architecture/registration.schema.json";
    /// <summary>Paths relative to the root. (No collection default: the config binder would append to it.)</summary>
    public List<string> Registrations { get; set; } = [];
    public ServiceBusOptions ServiceBus { get; set; } = new();
    /// <summary>Overview-owned state (flow positions), relative to the root unless absolute. Never inside service repos.</summary>
    public string DataDir { get; set; } = "data";
    public string? CommandPropertyProfile { get; set; }
    /// <summary>Slice 4 console (process manager, ring buffer, SSE). Log files go to {DataDir}/{Console.LogDir}.</summary>
    public ConsoleOptions Console { get; set; } = new();
    /// <summary>Slice 5 App Insights query (ADR-004). Target comes from the registration unless overridden here.</summary>
    public AppInsights.AppInsightsOptions AppInsights { get; set; } = new();
    /// <summary>Slice 6 registration generator inputs for POST /registration/refresh (ADR-002).</summary>
    public RegistrationRefreshOptions RegistrationGenerator { get; set; } = new();
    /// <summary>Slice 7 probe caching for GET /api/projects.</summary>
    public Health.HealthOptions Health { get; set; } = new();
    /// <summary>Slice 7 TraceIt link (ProjectSummary.traceItUrl).</summary>
    public Health.TraceItOptions TraceIt { get; set; } = new();

    public string ResolveRoot(string contentRoot)
    {
        if (!string.IsNullOrWhiteSpace(Root)) return Path.GetFullPath(Root);
        for (var dir = new DirectoryInfo(contentRoot); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Dsp.DevOverview.sln"))) return dir.FullName;
        throw new DirectoryNotFoundException($"Overview:Root not set and Dsp.DevOverview.sln not found above {contentRoot}");
    }
}

public sealed class ServiceBusOptions
{
    /// <summary>local (shared-stack emulator) is the only writable scope in v1.</summary>
    public SbScope Scope { get; set; } = SbScope.Local;
    public string ConnectionString { get; set; } = "";
    /// <summary>Optional. Empty + emulator = same endpoint on the emulator management port (5300).</summary>
    public string? AdminConnectionString { get; set; }
    public int EmulatorAdminPort { get; set; } = 5300;
    public int PeekCountCap { get; set; } = 250;
    /// <summary>
    /// Use ServiceBusAdministrationClient runtime properties for depths. Null = auto: off for the emulator, whose
    /// management endpoint (:5300) answers but reports 0 active/dead-letter messages (observed 2026-11-02), so peek
    /// counts are the truth there.
    /// </summary>
    public bool? UseAdminCounts { get; set; }

    public bool AdminCountsEnabled => UseAdminCounts ?? !IsEmulator;

    public bool IsEmulator => ConnectionString.Contains("UseDevelopmentEmulator=true", StringComparison.OrdinalIgnoreCase);
}

/// <summary>A registered project: the Architect registration plus its mirrored sample catalog.</summary>
public sealed record OverviewProject(ProjectRegistration Registration, SampleCatalog? Catalog, string? CatalogPath, string? CatalogError)
{
    public string Id => Registration.Id;
}

public sealed class ProjectStore
{
    // Swapped as a whole on hot reload (slice 6 POST /registration/refresh); readers always see one consistent snapshot.
    private volatile Dictionary<string, OverviewProject> _projects;
    private readonly object _swap = new();

    public ProjectStore(IEnumerable<OverviewProject> projects) => _projects = projects.ToDictionary(p => p.Id);

    public IEnumerable<OverviewProject> All => _projects.Values;
    public OverviewProject? Find(string id) => _projects.GetValueOrDefault(id);

    /// <summary>Hot reload: replaces (or adds) one project. Services resolve projects per request, so the next call sees it.</summary>
    public void Replace(OverviewProject project)
    {
        lock (_swap) _projects = new Dictionary<string, OverviewProject>(_projects) { [project.Id] = project };
    }

    public static ProjectStore Load(OverviewOptions options, string root, ILogger log)
    {
        var loader = new RegistrationLoader(Path.Combine(root, options.RegistrationSchema));
        var projects = new List<OverviewProject>();
        foreach (var relative in options.Registrations.Distinct())
            projects.Add(LoadProject(loader, Path.Combine(root, relative), root, log)); // invalid registration = startup failure, by design
        return new ProjectStore(projects);
    }

    /// <summary>One registration (schema-validated, throws when invalid) plus its sample catalog.</summary>
    public static OverviewProject LoadProject(RegistrationLoader loader, string path, string root, ILogger log)
    {
        var registration = loader.Load(path);
        log.LogInformation("Registration {Id} loaded from {Path} (schema-valid)", registration.Id, registration.SourcePath);

        var catalogPath = registration.Samples?.Catalog is { Length: > 0 } c ? Path.Combine(root, c) : null;
        SampleCatalog? catalog = null;
        string? error = null;
        if (catalogPath is null) error = "registration has no samples.catalog";
        else if (!File.Exists(catalogPath)) error = $"catalog not found: {catalogPath}";
        else
        {
            catalog = SampleCatalog.Load(catalogPath);
            log.LogInformation("Sample catalog {Path}: {Count} messages", catalogPath, catalog.Messages.Count);
        }
        if (error is not null) log.LogWarning("Project {Id}: {Error}", registration.Id, error);
        return new OverviewProject(registration, catalog, catalogPath, error);
    }
}
