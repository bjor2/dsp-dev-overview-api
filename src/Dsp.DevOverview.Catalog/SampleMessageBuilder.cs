using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Dsp.DevOverview.Catalog;

public sealed record SampleSendRequest(
    string Mode,
    string? Event = null,
    int? DataVersion = null,
    int? ProjectIndex = null,
    JsonNode? BodyOverride = null,
    IReadOnlyDictionary<string, object>? ApplicationPropertiesOverride = null,
    string? TargetEntityId = null);

/// <summary>A message ready to hand to a ServiceBusSender. Properties keep the kit's insertion order.</summary>
public sealed record BuiltMessage(
    int CatalogSeq,
    string TargetEntityId,
    string Subject,
    string? ContentType,
    IReadOnlyList<KeyValuePair<string, object>> ApplicationProperties,
    byte[] Body);

public sealed class SampleRequestException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>
/// Turns catalog entries into messages with the same body bytes, Subject, ContentType and application
/// properties as seed-messages/Program.cs, for a given clock. Pure apart from the injected clock/random sources.
/// </summary>
public sealed class SampleMessageBuilder
{
    public const string InjectedProperty = "x-overview-injected";
    public static readonly string[] Modes = ["all", "ingress", "commands", "one", "purge"];

    private readonly SampleCatalog _catalog;
    private readonly Func<DateTime> _utcNow;
    private readonly Func<int> _shardingBucket;
    private readonly Func<string> _correlationId;
    private readonly Func<DateTimeOffset> _utcNowOffset;

    public SampleMessageBuilder(SampleCatalog catalog, Func<DateTime>? utcNow = null, Func<int>? shardingBucket = null,
        Func<string>? correlationId = null, string? commandPropertyProfile = null, bool markInjected = true)
    {
        _catalog = catalog;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _utcNowOffset = utcNow is null ? () => DateTimeOffset.UtcNow : () => new DateTimeOffset(_utcNow(), TimeSpan.Zero);
        _shardingBucket = shardingBucket ?? (() => Random.Shared.Next(1, 13)); // QueueClientBase: 1-12
        _correlationId = correlationId ?? (() => Guid.NewGuid().ToString());   // CorrelationContextId.Init()
        CommandPropertyProfile = commandPropertyProfile ?? catalog.Commands.DefaultPropertyProfile;
        if (!catalog.Commands.PropertyProfiles.ContainsKey(CommandPropertyProfile))
            throw new ArgumentException($"Unknown command property profile '{CommandPropertyProfile}'. " +
                $"Known: {string.Join(", ", catalog.Commands.PropertyProfiles.Keys)}");
        MarkInjected = markInjected;
    }

    public string CommandPropertyProfile { get; }
    public bool MarkInjected { get; }

    public IReadOnlyList<BuiltMessage> Build(SampleSendRequest request)
    {
        var mode = request.Mode?.Trim().ToLowerInvariant() ?? "";
        if (request.DataVersion is < 1)
            throw new SampleRequestException("INVALID_DATA_VERSION", "dataVersion must be >= 1.");
        if (request.ProjectIndex is not null && request.ProjectIndex is < 1 or > 3)
            throw new SampleRequestException("INVALID_PROJECT_INDEX", "projectIndex must be 1, 2 or 3.");

        var messages = mode switch
        {
            "all" => [.. BuildIngress(request.DataVersion ?? _catalog.Ingress.DefaultDataVersion), .. BuildCommands()],
            "ingress" => BuildIngress(request.DataVersion ?? _catalog.Ingress.DefaultDataVersion),
            "commands" => BuildCommands(),
            "one" => [BuildOne(request.Event, request.DataVersion ?? _catalog.Ingress.DefaultDataVersion, request.ProjectIndex ?? 1)],
            "purge" => throw new SampleRequestException("PURGE_NOT_A_SEND", "mode 'purge' is handled by the purge endpoint, not the builder."),
            _ => throw new SampleRequestException("INVALID_MODE", $"mode must be one of {string.Join("|", Modes)}."),
        };
        return ApplyOverrides(messages, request);
    }

    private List<BuiltMessage> BuildIngress(int baseVersion)
    {
        var built = new List<BuiltMessage>();
        var bodiesBySeq = new Dictionary<int, byte[]>();
        foreach (var m in _catalog.IngressMessages)
        {
            var body = m.SameBodyAsSeq is { } same ? bodiesBySeq[same] : RenderIngressBody(m, baseVersion + m.DataVersionOffset, suffix: false);
            bodiesBySeq[m.Seq] = body;
            built.Add(ToBuilt(m, body, IngressProperties(m)));
        }
        return built;
    }

    private BuiltMessage BuildOne(string? eventName, int dataVersion, int projectIndex)
    {
        var m = _catalog.IngressMessages.FirstOrDefault(x => x.Variant == "base" && x.ProjectIndex == projectIndex && x.Subject == eventName)
            ?? throw new SampleRequestException("UNKNOWN_EVENT", $"event must be one of {string.Join(", ", _catalog.Events)}.");
        return ToBuilt(m, RenderIngressBody(m, dataVersion, suffix: true), IngressProperties(m));
    }

    private List<BuiltMessage> BuildCommands()
    {
        var profile = _catalog.Commands.PropertyProfiles[CommandPropertyProfile];
        return _catalog.CommandMessages.Select(m => ToBuilt(m, CanonicalBytes(m.Body), CommandProperties(profile))).ToList();
    }

    private byte[] RenderIngressBody(CatalogMessage m, int dataVersion, bool suffix)
    {
        var body = JsonNode.Parse(m.Body.GetRawText())!.AsObject();
        var now = _utcNow();
        var envelope = body["Envelope"]!.AsObject();
        envelope["DataVersion"] = dataVersion;
        envelope["LastModifiedUtc"] = JsonValue.Create(now.AddSeconds(dataVersion));
        if (_catalog.Ingress.DateFieldsMonthsFromNow.TryGetValue(m.Subject, out var dates))
            foreach (var (field, months) in dates)
                body[field] = YyyyMmDd(now.AddMonths(months));
        if (suffix && _catalog.Ingress.OneSuffixFields.TryGetValue(m.Subject, out var fields))
            foreach (var field in fields)
                body[field] = body[field]!.GetValue<string>() + string.Format(CultureInfo.InvariantCulture, _catalog.Ingress.OneSuffixFormat, dataVersion);
        // Default JsonSerializerOptions = what BinaryData(obj, type) uses in the kit (same escaping, same DateTime format).
        return JsonSerializer.SerializeToUtf8Bytes(body);
    }

    private static byte[] CanonicalBytes(JsonElement body) => JsonSerializer.SerializeToUtf8Bytes(JsonNode.Parse(body.GetRawText()));

    private static List<KeyValuePair<string, object>> IngressProperties(CatalogMessage m) =>
        m.ApplicationProperties.Select(p => new KeyValuePair<string, object>(p.Key, FromJson(p.Value))).ToList();

    private List<KeyValuePair<string, object>> CommandProperties(CommandPropertyProfile profile) =>
        profile.Properties.Select(p => new KeyValuePair<string, object>(p.Name, p.Runtime switch
        {
            null => FromJson(p.Value ?? throw new InvalidDataException($"Command property {p.Name} has neither value nor runtime.")),
            "shardingBucket" => _shardingBucket(),
            "correlationId" => _correlationId(),
            "utcNowRoundtrip" => _utcNowOffset().ToString("O"),
            var other => throw new InvalidDataException($"Unknown runtime property rule '{other}'."),
        })).ToList();

    private static BuiltMessage ToBuilt(CatalogMessage m, byte[] body, List<KeyValuePair<string, object>> props) =>
        new(m.Seq, m.TargetEntityId, m.Subject, m.ContentType, props, body);

    private List<BuiltMessage> ApplyOverrides(List<BuiltMessage> messages, SampleSendRequest request)
    {
        if (request.BodyOverride is not null && messages.Count != 1)
            throw new SampleRequestException("BODY_OVERRIDE_REQUIRES_SINGLE", $"bodyOverride replaces one message body; mode '{request.Mode}' sends {messages.Count}. Use mode 'one'.");

        return messages.Select(m =>
        {
            var props = m.ApplicationProperties.ToList();
            if (request.ApplicationPropertiesOverride is not null)
                foreach (var (key, value) in request.ApplicationPropertiesOverride)
                    Upsert(props, key, value);
            if (MarkInjected)
                Upsert(props, InjectedProperty, true);
            var body = request.BodyOverride is null ? m.Body : JsonSerializer.SerializeToUtf8Bytes(request.BodyOverride);
            return m with { ApplicationProperties = props, Body = body, TargetEntityId = request.TargetEntityId ?? m.TargetEntityId };
        }).ToList();
    }

    private static void Upsert(List<KeyValuePair<string, object>> props, string key, object value)
    {
        var i = props.FindIndex(p => p.Key == key);
        if (i >= 0) props[i] = new(key, value);
        else props.Add(new(key, value));
    }

    public static int YyyyMmDd(DateTime date) => date.Year * 10000 + date.Month * 100 + date.Day;

    /// <summary>JSON scalar → the CLR type Service Bus application properties accept.</summary>
    public static object FromJson(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => e.GetString()!,
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Number when e.TryGetInt32(out var i) => i,
        JsonValueKind.Number when e.TryGetInt64(out var l) => l,
        JsonValueKind.Number => e.GetDouble(),
        _ => throw new SampleRequestException("INVALID_PROPERTY_VALUE", $"Application property values must be string, number or bool (got {e.ValueKind})."),
    };
}
