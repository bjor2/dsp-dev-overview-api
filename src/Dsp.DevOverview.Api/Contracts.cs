using System.Text.Json;
using System.Text.Json.Serialization;
using Dsp.DevOverview.Catalog;

namespace Dsp.DevOverview.Api;

// Shapes from architecture/HANDOFF-ui-ux-data-contracts.md and ADDENDUM-ui-wireframe-q.md.

public sealed record ApiError(string Code, string Message, bool Retryable);
public sealed record ApiErrorEnvelope(ApiError Error);

public sealed class ApiException(int status, string code, string message, bool retryable = false) : Exception(message)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
    public bool Retryable { get; } = retryable;
}

public sealed record HealthDetail(int AppsUp, int AppsTotal, bool SbReachable, bool AiQueryOk);

public sealed record ProjectSummary(
    string Id, string Project, string Env, string Health, HealthDetail HealthDetail,
    int AppCount, int QueueCount, int? DlqTotal, string RegistrationGeneratedAt,
    // Slice 7: TraceIt link only (ADR-004); null when not configured.
    string? TraceItUrl = null);

/// <summary>ADDENDUM-ui-wireframe-q §1, field for field.</summary>
public sealed record EntityStatus(
    string EntityId, string Kind, string Label, long? ActiveCount, long? DeadLetterCount, bool DlqHot, string Owner,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ParentEntityId);

public record PeekedMessage(
    long SequenceNumber, string MessageId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Subject,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ContentType,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? CorrelationId,
    string EnqueuedTime, int DeliveryCount, IReadOnlyDictionary<string, object> ApplicationProperties,
    object? Body, bool BodyTruncated);

/// <summary>DlqMessage extends PeekedMessage (HANDOFF shapes); the dead-letter fields are omitted when the broker didn't set them.</summary>
public sealed record DlqMessage(
    long SequenceNumber, string MessageId, string? Subject, string? ContentType, string? CorrelationId,
    string EnqueuedTime, int DeliveryCount, IReadOnlyDictionary<string, object> ApplicationProperties,
    object? Body, bool BodyTruncated,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? DeadLetterReason,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? DeadLetterErrorDescription,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? DeadLetterSource)
    : PeekedMessage(SequenceNumber, MessageId, Subject, ContentType, CorrelationId, EnqueuedTime, DeliveryCount, ApplicationProperties, Body, BodyTruncated);

/// <summary>QueuePeek + the active scope's MaxDeliveryCount (local emulator 10, cloud 3) for delivery/DLQ hints.</summary>
public sealed record QueuePeek(string EntityId, long? ActiveCount, long? DeadLetterCount, string Scope, int MaxDeliveryCount, IReadOnlyList<PeekedMessage> Messages);

public sealed class SampleSendBody
{
    public string? Mode { get; set; }
    public string? Event { get; set; }
    public int? DataVersion { get; set; }
    public int? ProjectIndex { get; set; }
    /// <summary>Body only (ADDENDUM §3).</summary>
    public JsonElement? BodyOverride { get; set; }
    /// <summary>Merges over the kit properties, override wins per key.</summary>
    public Dictionary<string, JsonElement>? ApplicationPropertiesOverride { get; set; }
    public string? TargetEntityId { get; set; }
}

public sealed record SentToEntity(string EntityId, string EntityName, int Sent);
public sealed record SampleSendResult(int Sent, string? Entity, IReadOnlyList<SentToEntity> Entities, string? CommandPropertyProfile);

/// <summary>Body of POST …/dlq/retry and …/dlq/discard: <c>{"sequenceNumbers":[1,2]}</c>, or <c>"all"</c> (retry only).</summary>
public sealed class DlqActionBody
{
    public JsonElement SequenceNumbers { get; set; }
}

/// <summary>Which DLQ messages to act on: explicit sequence numbers, or everything in the DLQ.</summary>
public sealed record DlqSelection(bool All, IReadOnlySet<long> SequenceNumbers)
{
    public static DlqSelection Parse(JsonElement value, bool allowAll)
    {
        if (allowAll && value.ValueKind == JsonValueKind.String && string.Equals(value.GetString(), "all", StringComparison.OrdinalIgnoreCase))
            return new(true, new HashSet<long>());
        if (value.ValueKind == JsonValueKind.Array)
        {
            var set = new HashSet<long>();
            foreach (var item in value.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Number || !item.TryGetInt64(out var seq) || seq < 0)
                    throw Invalid(allowAll, $"'{item.GetRawText()}' is not a sequence number");
                set.Add(seq);
            }
            if (set.Count > 0) return new(false, set);
        }
        throw Invalid(allowAll, "sequenceNumbers is missing or empty");
    }

    private static ApiException Invalid(bool allowAll, string why) =>
        new(StatusCodes.Status400BadRequest, "INVALID_SEQUENCE_NUMBERS",
            $"{why}; expected {{\"sequenceNumbers\":[<seq>,...]}}{(allowAll ? " or {\"sequenceNumbers\":\"all\"}" : "")}.");
}

/// <summary>{retried:n} + where the clones went and which requested sequence numbers were not in the DLQ.</summary>
public sealed record DlqRetryResult(int Retried, string EntityId, string TargetEntityId, IReadOnlyList<long> SequenceNumbers, IReadOnlyList<long> NotFound);
public sealed record DlqDiscardResult(int Discarded, string EntityId, IReadOnlyList<long> SequenceNumbers, IReadOnlyList<long> NotFound);

public sealed record PurgedEntity(string EntityId, string EntityName, bool DeadLetter, int Removed);
public sealed record PurgeResult(int Purged, IReadOnlyList<PurgedEntity> Entities);

public sealed record SampleProjectDto(int Index, string CustomerId, string Name, string OrderNumber, bool Active);

public sealed record SampleMessageDto(
    string PresetId, string Subject, string TargetEntityId, IReadOnlyDictionary<string, object> ApplicationProperties, JsonElement Body,
    int Seq, string? ContentType, int? ProjectIndex, string Variant, IReadOnlyList<string> RuntimeApplicationProperties);

public sealed record SampleMessageCatalogDto(
    string ProjectId, string Source, string MirroredAt, DateTime ReferenceUtc, string CommandPropertyProfile,
    IReadOnlyList<SampleProjectDto> SampleProjects, IReadOnlyList<SamplePreset> Presets, IReadOnlyList<SampleMessageDto> Messages);
