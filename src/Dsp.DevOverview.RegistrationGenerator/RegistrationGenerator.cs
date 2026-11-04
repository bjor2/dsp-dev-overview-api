using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Dsp.DevOverview.RegistrationGenerator;

/// <summary>Where the generator reads from (ADR-002 sources). Null paths follow the DSP workspace conventions for <see cref="Project"/>.</summary>
public sealed class GeneratorInputs
{
    public const string DefaultTerraformEnvDir = "/workspace/claranet/terraform/870-brodrene-dahl-dsp/dev/eu-west";

    public required string Project { get; init; }
    public string Env { get; init; } = "dev";
    /// <summary>dev-overview root: relative catalog paths and the schema resolve against it.</summary>
    public required string OverviewRoot { get; init; }
    public string TerraformEnvDir { get; init; } = DefaultTerraformEnvDir;
    public string? ServiceRepo { get; init; }
    public string? LocalDevDir { get; init; }
    /// <summary>Written into samples.catalog as given (relative = relative to the overview root).</summary>
    public string? SamplesCatalog { get; init; }
    /// <summary>
    /// Also emit cloud subscriptions that connect the project to other services (other topics forwarding into project
    /// queues, other services subscribing to the project topic). Off by default: they add cloud-only nodes for foreign
    /// topics/queues, and the default keeps the flow graph to the project's own entities.
    /// </summary>
    public bool IncludeExternalSubscriptions { get; init; }

    public string Id => $"{Project}.{Env}";
    public string ServiceRepoPath => ServiceRepo ?? $"/workspace/dsp/dsp-{Project}-localrun";
    public string LocalDevPath => LocalDevDir ?? $"/workspace/dsp/local-dev/{Project}";
    public string SamplesCatalogRef => SamplesCatalog ?? $"catalog/{Project}.samples.json";
    public string AppStack => Path.Combine(TerraformEnvDir, $"unprovisioned_app-{Project}");
    public string IngressStack => Path.Combine(TerraformEnvDir, $"unprovisioned_app-{Project}-ingress");
    public string SharedMessage => Path.Combine(TerraformEnvDir, "unprovisioned_shared-message");
    public string SchemaPath => Path.Combine(OverviewRoot, "architecture", "registration.schema.json");
}

public sealed class GeneratorInputException(string message) : Exception(message);

public sealed record GenerationResult(JsonObject Record, IReadOnlyList<string> Diff, IReadOnlyList<string> Gaps, IReadOnlyList<string> SchemaErrors,
    IReadOnlyList<RegistrationIssue> Issues)
{
    /// <summary>Schema-valid and no V-1..V-5 / I-1 issue (ADDENDA-008 §9.3).</summary>
    public bool IsValid => SchemaErrors.Count == 0 && Issues.Count == 0;
    public string ToJson() => RegistrationGenerator.Serialize(Record);
}

/// <summary>
/// ADR-002 generator: Terraform app_settings + shared-message entities + [ServiceBusTrigger] scan + local-dev Config.json /
/// env.linux.ps1 / run-*.ps1 → registration record, merged into the current record so hand-maintained data survives:
/// <c>overrides</c> verbatim, edges with confidence "manual", top-level sections the generator does not own
/// (observability, naming, ...) and extra properties on apps/entities/edges (namingNote, labels, ...).
/// </summary>
public static class RegistrationGenerator
{
    private static readonly JsonSerializerOptions Write = new()
    {
        WriteIndented = true, IndentSize = 1, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Serialize(JsonNode node) => node.ToJsonString(Write) + "\n";

    public static GenerationResult Generate(GeneratorInputs inputs, JsonObject? current, DateTimeOffset now)
    {
        var gaps = new List<string>();
        var generated = new Builder(inputs, current, gaps).Build(now);
        var merged = Merge(current, generated);
        ApplyOverrides(merged);
        var issues = RegistrationValidator.Apply(merged); // I-1: code self-sends → selfRequeue badges
        var errors = File.Exists(inputs.SchemaPath)
            ? RegistrationSchema.Load(inputs.SchemaPath).Validate(merged)
            : [$"schema not found: {inputs.SchemaPath}"];
        return new GenerationResult(merged, RegistrationDiff.Describe(current, merged), gaps.Distinct().ToList(), errors, issues);
    }

    public static JsonObject? ReadCurrent(string path) =>
        File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject : null;

    // ---------------------------------------------------------------- merge

    private static readonly string[] AppOwned = ["id", "name", "kind", "azureResource", "triggers"];
    private static readonly string[] EntityOwned = ["id", "kind", "localName", "cloudName", "topic", "forwardTo", "sqlFilter", "owner", "scope", "cloudMaxDeliveryCount"];
    private static readonly string[] EdgeOwned = ["from", "to", "kind", "scope", "confidence", "sources", "messages"];
    private static readonly string[] ContainerOwned = ["id", "label", "kind", "resourceRefs", "scope"];
    private static readonly string[] HandlerOwned = ["id", "label", "appId", "containerId", "scope", "triggers", "selfRequeue"];
    private static readonly string[] KeyOrder = ["id", "project", "env", "schemaVersion", "generatedAt", "sources", "apps", "containers", "handlers", "entities", "edges"];
    private static readonly string[] SamplesOwned = ["catalog", "source"];

    public static string EdgeKey(JsonNode? e) => $"{e?["from"]}|{e?["to"]}|{e?["kind"]}";

    internal static JsonObject Merge(JsonObject? current, JsonObject generated)
    {
        if (current is null)
        {
            var fresh = (JsonObject)generated.DeepClone();
            fresh["overrides"] ??= new JsonObject();
            return Ordered(fresh);
        }
        var result = (JsonObject)current.DeepClone();
        foreach (var key in new[] { "id", "project", "env", "schemaVersion", "generatedAt" }) result[key] = generated[key]!.DeepClone();

        result["sources"] = UnionSources(generated["sources"] as JsonArray, current["sources"] as JsonArray);
        result["apps"] = MergeById(current["apps"] as JsonArray, (JsonArray)generated["apps"]!, AppOwned, MergeApp);
        result["containers"] = MergeById(current["containers"] as JsonArray, (JsonArray)generated["containers"]!, ContainerOwned, (c, g) => MergeObject(c, g, ContainerOwned));
        result["handlers"] = MergeById(current["handlers"] as JsonArray, (JsonArray)generated["handlers"]!, HandlerOwned, (c, g) => MergeObject(c, g, HandlerOwned));
        result["entities"] = MergeById(current["entities"] as JsonArray, (JsonArray)generated["entities"]!, EntityOwned, MergeEntity);
        result["edges"] = MergeEdges(current["edges"] as JsonArray, (JsonArray)generated["edges"]!, (JsonArray)generated["handlers"]!);
        result["samples"] = MergeObject(current["samples"] as JsonObject, (JsonObject)generated["samples"]!, SamplesOwned);
        if (current["observability"] is not JsonObject) result["observability"] = generated["observability"]!.DeepClone();
        if (current["overrides"] is not JsonObject) result["overrides"] = new JsonObject();
        return Ordered(result);
    }

    /// <summary>Stable key order (schemaVersion/containers/handlers next to their v1 neighbours); other keys keep their order.</summary>
    private static JsonObject Ordered(JsonObject o)
    {
        var result = new JsonObject();
        foreach (var key in KeyOrder.Where(o.ContainsKey)) result[key] = o[key]?.DeepClone();
        foreach (var (key, value) in o) if (!result.ContainsKey(key)) result[key] = value?.DeepClone();
        return result;
    }

    private static JsonArray UnionSources(JsonArray? generated, JsonArray? current)
    {
        var list = new JsonArray();
        var seen = new HashSet<string>();
        foreach (var s in (generated ?? []).Concat(current ?? []))
            if (seen.Add($"{s?["kind"]}|{s?["path"]}")) list.Add(s!.DeepClone());
        return list;
    }

    /// <summary>Owned properties come from the generator (and are removed when it no longer emits them); the rest of the current item is kept.</summary>
    private static JsonObject MergeObject(JsonObject? current, JsonObject generated, IReadOnlyCollection<string> owned)
    {
        var result = (JsonObject?)current?.DeepClone() ?? new JsonObject();
        foreach (var key in owned)
            if (!generated.ContainsKey(key)) result.Remove(key); // an explicit null (cloudName) is a value, not an absence
        foreach (var (key, value) in generated)
            result[key] = value?.DeepClone();
        return result;
    }

    private static JsonObject MergeApp(JsonObject? current, JsonObject generated)
    {
        var softRole = current?["cloudRoleName"]?.DeepClone(); // may be confirmed from App Insights (GET /logs/roles)
        if (generated["triggers"] is JsonArray triggers && current?["triggers"] is JsonArray known)
        {
            // Keep the current order for known triggers (file scan order is alphabetical), new ones last.
            var order = known.Select(t => t?["function"]?.ToString()).ToList();
            var sorted = triggers.OrderBy(t => order.IndexOf(t?["function"]?.ToString()) is var i && i < 0 ? int.MaxValue : i).Select(t => t!.DeepClone()).ToArray();
            generated = (JsonObject)generated.DeepClone();
            generated["triggers"] = new JsonArray(sorted);
        }
        var result = MergeObject(current, generated, AppOwned);
        if (softRole is not null) result["cloudRoleName"] = softRole;
        result["local"] = MergeObject(current?["local"] as JsonObject, (JsonObject?)generated["local"] ?? [], ["port", "runScript"]);
        return result;
    }

    private static JsonObject MergeEntity(JsonObject? current, JsonObject generated)
    {
        // Config.json is the only source of the local maxDeliveryCount; for cloud-only entities a hand value is kept.
        var owned = generated["scope"]?.GetValue<string>() == "cloud" ? EntityOwned : [.. EntityOwned, "maxDeliveryCount"];
        return MergeObject(current, generated, owned);
    }

    private static JsonArray MergeById(JsonArray? current, JsonArray generated, IReadOnlyCollection<string> owned,
        Func<JsonObject?, JsonObject, JsonObject> merge)
    {
        var byId = (current ?? []).OfType<JsonObject>().GroupBy(o => o["id"]?.ToString() ?? "").ToDictionary(g => g.Key, g => g.First());
        var list = new JsonArray();
        foreach (var g in generated.OfType<JsonObject>())
            list.Add(merge(byId.GetValueOrDefault(g["id"]!.ToString()), g));
        return list;
    }

    private static JsonArray MergeEdges(JsonArray? current, JsonArray generated, JsonArray handlers)
    {
        var byKey = (current ?? []).OfType<JsonObject>().GroupBy(EdgeKey).ToDictionary(g => g.Key, g => g.First());
        // v1 → v2: hand labels on app-level edges ("commands", "PCO events") carry over to the handler edges of that app.
        var appOf = handlers.OfType<JsonObject>().ToDictionary(h => h["id"]!.ToString(), h => h["appId"]?.ToString() ?? "");
        var list = new JsonArray();
        var keys = new HashSet<string>();
        foreach (var g in generated.OfType<JsonObject>())
        {
            var key = EdgeKey(g);
            if (!keys.Add(key)) continue;
            var cur = byKey.GetValueOrDefault(key);
            var merged = MergeObject(cur, g, EdgeOwned);
            if (g["label"] is null && cur?["label"] is { } label) merged["label"] = label.DeepClone(); // hand labels ("commands", "PCO events")
            else if (g["label"] is null && cur is null && appOf.TryGetValue(g["from"]!.ToString(), out var app)
                     && byKey.GetValueOrDefault($"{app}|{g["to"]}|{g["kind"]}")?["label"] is { } appLabel)
                merged["label"] = appLabel.DeepClone();
            list.Add(merged);
        }
        // Hand-maintained edges (seed-messages injects, ...) are never derived; keep them.
        foreach (var c in (current ?? []).OfType<JsonObject>())
            if (c["confidence"]?.ToString() == "manual" && keys.Add(EdgeKey(c))) list.Add(c.DeepClone());
        return list;
    }

    /// <summary>
    /// Optional structured overrides, applied after generation: <c>overrides.apps[]</c> / <c>overrides.entities[]</c>
    /// (patched or added by id) and <c>overrides.edges[]</c> (patched or added by from|to|kind). Other keys (e.g. "note")
    /// are documentation only. The overrides object itself is never rewritten.
    /// </summary>
    internal static void ApplyOverrides(JsonObject record)
    {
        if (record["overrides"] is not JsonObject overrides) return;
        Patch(record, "apps", overrides["apps"] as JsonArray, o => o["id"]?.ToString() ?? "");
        Patch(record, "entities", overrides["entities"] as JsonArray, o => o["id"]?.ToString() ?? "");
        Patch(record, "edges", overrides["edges"] as JsonArray, EdgeKey);
    }

    private static void Patch(JsonObject record, string section, JsonArray? patches, Func<JsonObject, string> key)
    {
        if (patches is null) return;
        if (record[section] is not JsonArray items) record[section] = items = [];
        foreach (var patch in patches.OfType<JsonObject>())
        {
            var target = items.OfType<JsonObject>().FirstOrDefault(i => key(i) == key(patch));
            if (target is null) { items.Add(patch.DeepClone()); continue; }
            foreach (var (k, v) in patch) target[k] = v?.DeepClone();
        }
    }

    // ---------------------------------------------------------------- build

    private sealed class EntityInfo
    {
        public required string Id;
        public required string Kind;
        public string LocalName = "n/a";
        public string? CloudName;
        public string? Short;
        public string? Topic;
        public string? ForwardTo;
        public string? SqlFilter;
        public bool Local;
        public bool Cloud;
        public bool Project;
        public int? MaxDelivery;
        public int? CloudMaxDelivery;
        public string Scope => Local && Cloud ? "both" : Local ? "local" : "cloud";
    }

    private sealed record AppInfo(string Id, string Name, string Kind, string AzureResource, LocalApp Local,
        IReadOnlyList<SbSetting> Settings, IReadOnlyList<FoundTrigger> Triggers, string TfFile)
    {
        public string ContainerId { get; set; } = "";
    }

    /// <summary>One Flow handler node: a [Function] + its trigger, or the HTTP surface of a web app (<see cref="Trigger"/> null).</summary>
    private sealed record HandlerInfo(string Id, AppInfo App, FoundTrigger? Trigger, string? EntityId, JsonObject Json);

    private sealed class Builder(GeneratorInputs inputs, JsonObject? current, List<string> gaps)
    {
        private readonly List<EntityInfo> _entities = [];
        private readonly JsonArray _edges = [];
        private readonly JsonArray _sources = [];
        private readonly List<HandlerInfo> _handlers = [];
        private CodeIndex? _code;
        private Dictionary<string, string> _env = [];
        private LocalConfig _local = new([], []);
        private TfServiceBus _tf = new([], [], []);
        private TfNaming _naming = new();

        public JsonObject Build(DateTimeOffset now)
        {
            _naming = NamingFromRecord(current);
            if (!Directory.Exists(inputs.AppStack)) throw new GeneratorInputException($"Terraform app stack not found: {inputs.AppStack}");
            if (!Directory.Exists(inputs.SharedMessage)) throw new GeneratorInputException($"Terraform shared-message stack not found: {inputs.SharedMessage}");
            if (!Directory.Exists(inputs.LocalDevPath)) throw new GeneratorInputException($"local-dev folder not found: {inputs.LocalDevPath}");

            Source("terraform", inputs.AppStack);
            if (Directory.Exists(inputs.IngressStack)) Source("terraform", inputs.IngressStack);
            Source("terraform", inputs.SharedMessage);
            Source("trigger-scan", Path.Combine(inputs.ServiceRepoPath, "src"));

            _tf = TerraformReader.ReadServiceBus(inputs.SharedMessage, inputs.IncludeExternalSubscriptions
                ? null
                : ["locals-sb-queue-dsp.tf", "locals-sb-topic-dsp.tf", "locals-sb-subscription-dsp.tf"]);
            var configPath = Path.Combine(inputs.LocalDevPath, "Config.json");
            if (File.Exists(configPath)) { _local = LocalDevReader.ReadConfig(configPath); Source("local-config", configPath); }
            else gaps.Add($"local-dev: {configPath} missing — no local entities");
            var envPath = Path.Combine(inputs.LocalDevPath, "env.linux.ps1");
            if (!File.Exists(envPath)) envPath = Path.Combine(inputs.LocalDevPath, "env.ps1");
            _env = new Dictionary<string, string>(LocalDevReader.ReadEnvSettings(envPath));
            if (File.Exists(envPath)) Source("local-config", envPath);

            var apps = ReadApps();
            foreach (var app in apps)
                app.ContainerId = HandlerNaming.ContainerId(inputs.Project, app.Id, app.Kind, apps.Select(a => (a.Id, a.Kind)));
            AddConfigEntities(apps);
            var appJson = new JsonArray();
            foreach (var app in apps) appJson.Add(AppJson(app));
            _code = Directory.Exists(Path.Combine(inputs.ServiceRepoPath, "src")) ? CodeIndex.Build(inputs.ServiceRepoPath) : null;
            AttributeSends(apps);
            AddLocalSubscriptions();
            AddCloudSubscriptions(apps);

            var kit = Path.Combine(inputs.LocalDevPath, "seed-messages", "Program.cs");
            if (File.Exists(kit)) Source("sample-kit", kit);
            var catalogFull = Path.IsPathRooted(inputs.SamplesCatalogRef) ? inputs.SamplesCatalogRef : Path.Combine(inputs.OverviewRoot, inputs.SamplesCatalogRef);
            if (!File.Exists(catalogFull)) gaps.Add($"samples: catalog {catalogFull} does not exist (run tools/CatalogGenerator)");

            var samples = new JsonObject { ["catalog"] = inputs.SamplesCatalogRef };
            if (File.Exists(kit)) samples["source"] = kit;

            return new JsonObject
            {
                ["id"] = inputs.Id,
                ["project"] = inputs.Project,
                ["env"] = inputs.Env,
                ["schemaVersion"] = 2,
                ["generatedAt"] = now.ToString("yyyy-MM-ddTHH:mm:sszzz"),
                ["sources"] = _sources,
                ["apps"] = appJson,
                ["containers"] = ContainersJson(apps),
                ["handlers"] = new JsonArray(_handlers.Select(h => (JsonNode)h.Json.DeepClone()).ToArray()),
                ["entities"] = new JsonArray(_entities.Select(EntityJson).ToArray()),
                ["edges"] = _edges,
                ["observability"] = new JsonObject
                {
                    ["appInsights"] = new JsonObject
                    {
                        ["resource"] = $"appin-{_naming.Suffix}-shared",
                        ["cloudRoleNames"] = new JsonArray(apps.Select(a => (JsonNode)a.AzureResource).ToArray()),
                    },
                },
                ["samples"] = samples,
            };
        }

        private static TfNaming NamingFromRecord(JsonObject? record)
        {
            var v = record?["naming"]?["values"];
            var d = new TfNaming();
            return v is null ? d : new TfNaming(
                v["project_code"]?.ToString() ?? d.ProjectCode, v["env"]?.ToString() ?? d.Env,
                v["location_short"]?.ToString() ?? d.LocationShort, v["project_name"]?.ToString() ?? d.ProjectName);
        }

        private void Source(string kind, string path) => _sources.Add(new JsonObject { ["kind"] = kind, ["path"] = path });

        private List<AppInfo> ReadApps()
        {
            var locals = LocalDevReader.ReadRunScripts(inputs.LocalDevPath, inputs.ServiceRepoPath);
            if (locals.Count == 0) throw new GeneratorInputException($"no run-*.ps1 scripts in {inputs.LocalDevPath}");
            var apps = new List<AppInfo>();
            var usedTfFiles = new HashSet<string>();
            foreach (var l in locals)
            {
                Source("run-script", l.RunScript);
                var ingress = l.IsFunctionHost && ((l.ProjectFolder?.Contains(".Ingress.", StringComparison.OrdinalIgnoreCase) ?? false) || l.Name.Contains("ingress"));
                var kind = !l.IsFunctionHost ? "web" : ingress ? "func-ingress" : "func";
                var stack = kind == "func-ingress" && Directory.Exists(inputs.IngressStack) ? inputs.IngressStack : inputs.AppStack;
                var (file, local) = kind == "web" ? ("locals-web.tf", "web_app") : ("locals-function.tf", "function_app");
                var tfFile = Path.Combine(stack, file);
                var appName = Path.GetFileName(stack)["unprovisioned_app-".Length..];
                IReadOnlyList<SbSetting> settings = [];
                if (File.Exists(tfFile))
                {
                    settings = TerraformReader.ServiceBusSettings(TerraformReader.ReadAppSettings(tfFile, local), _naming);
                    usedTfFiles.Add(tfFile);
                }
                else gaps.Add($"app:{l.Name}: {tfFile} not found — no Terraform app settings");
                foreach (var s in settings.Where(s => s.Kind == SettingKind.Other))
                    gaps.Add($"app:{l.Name}: {s.Key} = {s.RawValue} is not a module.naming queue/topic reference — skipped");

                var triggers = l.ProjectFolder is null ? [] : TriggerScanner.Scan(Path.Combine(inputs.ServiceRepoPath, "src", l.ProjectFolder), inputs.ServiceRepoPath);
                if (l.ProjectFolder is null) gaps.Add($"app:{l.Name}: project folder not found in {l.RunScript} — no trigger scan");
                if (l.Port is null) gaps.Add($"app:{l.Name}: no port in {l.RunScript}");
                if (kind == "func-ingress")
                    gaps.Add($"app:{l.Name}: Azure name {_naming.AppResource("func", appName)} is built by the external dahl-app module (app_short_name differs) — unconfirmed");
                apps.Add(new AppInfo($"app:{l.Name}", char.ToUpperInvariant(l.Name[0]) + l.Name[1..], kind,
                    _naming.AppResource(kind == "web" ? "web" : "func", appName), l, settings, triggers, tfFile));
            }
            foreach (var stack in new[] { inputs.AppStack, inputs.IngressStack }.Where(Directory.Exists))
                foreach (var f in new[] { "locals-web.tf", "locals-function.tf" }.Select(f => Path.Combine(stack, f)).Where(File.Exists))
                    if (!usedTfFiles.Contains(f)) gaps.Add($"terraform: {f} has no local run script — app not registered");
            return apps;
        }

        // Entity ids follow the local (emulator) name when there is one, else the Terraform short name.
        private EntityInfo Entity(string kind, string? localName, string? shortName)
        {
            var prefix = kind == "queue" ? "q:" : "t:";
            var id = prefix + (localName ?? shortName);
            var e = _entities.FirstOrDefault(x => x.Id == id)
                ?? (shortName is null ? null : _entities.FirstOrDefault(x => x.Kind == kind && x.Short == shortName));
            if (e is not null) return e;
            e = new EntityInfo { Id = id, Kind = kind, Short = shortName };
            if (localName is not null) e.LocalName = localName;
            e.Project = (localName ?? shortName ?? "").StartsWith(inputs.Project, StringComparison.Ordinal);
            _entities.Add(e);
            return e;
        }

        /// <summary>Resolves an app setting to its entity: cloud name from Terraform, local name from env.linux.ps1.</summary>
        private EntityInfo? Resolve(SbSetting s, string appId)
        {
            if (s.Kind is not (SettingKind.Queue or SettingKind.Topic)) return null;
            var kind = s.Kind == SettingKind.Queue ? "queue" : "topic";
            var local = _env.GetValueOrDefault(s.Key);
            if (local is not null && !(kind == "queue" ? _local.HasQueue(local) : _local.HasTopic(local)))
            {
                gaps.Add($"{appId}: {s.Key} → '{local}' (env) is not in Config.json");
                local = null;
            }
            if (local is null && s.ShortName is not null && (kind == "queue" ? _local.HasQueue(s.ShortName) : _local.HasTopic(s.ShortName))) local = s.ShortName;
            var e = Entity(kind, local, s.ShortName);
            e.Local |= local is not null;
            ApplyCloud(e, s.ShortName, appId, s.Key);
            return e;
        }

        private void ApplyCloud(EntityInfo e, string? shortName, string appId, string setting)
        {
            if (shortName is null || e.Cloud) return;
            e.Short ??= shortName;
            if (e.Kind == "queue" && _tf.Queue(shortName) is { } q)
            {
                e.Cloud = true;
                e.CloudName = _naming.QueueName(shortName);
                e.CloudMaxDelivery = q.MaxDeliveryCount;
            }
            else if (e.Kind == "topic" && _tf.HasTopic(shortName))
            {
                e.Cloud = true;
                e.CloudName = _naming.TopicName(shortName);
            }
            else gaps.Add($"{appId}: {setting} → {e.Kind} '{shortName}' is not declared in shared-message locals-sb-*.tf");
        }

        /// <summary>Config.json order first (queues, then topics), then trigger/publish targets that only exist in the cloud.</summary>
        private void AddConfigEntities(List<AppInfo> apps)
        {
            var byLocal = apps.SelectMany(a => a.Settings.Select(s => (a, s)))
                .Where(x => _env.TryGetValue(x.s.Key, out _))
                .GroupBy(x => _env[x.s.Key]).ToDictionary(g => g.Key, g => g.First());
            foreach (var q in _local.Queues)
            {
                var e = Entity("queue", q.Name, null);
                e.Local = true;
                e.MaxDelivery = q.MaxDeliveryCount;
                if (byLocal.TryGetValue(q.Name, out var hit) && hit.s.Kind == SettingKind.Queue) ApplyCloud(e, hit.s.ShortName, hit.a.Id, hit.s.Key);
            }
            foreach (var t in _local.Topics)
            {
                var e = Entity("topic", t.Name, null);
                e.Local = true;
                if (byLocal.TryGetValue(t.Name, out var hit) && hit.s.Kind == SettingKind.Topic) ApplyCloud(e, hit.s.ShortName, hit.a.Id, hit.s.Key);
            }
            foreach (var app in apps) AddHandlers(app);
        }

        private JsonObject? Overrides => current?["overrides"] as JsonObject;

        /// <summary>One handler per [Function] trigger (+ the HTTP surface of a web app); trigger edges end at the handler.</summary>
        private void AddHandlers(AppInfo app)
        {
            foreach (var t in app.Triggers)
            {
                EntityInfo? e;
                if (t.Setting is not null)
                {
                    var setting = app.Settings.FirstOrDefault(s => s.Key == t.Setting);
                    if (setting is null)
                    {
                        gaps.Add($"{app.Id}: trigger {t.Function} uses %{t.Setting}% which is not in {app.TfFile} app_settings");
                        var local = _env.GetValueOrDefault(t.Setting);
                        e = local is not null && _local.HasQueue(local) ? Entity("queue", local, null) : null;
                    }
                    else e = Resolve(setting, app.Id);
                }
                else e = _local.HasQueue(t.LiteralName!) ? Entity("queue", t.LiteralName, null) : Entity("queue", null, t.LiteralName);
                if (e is null) { gaps.Add($"{app.Id}: trigger {t.Function} (%{t.Setting}%) could not be resolved to an entity"); continue; }
                var id = HandlerNaming.HandlerId(inputs.Project, t.Function, Overrides);
                _edges.Add(Edge(e.Id, id, "trigger", t.Function, e.Scope, "code"));
                var trigger = new JsonObject { ["kind"] = "servicebus", ["function"] = t.Function, ["entityId"] = e.Id };
                if (t.Setting is not null) trigger["setting"] = t.Setting;
                if (t.Connection is not null) trigger["connection"] = t.Connection;
                trigger["sourceFile"] = t.SourceFile;
                _handlers.Add(new HandlerInfo(id, app, t, e.Id, HandlerJson(id, t.Function, app, trigger)));
            }
            if (app.Kind == "web")
            {
                var id = HandlerNaming.HttpHandlerId(inputs.Project, app.Id);
                var trigger = new JsonObject { ["kind"] = "http" };
                if (app.Local.ProjectFolder is { } folder) trigger["sourceFile"] = $"src/{folder}";
                _handlers.Add(new HandlerInfo(id, app, null, null, HandlerJson(id, null, app, trigger)));
            }
        }

        private JsonObject HandlerJson(string id, string? function, AppInfo app, JsonObject trigger) => new()
        {
            ["id"] = id, ["label"] = HandlerNaming.HandlerLabel(id, inputs.Project, function, Overrides),
            ["appId"] = app.Id, ["containerId"] = app.ContainerId, ["triggers"] = new JsonArray(trigger),
        };

        /// <summary>One resource container per app; overrides.logicalContainers[] adds logical ones and moves their members (§1.3, §11).</summary>
        private JsonArray ContainersJson(List<AppInfo> apps)
        {
            var list = new JsonArray();
            foreach (var a in apps)
                list.Add(new JsonObject
                {
                    ["id"] = a.ContainerId, ["label"] = HandlerNaming.ContainerLabel(a.Name, a.Local.Port),
                    ["kind"] = "resource", ["resourceRefs"] = new JsonArray(a.Id),
                });
            foreach (var l in (Overrides?["logicalContainers"] as JsonArray ?? []).OfType<JsonObject>())
            {
                var id = l["id"]?.ToString();
                if (id is null) continue;
                list.Add(new JsonObject
                {
                    ["id"] = id, ["label"] = l["label"]?.ToString() ?? id["container:".Length..], ["kind"] = "logical",
                    ["resourceRefs"] = l["resourceRefs"]?.DeepClone() ?? new JsonArray(),
                });
                foreach (var member in (l["members"] as JsonArray ?? []).Select(m => m?.ToString()))
                    if (_handlers.FirstOrDefault(h => h.Id == member) is { } h) h.Json["containerId"] = id;
                    else gaps.Add($"overrides.logicalContainers {id}: member {member} is not a handler");
            }
            return list;
        }

        /// <summary>
        /// ADDENDA-008 §9.2: outbound edges per handler. Send sites reachable in code from the handler's [Function] method
        /// (or the web app's controllers) become <c>confidence: code</c> edges with file:line <c>sources</c>. A queue/topic
        /// app setting no handler sends to: no sender class for it in code → app-level fallback (every handler of the app,
        /// <c>inferred-from-settings</c>); sends exist in code but none reachable from this app → same fallback (ambiguous);
        /// a sender class with no send call anywhere → no edge (OQ-2: api → customerproject-ingress). Trigger-bound
        /// settings never fall back. The retry client handed to the Dahl.Core MessageHandler base is not drawn (OQ-4).
        /// </summary>
        private void AttributeSends(List<AppInfo> apps)
        {
            foreach (var app in apps)
            {
                var handlers = _handlers.Where(h => h.App == app).ToList();
                if (handlers.Count == 0) continue;
                var code = new List<(HandlerInfo H, EntityInfo E, List<SendSite> Sites)>();
                foreach (var h in handlers)
                {
                    var sites = Sites(h);
                    foreach (var g in sites.GroupBy(x => x.ConfigKey))
                    {
                        var e = EntityForKey(g.Key, app, h.Id);
                        if (e is null) continue;
                        var hit = code.FirstOrDefault(c => c.H == h && c.E == e);
                        if (hit.H is null) code.Add((h, e, g.ToList()));
                        else hit.Sites.AddRange(g);
                    }
                }
                foreach (var (h, e, sites) in code)
                {
                    var edge = Edge(h.Id, e.Id, "publish", null, e.Scope, "code");
                    edge["sources"] = new JsonArray(sites.Select(x => (JsonNode)x.Source).Distinct().ToArray());
                    var messages = sites.Select(x => x.Message).OfType<string>().Distinct().Order(StringComparer.Ordinal).ToArray();
                    if (messages.Length > 0) edge["messages"] = new JsonArray(messages.Select(m => (JsonNode)m).ToArray());
                    _edges.Add(edge);
                }

                var triggered = app.Triggers.Select(t => t.Setting).ToHashSet();
                foreach (var s in app.Settings.Where(s => s.Kind is SettingKind.Queue or SettingKind.Topic))
                {
                    if (Resolve(s, app.Id) is not { } e || code.Any(c => c.E == e)) continue;
                    if (triggered.Contains(s.Key))
                    {
                        gaps.Add($"{app.Id}: {s.Key} → {e.Id} is trigger-bound; no other handler of the app sends to it in code");
                        continue;
                    }
                    string why;
                    if (_code is null || !_code.SenderConfigKeys.Contains(s.Key))
                        why = $"no sender class for {s.Key} found in code";
                    else if (_code.AllSites.Any(x => x.ConfigKey == s.Key))
                        why = $"sends to {e.Id} exist in code but none is reachable from {app.Id}'s handlers (ambiguous)";
                    else
                    {
                        gaps.Add($"{app.Id}: {s.Key} → {e.Id} has a sender class but no send call in code — no edge (ADDENDA-008 OQ-2)");
                        continue;
                    }
                    gaps.Add($"{app.Id}: {why} — app-level fallback: {e.Id} from every handler of the app (inferred-from-settings)");
                    foreach (var h in handlers)
                        _edges.Add(Edge(h.Id, e.Id, "publish", null, e.Scope, "inferred-from-settings"));
                }
            }
        }

        /// <summary>Send sites of one handler: its [Function] method's call graph (Functions sharing a class stay apart), or all controllers.</summary>
        private IReadOnlyList<SendSite> Sites(HandlerInfo h)
        {
            if (_code is null) return [];
            if (h.Trigger is null)
            {
                var controllers = h.App.Local.ProjectFolder is { } folder ? _code.Controllers(folder) : [];
                if (controllers.Count == 0) gaps.Add($"{h.Id}: no controllers found in src/{h.App.Local.ProjectFolder} — HTTP sends not attributed");
                return _code.SitesFrom(controllers);
            }
            var root = _code.FunctionRoot(h.Trigger.SourceFile, h.Trigger.Function);
            if (root is null)
            {
                gaps.Add($"{h.Id}: [Function(\"{h.Trigger.Function}\")] method not found in {h.Trigger.SourceFile} — sends not attributed");
                return [];
            }
            foreach (var (type, key) in _code.BaseCtorSenders(root.Value.Class))
                gaps.Add($"{h.Id}: {type} ({key}) is passed to the {string.Join("/", root.Value.Class.Bases)} base — Dahl.Core HandleWithRetry re-sends to its own trigger queue; retry infrastructure, not drawn (OQ-4)");
            return _code.SitesFrom(root.Value.Class, root.Value.Method);
        }

        /// <summary>Config key of a sender → entity: this app's Terraform setting, else another app's, else env.linux.ps1 + Config.json.</summary>
        private EntityInfo? EntityForKey(string key, AppInfo app, string handlerId)
        {
            if (app.Settings.FirstOrDefault(s => s.Key == key) is { } own) return Resolve(own, app.Id);
            var local = _env.GetValueOrDefault(key);
            if (local is not null && _local.HasQueue(local)) { gaps.Add($"{handlerId}: sends via {key}, which is not in {app.TfFile} app_settings (resolved from env.linux.ps1)"); return Entity("queue", local, null); }
            if (local is not null && _local.HasTopic(local)) { gaps.Add($"{handlerId}: sends via {key}, which is not in {app.TfFile} app_settings (resolved from env.linux.ps1)"); return Entity("topic", local, null); }
            gaps.Add($"{handlerId}: sends via {key}, which resolves to no queue/topic (not in app_settings or env.linux.ps1) — no edge");
            return null;
        }

        private void AddLocalSubscriptions()
        {
            foreach (var t in _local.Topics)
            {
                var topic = _entities.First(e => e.Kind == "topic" && e.LocalName == t.Name);
                foreach (var s in t.Subscriptions)
                {
                    var sub = new EntityInfo
                    {
                        Id = $"s:{t.Name}/{s.Name}", Kind = "subscription", LocalName = s.Name, Topic = topic.Id, Local = true,
                        Project = topic.Project, MaxDelivery = s.MaxDeliveryCount,
                    };
                    _entities.Add(sub);
                    _edges.Add(Edge(topic.Id, sub.Id, "subscription", null, "local", "manual"));
                    if (s.ForwardTo is { } fwd)
                    {
                        var target = _entities.FirstOrDefault(e => e.Kind == "queue" && e.LocalName == fwd);
                        if (target is null) { gaps.Add($"local-config: subscription {t.Name}/{s.Name} forwards to unknown queue '{fwd}'"); continue; }
                        sub.ForwardTo = target.Id;
                        _edges.Add(Edge(sub.Id, target.Id, "forward", null, "local", "manual"));
                    }
                }
            }
        }

        private void AddCloudSubscriptions(List<AppInfo> apps)
        {
            var projectTopics = _entities.Where(e => e.Kind == "topic" && e.Cloud && e.Short is not null).ToDictionary(e => e.Short!);
            var projectQueues = _entities.Where(e => e.Kind == "queue" && e.Cloud && e.Short is not null).ToDictionary(e => e.Short!);
            var included = new List<TfSubscription>();
            foreach (var sub in _tf.Subscriptions)
            {
                var onProjectTopic = projectTopics.ContainsKey(sub.Topic);
                var intoProjectQueue = sub.ForwardToQueue is not null && projectQueues.ContainsKey(sub.ForwardToQueue);
                var include = onProjectTopic && intoProjectQueue
                    || (inputs.IncludeExternalSubscriptions && (onProjectTopic || intoProjectQueue));
                if (include && included.All(s => s.Key != sub.Key)) included.Add(sub);
            }
            if (!inputs.IncludeExternalSubscriptions)
            {
                var external = _tf.Subscriptions.Count(s => (projectTopics.ContainsKey(s.Topic) || (s.ForwardToQueue is not null && projectQueues.ContainsKey(s.ForwardToQueue))) && !included.Contains(s));
                if (external > 0) gaps.Add($"terraform: {external} cloud subscription(s) link the project to other services in locals-sb-subscription-dsp.tf (not emitted; use --include-external)");
            }

            // Subscription names referenced by app settings must exist in Terraform.
            foreach (var app in apps)
                foreach (var s in app.Settings.Where(s => s.Kind == SettingKind.Subscription))
                    if (_tf.Subscriptions.All(t => t.Name != s.ShortName))
                    {
                        var near = _tf.Subscriptions.FirstOrDefault(t => s.ShortName!.StartsWith(t.Key, StringComparison.Ordinal));
                        gaps.Add($"{app.Id}: {s.Key} = '{s.ShortName}' matches no Terraform subscription name" +
                                 (near is null ? "" : $" (Terraform has v{near.Version} '{near.Name}')"));
                    }

            foreach (var sub in included)
            {
                var topic = projectTopics.GetValueOrDefault(sub.Topic) ?? ExternalEntity("topic", sub.Topic);
                var sid = $"s:{topic.Id[2..]}/{sub.Key}";
                var e = new EntityInfo
                {
                    Id = sid, Kind = "subscription", Topic = topic.Id, CloudName = sub.Name, Cloud = true, Short = sub.Key,
                    SqlFilter = sub.SqlFilter, CloudMaxDelivery = TfSubscription.MaxDeliveryCount,
                };
                EntityInfo? target = null;
                if (sub.ForwardToQueue is { } fq) target = projectQueues.GetValueOrDefault(fq) ?? ExternalEntity("queue", fq);
                else if (sub.ForwardToTopic is { } ft) target = projectTopics.GetValueOrDefault(ft) ?? ExternalEntity("topic", ft);
                e.ForwardTo = target?.Id;
                e.Project = topic.Project || (target?.Project ?? false);
                _entities.Add(e);
                _edges.Add(Edge(topic.Id, sid, "subscription", target is null ? $"consumer: {sub.From} (v{sub.Version})" : null, "cloud", "terraform"));
                if (target is not null) _edges.Add(Edge(sid, target.Id, "forward", TerraformReader.LabelsFromFilter(sub.SqlFilter), "cloud", "terraform"));
            }
        }

        /// <summary>A topic/queue of another service: cloud-only, shared, no emulator name.</summary>
        private EntityInfo ExternalEntity(string kind, string shortName)
        {
            var e = Entity(kind, null, shortName);
            if (!e.Cloud)
            {
                e.Cloud = true;
                e.CloudName = kind == "queue" ? _naming.QueueName(shortName) : _naming.TopicName(shortName);
                if (kind == "queue") e.CloudMaxDelivery = _tf.Queue(shortName)?.MaxDeliveryCount ?? 3;
                var declared = kind == "queue" ? _tf.Queue(shortName) is not null : _tf.HasTopic(shortName);
                if (!declared) gaps.Add($"terraform: external {kind} '{shortName}' is not declared in the locals-sb-*.tf files read");
            }
            return e;
        }

        private static JsonObject Edge(string from, string to, string kind, string? label, string scope, string confidence)
        {
            var o = new JsonObject { ["from"] = from, ["to"] = to, ["kind"] = kind };
            if (label is not null) o["label"] = label;
            o["scope"] = scope;
            o["confidence"] = confidence;
            return o;
        }

        private JsonObject AppJson(AppInfo a)
        {
            var local = new JsonObject();
            if (a.Local.Port is { } port) local["port"] = port;
            if (a.Local.Url is { } url) local["url"] = url;
            local["runScript"] = a.Local.RunScript;
            return new JsonObject // v2: triggers live on handlers[]
            {
                ["id"] = a.Id, ["name"] = a.Name, ["kind"] = a.Kind, ["azureResource"] = a.AzureResource, ["cloudRoleName"] = a.AzureResource,
                ["local"] = local,
            };
        }

        private JsonObject EntityJson(EntityInfo e)
        {
            var o = new JsonObject
            {
                ["id"] = e.Id, ["kind"] = e.Kind, ["localName"] = e.LocalName, ["cloudName"] = e.CloudName,
            };
            if (e.Topic is not null) o["topic"] = e.Topic;
            if (e.ForwardTo is not null) o["forwardTo"] = e.ForwardTo;
            if (e.SqlFilter is not null) o["sqlFilter"] = e.SqlFilter;
            o["owner"] = e.Project ? "project" : "shared";
            o["scope"] = e.Scope;
            if (e.MaxDelivery is { } md) o["maxDeliveryCount"] = md;
            if (e.Cloud && e.CloudMaxDelivery is { } cmd) o["cloudMaxDeliveryCount"] = cmd;
            return o;
        }
    }
}
