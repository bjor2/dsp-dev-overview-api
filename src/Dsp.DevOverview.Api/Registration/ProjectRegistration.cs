using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dsp.DevOverview.Api.Registration;

/// <summary>
/// Typed view of an Architect registration (registration.schema.json). Read as-is; the overview never rewrites it.
/// Unknown/extra fields (namingNote, cloudMaxDeliveryCount, ...) are kept in <see cref="Extra"/>.
/// </summary>
public sealed class ProjectRegistration
{
    public string Id { get; set; } = "";
    public string Project { get; set; } = "";
    public string Env { get; set; } = "";
    public DateTimeOffset GeneratedAt { get; set; }
    /// <summary>ADDENDA-008: 2 after load (v1 files are upgraded in memory, see <see cref="UpgradeNotes"/>).</summary>
    public int SchemaVersion { get; set; } = 2;
    /// <summary>Apps stay as resources (attach/run/logs/cloudRoleName); they are no longer Flow nodes.</summary>
    public List<RegisteredApp> Apps { get; set; } = new();
    public List<RegisteredContainer> Containers { get; set; } = new();
    public List<RegisteredHandler> Handlers { get; set; } = new();
    public List<RegisteredEntity> Entities { get; set; } = new();
    public List<RegisteredEdge> Edges { get; set; } = new();
    /// <summary>ADDENDA-009: SB naming/namespace per host kind (emulator | developer-local-azure | cloud-dev). Optional.</summary>
    public Dictionary<string, RegisteredHost>? Hosts { get; set; }
    /// <summary>Checked-in default Flow layout (graph ids; runtime positions override per key). Optional.</summary>
    public RegisteredLayoutDefaults? LayoutDefaults { get; set; }
    public RegisteredSamples? Samples { get; set; }
    /// <summary>Slice 5: App Insights target (ADR-004). Optional in the schema.</summary>
    public RegisteredObservability? Observability { get; set; }

    [JsonIgnore] public string SourcePath { get; set; } = "";
    /// <summary>Set when the file was schema v1 and upgraded in memory (surfaced as a Flow warning).</summary>
    [JsonIgnore] public IReadOnlyList<string> UpgradeNotes { get; set; } = [];
    /// <summary>layoutDefaults ids that are not in the registration (ignored; surfaced as Flow warnings).</summary>
    [JsonIgnore] public IReadOnlyList<string> LayoutWarnings { get; set; } = [];

    public RegisteredEntity? FindEntity(string entityId) => Entities.FirstOrDefault(e => e.Id == entityId);
}

public sealed class RegisteredApp
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "";
    public string? CloudRoleName { get; set; }
    public RegisteredAppLocal? Local { get; set; }
}

/// <summary>ADDENDA-008 container: a labelled box around handler nodes; never an edge endpoint.</summary>
public sealed class RegisteredContainer
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    public string Kind { get; set; } = "resource";   // resource | logical
    public List<string> ResourceRefs { get; set; } = new();
    public string? Scope { get; set; }
}

/// <summary>ADDENDA-008 handler node: one [Function] + trigger, or a web app's HTTP surface.</summary>
public sealed class RegisteredHandler
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    public string AppId { get; set; } = "";
    public string ContainerId { get; set; } = "";
    public string? Scope { get; set; }
    public List<RegisteredTrigger> Triggers { get; set; } = new();
    public List<RegisteredSelfRequeue> SelfRequeue { get; set; } = new();
}

public sealed class RegisteredHost
{
    public string Namespace { get; set; } = "";
    public bool? Provisioned { get; set; }
    public string? Note { get; set; }
}

public sealed class RegisteredTrigger
{
    public string Kind { get; set; } = "";          // servicebus | http | timer | blob
    public string? Schedule { get; set; }           // timer
    public string? Path { get; set; }               // blob
    public string? Function { get; set; }
    public string? EntityId { get; set; }
    public string? Setting { get; set; }
    public string? Connection { get; set; }
    public string? SourceFile { get; set; }
}

public sealed class RegisteredSelfRequeue
{
    public string EntityId { get; set; } = "";
    public List<string> Messages { get; set; } = new();
    public List<string> Sources { get; set; } = new();
}

public sealed class RegisteredLayoutDefaults
{
    public Dictionary<string, RegisteredPoint> Positions { get; set; } = new(StringComparer.Ordinal);
    public string? Note { get; set; }
}

public sealed class RegisteredPoint
{
    public double X { get; set; }
    public double Y { get; set; }
}

public sealed class RegisteredAppLocal
{
    public int? Port { get; set; }
    public string? Url { get; set; }
    public string? RunScript { get; set; }
}

public sealed class RegisteredEntity
{
    public string Id { get; set; } = "";
    public string Kind { get; set; } = "";          // queue | topic | subscription | blobContainer (ADDENDA-009)

    /// <summary>blobContainer entities are blob storage: never probed, counted, peeked or purged on Service Bus.</summary>
    [JsonIgnore] public bool IsServiceBus => Kind is "queue" or "topic" or "subscription";
    public string LocalName { get; set; } = "";
    public string? CloudName { get; set; }
    public string? Topic { get; set; }              // subscription → topic entity id
    public string? ForwardTo { get; set; }
    public string? Owner { get; set; }              // project | shared
    public string? Scope { get; set; }              // local | cloud | both
    public int? MaxDeliveryCount { get; set; }      // local emulator value
    public int? CloudMaxDeliveryCount { get; set; } // ADDENDA-001 §3, not in the schema (extra field)

    public bool ExistsIn(SbScope scope) => scope switch
    {
        SbScope.Local => Scope is null or "local" or "both",
        SbScope.Cloud => Scope is null or "cloud" or "both",
        _ => false,
    };

    /// <summary>Scope-aware MaxDeliveryCount: local emulator 10, cloud 3 (ADDENDA-001 §3) unless the registration says otherwise.</summary>
    public int MaxDeliveryCountFor(SbScope scope) => scope == SbScope.Local
        ? MaxDeliveryCount ?? 10
        : CloudMaxDeliveryCount ?? 3;

    /// <summary>Local emulator name; cloud names (sbq-870-dev-euw-dahl-shared-*) are informational only in v1.</summary>
    public string NameFor(SbScope scope) => scope == SbScope.Local ? LocalName : CloudName ?? LocalName;
}

public sealed class RegisteredEdge
{
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    public string Kind { get; set; } = "";
    public string? Label { get; set; }
    public string? Scope { get; set; }
    public string? Confidence { get; set; }
    /// <summary>file:line of the send calls (code-attributed edges).</summary>
    public List<string>? Sources { get; set; }
}

public sealed class RegisteredSamples
{
    public string? Catalog { get; set; }
    public string? Source { get; set; }
}

public sealed class RegisteredObservability
{
    public RegisteredAppInsights? AppInsights { get; set; }
    /// <summary>Slice 7: TraceIt link (ADR-004, link-only in v1).</summary>
    public RegisteredTraceIt? TraceIt { get; set; }
}

public sealed class RegisteredTraceIt
{
    public string? Url { get; set; }
    public string? Status { get; set; }
}

public sealed class RegisteredAppInsights
{
    public string? Resource { get; set; }
    public string? ResourceGroup { get; set; }
    /// <summary>Subscription display name or id; names are resolved via ~/.azure/azureProfile.json.</summary>
    public string? Subscription { get; set; }
    public double? SamplingPercentage { get; set; }
    public List<string>? CloudRoleNames { get; set; }
}

public enum SbScope { Local, Cloud }

public static class RegistrationJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}
