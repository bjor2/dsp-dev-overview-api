using System.Text.Json;
using System.Text.RegularExpressions;

namespace Dsp.DevOverview.RegistrationGenerator;

/// <summary>A [ServiceBusTrigger] found in source. <see cref="Setting"/> is set for "%Setting%" names, else <see cref="LiteralName"/>.</summary>
public sealed record FoundTrigger(string Function, string? Setting, string? LiteralName, string? Connection, string SourceFile);

public static partial class TriggerScanner
{
    [GeneratedRegex(@"\[\s*(?:Function|FunctionName)\s*\(\s*(?:""(?<fn>[^""]+)""|nameof\((?<fn>\w+)\))\s*\)\s*\]")]
    private static partial Regex FunctionAttribute();

    [GeneratedRegex(@"\[\s*ServiceBusTrigger\s*\(\s*""(?<name>[^""]*)""(?<rest>[^\]]*)\]", RegexOptions.Singleline)]
    private static partial Regex TriggerAttribute();

    [GeneratedRegex(@"Connection\s*=\s*""(?<conn>[^""]*)""")]
    private static partial Regex ConnectionArgument();

    [GeneratedRegex(@"\bclass\s+(?<name>\w+)")]
    private static partial Regex ClassName();

    /// <summary>Scans <c>**/Functions/*.cs</c> under <paramref name="projectDir"/>; paths in the result are relative to <paramref name="repoRoot"/>.</summary>
    public static IReadOnlyList<FoundTrigger> Scan(string projectDir, string repoRoot)
    {
        if (!Directory.Exists(projectDir)) return [];
        var files = Directory.EnumerateFiles(projectDir, "*.cs", SearchOption.AllDirectories)
            .Where(f => Path.GetFileName(Path.GetDirectoryName(f)) == "Functions"
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .OrderBy(f => f, StringComparer.Ordinal);
        var result = new List<FoundTrigger>();
        foreach (var file in files)
            result.AddRange(ScanSource(File.ReadAllText(file), Path.GetRelativePath(repoRoot, file).Replace('\\', '/')));
        return result;
    }

    /// <summary>Pairs each ServiceBusTrigger with the nearest preceding [Function("...")] (else the class name).</summary>
    public static IEnumerable<FoundTrigger> ScanSource(string source, string sourceFile)
    {
        var functions = FunctionAttribute().Matches(source).Select(m => (m.Index, Name: m.Groups["fn"].Value)).ToList();
        var className = ClassName().Match(source) is { Success: true } c ? c.Groups["name"].Value : Path.GetFileNameWithoutExtension(sourceFile);
        foreach (Match m in TriggerAttribute().Matches(source))
        {
            var fn = functions.LastOrDefault(f => f.Index < m.Index).Name ?? className;
            var name = m.Groups["name"].Value;
            var conn = ConnectionArgument().Match(m.Groups["rest"].Value) is { Success: true } cm ? cm.Groups["conn"].Value : null;
            var isSetting = name.Length > 2 && name.StartsWith('%') && name.EndsWith('%');
            yield return new FoundTrigger(fn, isSetting ? name[1..^1] : null, isSetting ? null : name, conn, sourceFile);
        }
    }
}

public sealed record LocalQueue(string Name, int? MaxDeliveryCount, string? ForwardTo);
public sealed record LocalSubscription(string Name, int? MaxDeliveryCount, string? ForwardTo);
public sealed record LocalTopic(string Name, IReadOnlyList<LocalSubscription> Subscriptions);
public sealed record LocalConfig(IReadOnlyList<LocalQueue> Queues, IReadOnlyList<LocalTopic> Topics)
{
    public bool HasQueue(string name) => Queues.Any(q => q.Name == name);
    public bool HasTopic(string name) => Topics.Any(t => t.Name == name);
}

/// <summary>An app the local-dev kit runs: run-{name}.linux.ps1 → project folder, port, URL.</summary>
public sealed record LocalApp(string Name, string RunScript, string? ProjectFolder, bool IsFunctionHost, int? Port, string? Url);

public static partial class LocalDevReader
{
    /// <summary>Service Bus emulator Config.json (UserConfig.Namespaces[*].Queues/Topics).</summary>
    public static LocalConfig ReadConfig(string configJson)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(configJson), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        var queues = new List<LocalQueue>();
        var topics = new List<LocalTopic>();
        foreach (var ns in Array(doc.RootElement, "UserConfig", "Namespaces"))
        {
            foreach (var q in Array(ns, "Queues"))
                queues.Add(new LocalQueue(q.GetProperty("Name").GetString()!, IntProp(q, "MaxDeliveryCount"), Forward(q)));
            foreach (var t in Array(ns, "Topics"))
                topics.Add(new LocalTopic(t.GetProperty("Name").GetString()!,
                    Array(t, "Subscriptions").Select(s => new LocalSubscription(s.GetProperty("Name").GetString()!, IntProp(s, "MaxDeliveryCount"), Forward(s))).ToList()));
        }
        return new LocalConfig(queues, topics);
    }

    private static IEnumerable<JsonElement> Array(JsonElement e, params string[] path)
    {
        foreach (var p in path)
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(p, out e)) return [];
        return e.ValueKind == JsonValueKind.Array ? e.EnumerateArray().ToList() : [];
    }

    private static int? IntProp(JsonElement e, string name) =>
        e.TryGetProperty("Properties", out var p) && p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;

    private static string? Forward(JsonElement e) =>
        e.TryGetProperty("Properties", out var p) && p.TryGetProperty("ForwardTo", out var v) && v.GetString() is { Length: > 0 } f ? f : null;

    [GeneratedRegex(@"^\s*'(?<key>[^']+)'\s*=\s*'(?<value>[^']*)'", RegexOptions.Multiline)]
    private static partial Regex PsSetting();

    /// <summary>The literal 'Key' = 'value' pairs of env(.linux).ps1 — these are the emulator entity names the apps get.</summary>
    public static IReadOnlyDictionary<string, string> ReadEnvSettings(string envPs1)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!File.Exists(envPs1)) return map;
        foreach (Match m in PsSetting().Matches(File.ReadAllText(envPs1)))
            map[m.Groups["key"].Value] = m.Groups["value"].Value;
        return map;
    }

    [GeneratedRegex(@"^run-(?<name>[a-z0-9-]+?)(?<linux>\.linux)?\.ps1$")]
    private static partial Regex RunScriptName();

    [GeneratedRegex(@"\$repo[/\\]src[/\\](?<project>[^""'\s/\\]+)")]
    private static partial Regex ProjectPath();

    [GeneratedRegex(@"--port\s+(?<port>\d+)")]
    private static partial Regex PortArgument();

    [GeneratedRegex(@"--launch-profile\s+""?(?<profile>[^""\s]+)")]
    private static partial Regex LaunchProfile();

    [GeneratedRegex(@"https?://localhost:(?<port>\d+)")]
    private static partial Regex LocalhostUrl();

    /// <summary>run-*.linux.ps1 (preferred) or run-*.ps1 per app, sorted by name. Ports from `--port N`, else the launch profile, else the header comment.</summary>
    public static IReadOnlyList<LocalApp> ReadRunScripts(string localDevDir, string serviceRepo)
    {
        var byName = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in Directory.GetFiles(localDevDir, "run-*.ps1"))
        {
            var m = RunScriptName().Match(Path.GetFileName(file));
            if (!m.Success) continue;
            var name = m.Groups["name"].Value;
            if (m.Groups["linux"].Success || !byName.ContainsKey(name)) byName[name] = file;
        }
        var apps = new List<LocalApp>();
        foreach (var (name, file) in byName)
        {
            var text = File.ReadAllText(file);
            var project = ProjectPath().Match(text) is { Success: true } pm ? pm.Groups["project"].Value : null;
            var isFunc = text.Contains("func start", StringComparison.Ordinal);
            int? port = PortArgument().Match(text) is { Success: true } po ? int.Parse(po.Groups["port"].Value) : null;
            string? url = null;
            if (project is not null && LaunchProfile().Match(text) is { Success: true } lp
                && ReadLaunchProfile(Path.Combine(serviceRepo, "src", project, "Properties", "launchSettings.json"), lp.Groups["profile"].Value) is { } profile)
            {
                port ??= profile.Port;
                url = profile.Url;
            }
            if (port is null && LocalhostUrl().Match(text) is { Success: true } cm) port = int.Parse(cm.Groups["port"].Value);
            apps.Add(new LocalApp(name, file, project, isFunc, port, url));
        }
        return apps;
    }

    /// <summary>First https applicationUrl of the profile (+ launchUrl) → (port, url).</summary>
    public static (int Port, string Url)? ReadLaunchProfile(string launchSettings, string profileName)
    {
        if (!File.Exists(launchSettings)) return null;
        using var doc = JsonDocument.Parse(File.ReadAllText(launchSettings), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        if (!doc.RootElement.TryGetProperty("profiles", out var profiles) || !profiles.TryGetProperty(profileName, out var profile)
            || !profile.TryGetProperty("applicationUrl", out var urls) || urls.GetString() is not { } urlList) return null;
        var all = urlList.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var baseUrl = all.FirstOrDefault(u => u.StartsWith("https://", StringComparison.Ordinal)) ?? all.FirstOrDefault();
        if (baseUrl is null || !Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)) return null;
        var launch = profile.TryGetProperty("launchUrl", out var lu) ? lu.GetString() : null;
        return (uri.Port, string.IsNullOrEmpty(launch) ? baseUrl : $"{baseUrl.TrimEnd('/')}/{launch.TrimStart('/')}");
    }
}
