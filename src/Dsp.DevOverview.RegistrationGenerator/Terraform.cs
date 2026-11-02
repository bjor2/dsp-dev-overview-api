using System.Text.RegularExpressions;

namespace Dsp.DevOverview.RegistrationGenerator;

/// <summary>
/// Values behind module.naming (Azure/naming with suffix [project_code, env, location_short, project_name]).
/// The stacks' terraform.tfvars.ci only hold placeholders, so these come from the registration's naming.values
/// (ADDENDA-001) or the defaults below.
/// </summary>
public sealed record TfNaming(string ProjectCode = "870", string Env = "dev", string LocationShort = "euw", string ProjectName = "dahl")
{
    public string Suffix => $"{ProjectCode}-{Env}-{LocationShort}-{ProjectName}";
    public string QueuePrefix => $"sbq-{Suffix}";
    public string TopicPrefix => $"sbt-{Suffix}";
    /// <summary>shared-message forces stack_name = "shared": every SB entity is sbq|sbt-{suffix}-shared-{name}.</summary>
    public string QueueName(string name) => $"{QueuePrefix}-shared-{name}";
    public string TopicName(string name) => $"{TopicPrefix}-shared-{name}";
    /// <summary>App resources: {web|func}-{suffix}-{app_name} (app_name = stack minus "app-").</summary>
    public string AppResource(string type, string appName) => $"{type}-{Suffix}-{appName}";
}

public enum SettingKind { Queue, Topic, Subscription, Connection, Other }

/// <summary>One ServiceBus* app setting. <see cref="ShortName"/> is the shared-message entity name (locals-sb-*.tf).</summary>
public sealed record SbSetting(string Key, SettingKind Kind, string RawValue, string? ShortName, string? CloudName);

public sealed record TfQueue(string Name, int MaxDeliveryCount, string? ForwardToQueue, string? ForwardToTopic);

public sealed record TfTopic(string Name);

/// <summary>
/// A subscription from locals-sb-subscription-*.tf, named and placed exactly like r-servicebus-entities.tf does:
/// v1 "{from}-to-{to}-subscription" on topic `to`, v2 "{from}-to-{to}" and v3 "{to}" on topic `from`.
/// Subscriptions are always created with max_delivery_count = 3.
/// </summary>
public sealed record TfSubscription(string From, string To, int Version, string? ForwardToQueue, string? ForwardToTopic, string? SqlFilter)
{
    public string Key => $"{From}-to-{To}";
    public string Name => Version switch { 2 => Key, 3 => To, _ => $"{Key}-subscription" };
    public string Topic => Version == 1 ? To : From;
    public const int MaxDeliveryCount = 3;
}

public sealed record TfServiceBus(IReadOnlyList<TfQueue> Queues, IReadOnlyList<TfTopic> Topics, IReadOnlyList<TfSubscription> Subscriptions)
{
    public TfQueue? Queue(string name) => Queues.FirstOrDefault(q => q.Name == name);
    public bool HasTopic(string name) => Topics.Any(t => t.Name == name);
}

public static partial class TerraformReader
{
    /// <summary>
    /// app_settings of `locals { &lt;local&gt; = { app_settings = { ... } } }` (web_app in locals-web.tf, function_app in
    /// locals-function.tf). Non-literal values are returned as their source text.
    /// </summary>
    public static IReadOnlyDictionary<string, string> ReadAppSettings(string tfFile, string localName)
    {
        var doc = Hcl.ParseFile(tfFile);
        if (doc.GetValueOrDefault("locals") is not Dictionary<string, object?> locals
            || locals.GetValueOrDefault(localName) is not Dictionary<string, object?> app
            || app.GetValueOrDefault("app_settings") is not Dictionary<string, object?> settings)
            throw new FormatException($"{tfFile}: no locals.{localName}.app_settings");
        return settings.ToDictionary(kv => kv.Key, kv => Text(kv.Value), StringComparer.Ordinal);
    }

    private static string Text(object? value) => value switch
    {
        null => "null",
        bool b => b ? "true" : "false",
        _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "",
    };

    [GeneratedRegex(@"^\$\{module\.naming\.servicebus_(?<kind>queue|topic)\.name\}-(?<rest>[A-Za-z0-9._-]+)$")]
    private static partial Regex NamingRef();

    /// <summary>Classifies the ServiceBus* settings: queue/topic names built from module.naming, literal subscription names, connections.</summary>
    public static IReadOnlyList<SbSetting> ServiceBusSettings(IReadOnlyDictionary<string, string> appSettings, TfNaming naming)
    {
        var list = new List<SbSetting>();
        foreach (var (key, value) in appSettings)
        {
            if (!key.StartsWith("ServiceBus", StringComparison.Ordinal)) continue;
            var m = NamingRef().Match(value);
            if (m.Success)
            {
                var rest = m.Groups["rest"].Value;
                var shortName = rest.StartsWith("shared-", StringComparison.Ordinal) ? rest["shared-".Length..] : rest;
                var isQueue = m.Groups["kind"].Value == "queue";
                var cloud = $"{(isQueue ? naming.QueuePrefix : naming.TopicPrefix)}-{rest}";
                list.Add(new SbSetting(key, isQueue ? SettingKind.Queue : SettingKind.Topic, value, shortName, cloud));
            }
            else if (key.EndsWith("Subscription", StringComparison.Ordinal) && !value.Contains("${"))
                list.Add(new SbSetting(key, SettingKind.Subscription, value, value, value));
            else if (key.Contains("Connection", StringComparison.Ordinal) || key.Contains("Namespace", StringComparison.Ordinal))
                list.Add(new SbSetting(key, SettingKind.Connection, value, null, null));
            else
                list.Add(new SbSetting(key, SettingKind.Other, value, null, null));
        }
        return list;
    }

    /// <summary>Reads queues/topics/subscriptions from unprovisioned_shared-message/locals-sb-{queue,topic,subscription}-*.tf.</summary>
    public static TfServiceBus ReadServiceBus(string sharedMessageDir, IEnumerable<string>? onlyFiles = null)
    {
        var files = onlyFiles?.Select(f => Path.Combine(sharedMessageDir, f)).ToList()
            ?? Directory.GetFiles(sharedMessageDir, "locals-sb-*.tf").OrderBy(f => f, StringComparer.Ordinal).ToList();
        var queues = new List<TfQueue>();
        var topics = new List<TfTopic>();
        var subs = new List<TfSubscription>();
        foreach (var file in files.Where(File.Exists))
        {
            var name = Path.GetFileName(file);
            var locals = Hcl.ParseFile(file).GetValueOrDefault("locals") as Dictionary<string, object?> ?? [];
            foreach (var (_, value) in locals)
            {
                if (value is not List<object?> items) continue;
                foreach (var item in items.OfType<Dictionary<string, object?>>())
                {
                    if (name.StartsWith("locals-sb-queue", StringComparison.Ordinal) && Str(item, "name") is { } q)
                        queues.Add(new TfQueue(q, Int(item, "max_delivery_count") ?? 3, Str(item, "forward_to_queue"), Str(item, "forward_to_topic")));
                    else if (name.StartsWith("locals-sb-topic", StringComparison.Ordinal) && Str(item, "name") is { } t)
                        topics.Add(new TfTopic(t));
                    else if (name.StartsWith("locals-sb-subscription", StringComparison.Ordinal) && Str(item, "from") is { } from && Str(item, "to") is { } to)
                        subs.Add(new TfSubscription(from, to, Int(item, "version") ?? 1, Str(item, "forward_to_queue"), Str(item, "forward_to_topic"), Str(item, "sql_filter")));
                }
            }
        }
        return new TfServiceBus(queues, topics, subs);
    }

    private static string? Str(Dictionary<string, object?> o, string key) => o.GetValueOrDefault(key) as string;

    private static int? Int(Dictionary<string, object?> o, string key) => o.GetValueOrDefault(key) switch
    {
        long l => (int)l,
        string s when int.TryParse(s, out var i) => i,
        _ => null,
    };

    [GeneratedRegex(@"sys\.label\s*=\s*'(?<label>[^']+)'", RegexOptions.IgnoreCase)]
    private static partial Regex LabelFilter();

    /// <summary>"(sys.label = 'A' OR sys.Label = 'B')" → "A, B" (edge label); null when the filter is not label-based.</summary>
    public static string? LabelsFromFilter(string? sqlFilter)
    {
        if (string.IsNullOrWhiteSpace(sqlFilter)) return null;
        var labels = LabelFilter().Matches(sqlFilter).Select(m => m.Groups["label"].Value).Distinct().ToList();
        return labels.Count > 0 ? string.Join(", ", labels) : null;
    }
}
