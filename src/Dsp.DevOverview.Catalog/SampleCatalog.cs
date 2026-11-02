using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dsp.DevOverview.Catalog;

/// <summary>
/// On-disk shape of <c>catalog/customerproject.samples.json</c>. A mirror of seed-messages/Program.cs:
/// bodies are stored as the kit serializes them at <see cref="ReferenceUtc"/>; the time- and version-dependent
/// fields are re-rendered by <see cref="SampleMessageBuilder"/> at send time.
/// </summary>
public sealed class SampleCatalog
{
    public string ProjectId { get; set; } = "";
    public string Source { get; set; } = "";
    public string SourceSha256 { get; set; } = "";
    public string MirroredAt { get; set; } = "";
    public string Generator { get; set; } = "";
    public DateTime ReferenceUtc { get; set; }
    public Dictionary<string, string> ContractPackages { get; set; } = new();
    public List<SampleProjectInfo> SampleProjects { get; set; } = new();
    public List<string> Events { get; set; } = new();
    public IngressSpec Ingress { get; set; } = new();
    public CommandSpec Commands { get; set; } = new();
    public List<string> PurgeEntityIds { get; set; } = new();
    public List<SamplePreset> Presets { get; set; } = new();
    public List<CatalogMessage> Messages { get; set; } = new();

    public static readonly JsonSerializerOptions FileJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static SampleCatalog Load(string path) =>
        JsonSerializer.Deserialize<SampleCatalog>(File.ReadAllText(path), FileJsonOptions)
        ?? throw new InvalidDataException($"Empty sample catalog: {path}");

    public string ToJson() => JsonSerializer.Serialize(this, FileJsonOptions);

    public IEnumerable<CatalogMessage> IngressMessages => Messages.Where(m => m.PresetId == PresetIds.Ingress).OrderBy(m => m.Seq);
    public IEnumerable<CatalogMessage> CommandMessages => Messages.Where(m => m.PresetId == PresetIds.Commands).OrderBy(m => m.Seq);
}

public static class PresetIds
{
    public const string Ingress = "ingress-all";
    public const string One = "one";
    public const string Commands = "commands";
    public const string All = "all";
    public const string Purge = "purge";
}

public sealed class SampleProjectInfo
{
    public int Index { get; set; }
    public string CustomerId { get; set; } = "";
    public string Name { get; set; } = "";
    public string OrderNumber { get; set; } = "";
    public bool Active { get; set; }
    public string Email { get; set; } = "";
    public string Phone { get; set; } = "";
    public string AddressNumber { get; set; } = "";
    public string AddressLine1 { get; set; } = "";
    public string AddressLine2 { get; set; } = "";
    public string PostalCode { get; set; } = "";
    public string City { get; set; } = "";
    public string ProjectName { get; set; } = "";
}

public sealed class IngressSpec
{
    public string TargetEntityId { get; set; } = "";
    public string ContentType { get; set; } = "";
    public Dictionary<string, JsonElement> ApplicationProperties { get; set; } = new();
    public int DefaultDataVersion { get; set; } = 1;
    /// <summary>Envelope.LastModifiedUtc = utcNow + DataVersion seconds (kit: NewEnvelope).</summary>
    public string LastModifiedRule { get; set; } = "";
    /// <summary>Per event: int yyyyMMdd fields rendered as utcNow.AddMonths(n).</summary>
    public Dictionary<string, Dictionary<string, int>> DateFieldsMonthsFromNow { get; set; } = new();
    /// <summary>Mode one: per event, string fields that get <see cref="OneSuffixFormat"/> appended.</summary>
    public Dictionary<string, List<string>> OneSuffixFields { get; set; } = new();
    public string OneSuffixFormat { get; set; } = " (v{0})";
}

public sealed class CommandSpec
{
    public string TargetEntityId { get; set; } = "";
    public string? ContentType { get; set; }
    public string DefaultPropertyProfile { get; set; } = "";
    public Dictionary<string, CommandPropertyProfile> PropertyProfiles { get; set; } = new();
}

public sealed class CommandPropertyProfile
{
    public string Description { get; set; } = "";
    /// <summary>In the order QueueClientBase adds them.</summary>
    public List<CommandProperty> Properties { get; set; } = new();
}

public sealed class CommandProperty
{
    public string Name { get; set; } = "";
    /// <summary>Static value; null when <see cref="Runtime"/> is set.</summary>
    public JsonElement? Value { get; set; }
    /// <summary>shardingBucket (int 1-12) | correlationId (guid string) | utcNowRoundtrip (DateTimeOffset "O").</summary>
    public string? Runtime { get; set; }
}

public sealed class SamplePreset
{
    public string Id { get; set; } = "";
    public string Mode { get; set; } = "";
    public string Label { get; set; } = "";
    public string? TargetEntityId { get; set; }
    public int MessageCount { get; set; }
    public SamplePresetParams? Params { get; set; }
    public bool Destructive { get; set; }
}

public sealed class SamplePresetParams
{
    public List<string>? Event { get; set; }
    public int? DataVersion { get; set; }
    public List<int>? ProjectIndex { get; set; }
}

public sealed class CatalogMessage
{
    public int Seq { get; set; }
    public string PresetId { get; set; } = "";
    public string Subject { get; set; } = "";
    public string TargetEntityId { get; set; } = "";
    public string? ContentType { get; set; }
    public Dictionary<string, JsonElement> ApplicationProperties { get; set; } = new();
    public JsonElement Body { get; set; }
    /// <summary>SHA-256 of the exact UTF-8 body bytes the kit produces at <see cref="SampleCatalog.ReferenceUtc"/>.</summary>
    public string BodySha256AtReference { get; set; } = "";
    public int? ProjectIndex { get; set; }
    /// <summary>base | renamed | stale | duplicate | command</summary>
    public string Variant { get; set; } = "";
    public int DataVersionOffset { get; set; }
    /// <summary>The kit sends the same object instance twice (duplicate); reuse that body byte-for-byte.</summary>
    public int? SameBodyAsSeq { get; set; }
}
