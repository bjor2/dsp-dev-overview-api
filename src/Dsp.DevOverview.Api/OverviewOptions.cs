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
    /// <summary>"Render" when the API runs on Render (also auto-detected from the RENDER env var Render sets). Only changes error copy.</summary>
    public string? Hosting { get; set; }

    public static bool DetectRender(string? hosting, string? renderEnv) =>
        string.Equals(hosting, "Render", StringComparison.OrdinalIgnoreCase) || !string.IsNullOrWhiteSpace(renderEnv);
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

    /// <summary>Budget for one entity's depth read (active + DLQ in parallel). Over budget = unknown (null), never a hang.</summary>
    public double CountTimeoutSeconds { get; set; } = 3;
    /// <summary>Budget for the reachability probe; over budget = unreachable.</summary>
    public double ProbeTimeoutSeconds { get; set; } = 3;
    /// <summary>Budget for probes until one has succeeded (first AAD token + AMQP/WebSocket link can take several seconds).</summary>
    public double ColdProbeTimeoutSeconds { get; set; } = 12;
    /// <summary>A successful probe is reused this long (no new link per request; failures are never cached).</summary>
    public double ProbeCacheSeconds { get; set; } = 10;
    /// <summary>MessagingEntityNotFound is remembered this long (e.g. emulator-only entities on a developer namespace).</summary>
    public double MissingEntityCacheSeconds { get; set; } = 300;
    /// <summary>Depth reads are shared between /flow, /sb/entities and /projects for this long.</summary>
    public double CountsCacheSeconds { get; set; } = 3;
    /// <summary>Entities read in parallel per request.</summary>
    public int CountParallelism { get; set; } = 8;

    /// <summary>
    /// e.g. sb-bjoo-local-noe-dahl-shared.servicebus.windows.net. When set (and ConnectionString empty) the clients use
    /// DefaultAzureCredential (az login / VS / env / managed identity) instead of a SAS connection string.
    /// </summary>
    public string? FullyQualifiedNamespace { get; set; }

    /// <summary>AmqpTcp | AmqpWebSockets. Null = auto: AmqpTcp for the emulator, AmqpWebSockets (443) otherwise (box blocks 5671).</summary>
    public Azure.Messaging.ServiceBus.ServiceBusTransportType? TransportType { get; set; }

    public Azure.Messaging.ServiceBus.ServiceBusTransportType EffectiveTransport => TransportType
        ?? (IsEmulator ? Azure.Messaging.ServiceBus.ServiceBusTransportType.AmqpTcp : Azure.Messaging.ServiceBus.ServiceBusTransportType.AmqpWebSockets);

    /// <summary>Set at startup from Overview:Hosting / RENDER. On Render, SB errors point at the cloud API's SB config, not the shared stack.</summary>
    public bool HostedOnRender { get; set; }

    /// <summary>
    /// Option (b): base URL of another Overview API (e.g. the box API behind ngrok). When set, the SB endpoints (entities, peek,
    /// dlq list/retry/discard, samples/send, purge) are forwarded to {ProxyUpstream}/api/... and health sbReachable comes from
    /// its /api/projects. Off (empty) by default.
    /// </summary>
    public string? ProxyUpstream { get; set; }
    public int ProxyTimeoutSeconds { get; set; } = 30;
    public bool ProxyEnabled => !string.IsNullOrWhiteSpace(ProxyUpstream);

    public const string RenderNotConfiguredMessage =
        "Render-hosted Overview API has no Service Bus credential configured; set AZURE_TENANT_ID/AZURE_CLIENT_ID/AZURE_CLIENT_SECRET + " +
        "Overview__ServiceBus__FullyQualifiedNamespace, or enable SB proxy to box API (Overview__ServiceBus__ProxyUpstream). " +
        "This is the cloud API's Service Bus config, not the local shared stack.";

    public string NotConfiguredMessage => HostedOnRender ? RenderNotConfiguredMessage
        : "Set Overview:ServiceBus:ConnectionString or Overview:ServiceBus:FullyQualifiedNamespace (see README).";

    /// <summary>SB_AUTH_FAILED text: the credential was rejected or lacks RBAC on the namespace (host-aware).</summary>
    public string AuthFailedMessage => HostedOnRender
        ? $"Render-hosted Overview API: Service Bus credential rejected for {HostName ?? "(unknown)"}. Check the cloud API's AZURE_TENANT_ID/AZURE_CLIENT_ID/AZURE_CLIENT_SECRET " +
          "(service principal, secret not expired) and its Azure Service Bus Data Owner role on the namespace (ADDENDA-005, RUNBOOK-render-sb-sp.md)."
        : AuthMode == SbAuthMode.ConnectionString
        ? $"Service Bus rejected the connection string credential for {HostName ?? "(unknown)"}."
        : $"Service Bus credential rejected for {HostName ?? "(unknown)"}: check az login (DefaultAzureCredential) and Azure Service Bus Data Owner/Sender/Receiver RBAC on the namespace.";

    /// <summary>Host-kind aware SB_UNREACHABLE text (no "emulator" wording for Azure namespaces; cloud-API wording on Render).</summary>
    public string UnreachableMessage =>
        HostedOnRender && AuthMode == SbAuthMode.None ? RenderNotConfiguredMessage
        : HostedOnRender ? $"Render-hosted Overview API cannot reach Service Bus namespace {HostName ?? "(unknown)"} ({HostKind}, {EffectiveTransport}). " +
            "Check the cloud API's Service Bus config (Overview__ServiceBus__FullyQualifiedNamespace, AmqpWebSockets/443 egress). " +
            "Not a local shared-stack problem."
        : AuthMode == SbAuthMode.None ? NotConfiguredMessage
        : HostKind == SbHostKind.Emulator
        ? "Service Bus emulator unreachable (is the shared stack up? pwsh /workspace/dsp/shared-stack/Test-DspStack.ps1)."
        : $"Service Bus namespace {HostName ?? "(unknown)"} unreachable ({HostKind}, {EffectiveTransport}); check az login / RBAC / network.";

    public SbAuthMode AuthMode =>
        !string.IsNullOrWhiteSpace(ConnectionString) ? SbAuthMode.ConnectionString
        : !string.IsNullOrWhiteSpace(FullyQualifiedNamespace) ? SbAuthMode.Credential
        : SbAuthMode.None;

    /// <summary>Normalised FQNS (strips sb:// and trailing slash).</summary>
    public string? NormalizedNamespace => FullyQualifiedNamespace?.Trim().Replace("sb://", "", StringComparison.OrdinalIgnoreCase).TrimEnd('/');

    public bool IsEmulator => AuthMode == SbAuthMode.ConnectionString
        && ConnectionString.Contains("UseDevelopmentEmulator=true", StringComparison.OrdinalIgnoreCase);

    private static readonly System.Text.RegularExpressions.Regex DevLocal =
        new(@"^sb-(?<dev>[a-z0-9]+)-local-noe-dahl-shared\.servicebus\.windows\.net$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private string? HostName => AuthMode == SbAuthMode.Credential ? NormalizedNamespace
        : System.Text.RegularExpressions.Regex.Match(ConnectionString, @"Endpoint=sb://([^;/:]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase) is { Success: true } m ? m.Groups[1].Value : null;

    /// <summary>ADDENDA-004 host kind: emulator | developer-local-azure (sb-{dev}-local-noe-dahl-shared) | cloud-dev.</summary>
    public SbHostKind HostKind => IsEmulator ? SbHostKind.Emulator
        : HostName is { } h && DevLocal.IsMatch(h) ? SbHostKind.DeveloperLocalAzure
        : SbHostKind.CloudDev;

    public string? DeveloperName => HostName is { } h && DevLocal.Match(h) is { Success: true } m ? m.Groups["dev"].Value.ToLowerInvariant() : null;

    /// <summary>Emulator + developer-local-azure are personal sandboxes: inject / DLQ retry / discard / purge (confirm) allowed.</summary>
    public bool IsSandbox => HostKind != SbHostKind.CloudDev;

    /// <summary>Kept for tests: which registration name family applies.</summary>
    public SbScope EntityNameScope => HostKind == SbHostKind.CloudDev ? SbScope.Cloud : SbScope.Local;

    private static readonly System.Text.RegularExpressions.Regex CloudShort =
        new(@"^sb[qt]-[a-z0-9]+-[a-z]+-[a-z]+-dahl-shared-(?<short>.+)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>
    /// ADDENDA-004 &lt;short&gt;: taken from the cloud name (sbt-870-dev-euw-dahl-shared-customerproject → customerproject), since the
    /// emulator localName can differ (customerproject-topic); falls back to localName for local-only entities.
    /// </summary>
    public static string ShortName(Registration.RegisteredEntity e) =>
        e.CloudName is { } c && CloudShort.Match(c) is { Success: true } m ? m.Groups["short"].Value : e.LocalName;

    /// <summary>Wire label for the host kind: emulator | developer-local-azure | cloud-dev.</summary>
    public string HostKindLabel => HostKind switch
    {
        SbHostKind.Emulator => "emulator",
        SbHostKind.DeveloperLocalAzure => "developer-local-azure",
        _ => "cloud-dev",
    };

    /// <summary>Entity name on this host: emulator=localName; dev-local=sbq/sbt-{dev}-local-noe-dahl-shared-{localName}; cloud-dev=cloudName.</summary>
    public string EntityName(Registration.RegisteredEntity e) => HostKind switch
    {
        SbHostKind.Emulator => e.LocalName,
        SbHostKind.DeveloperLocalAzure => e.Kind switch
        {
            "queue" => $"sbq-{DeveloperName}-local-noe-dahl-shared-{ShortName(e)}",
            "topic" => $"sbt-{DeveloperName}-local-noe-dahl-shared-{ShortName(e)}",
            _ => e.LocalName, // subscription names are scoped under the (derived) topic
        },
        _ => e.CloudName ?? e.LocalName,
    };
}

public enum SbAuthMode { None, ConnectionString, Credential }
public enum SbHostKind { Emulator, DeveloperLocalAzure, CloudDev }

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

        var noKit = registration.Samples?.Catalog is "none"; // ADDENDA-009 (price): no seed kit — a state, not a problem
        var catalogPath = !noKit && registration.Samples?.Catalog is { Length: > 0 } c ? Path.Combine(root, c) : null;
        SampleCatalog? catalog = null;
        string? error = null;
        if (noKit) error = $"no sample kit for {registration.Id} (samples.catalog = none{(registration.Samples?.Source is { } src ? $"; {src}" : "")})";
        else if (catalogPath is null) error = "registration has no samples.catalog";
        else if (!File.Exists(catalogPath)) error = $"catalog not found: {catalogPath}";
        else
        {
            catalog = SampleCatalog.Load(catalogPath);
            log.LogInformation("Sample catalog {Path}: {Count} messages", catalogPath, catalog.Messages.Count);
        }
        if (error is not null && noKit) log.LogInformation("Project {Id}: {Error}", registration.Id, error);
        else if (error is not null) log.LogWarning("Project {Id}: {Error}", registration.Id, error);
        return new OverviewProject(registration, catalog, catalogPath, error);
    }
}
