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
    public List<RegisteredApp> Apps { get; set; } = new();
    public List<RegisteredEntity> Entities { get; set; } = new();
    public List<RegisteredEdge> Edges { get; set; } = new();
    public RegisteredSamples? Samples { get; set; }
    /// <summary>Slice 5: App Insights target (ADR-004). Optional in the schema.</summary>
    public RegisteredObservability? Observability { get; set; }

    [JsonIgnore] public string SourcePath { get; set; } = "";

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

public sealed class RegisteredAppLocal
{
    public int? Port { get; set; }
    public string? Url { get; set; }
    public string? RunScript { get; set; }
}

public sealed class RegisteredEntity
{
    public string Id { get; set; } = "";
    public string Kind { get; set; } = "";          // queue | topic | subscription
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
