using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dsp.DevOverview.Api.Registration;
using Json.Schema;

namespace Dsp.DevOverview.Api.Architecture;

/// <summary>architecture-layer.schema.json (Draft 2020-12), evaluated per document type so errors name one branch, not the oneOf.</summary>
public sealed class ArchitectureSchema
{
    // Neutral base URI: schema locations never carry the machine path of the schema file.
    private static readonly Uri Base = new("https://dsp-dev-overview.invalid/");
    private readonly JsonSchema _layer;
    private readonly JsonSchema _catalog;

    public ArchitectureSchema(string schemaPath)
    {
        var text = File.ReadAllText(schemaPath);
        var id = new Uri(Base, JsonNode.Parse(text)?["$id"]?.GetValue<string>() ?? "architecture-layer.schema.json");
        var build = new BuildOptions { SchemaRegistry = new SchemaRegistry() };
        JsonSchema.FromText(text, build, Base);
        _layer = JsonSchema.FromText($$"""{"$ref":"{{id}}#/$defs/layer"}""", build, new Uri(Base, "layer"));
        _catalog = JsonSchema.FromText($$"""{"$ref":"{{id}}#/$defs/catalog"}""", build, new Uri(Base, "catalog"));
    }

    public IReadOnlyList<string> ValidateLayer(JsonElement instance) => Validate(_layer, instance);
    public IReadOnlyList<string> ValidateCatalog(JsonElement instance) => Validate(_catalog, instance);

    private static IReadOnlyList<string> Validate(JsonSchema schema, JsonElement instance)
    {
        var result = schema.Evaluate(instance, new EvaluationOptions { OutputFormat = OutputFormat.List, RequireFormatValidation = true });
        if (result.IsValid) return [];
        var all = (result.Details ?? [])
            .Where(d => d.Errors is { Count: > 0 })
            .SelectMany(d => d.Errors!.Select(e => (Path: d.EvaluationPath.ToString(), e.Key, Text: $"{(d.InstanceLocation.ToString() is { Length: > 0 } at ? at : "/")}: {e.Value}")))
            .ToList();
        // List output also reports failing branches of a passing anyOf/if and per-keyword roll-ups; keep the leaf causes.
        var leaves = all.Where(e => !e.Path.Contains("/anyOf/") && !e.Path.Contains("/if") && !RollUps.Contains(e.Key)).ToList();
        return (leaves.Count > 0 ? leaves : all).Select(e => e.Text).Distinct().DefaultIfEmpty("does not match the schema").ToList();
    }

    private static readonly HashSet<string> RollUps = ["properties", "items", "allOf", "$ref"];
}

/// <summary>
/// Loads <c>{layersRoot}/{project}/index.json</c> and every layer it lists into one immutable snapshot, or rejects the whole
/// catalog (ADDENDA-011 §2). Read-only: never writes, repairs, generates or fetches anything.
/// </summary>
public static class ArchitectureLoader
{
    public const string IndexFile = "index.json";
    public const int SchemaVersion = 1;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static ArchitectureState Load(string layersRoot, ProjectRegistration registration, Func<ArchitectureSchema> schema)
    {
        var dir = ArchitecturePaths.ProjectDir(layersRoot, registration.Project);
        if (dir is null || !Present(Path.Combine(dir, IndexFile))) return ArchitectureState.Absent;

        ArchitectureSchema validator;
        try { validator = schema(); }
        catch (Exception)
        {
            return ArchitectureState.Invalid(["V-001: architecture schema unavailable"]);
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var (catalogNode, issues) = Read(layersRoot, dir, IndexFile, validator.ValidateCatalog, hash);
        if (catalogNode is null) return ArchitectureState.Invalid(issues);
        var catalog = catalogNode.Deserialize<ArchitectureCatalogDoc>(Json)!;
        // The catalog selects one registration; another env of the same project simply has no architecture (no cross-env fallback).
        if (catalog.Project == registration.Project && catalog.RegistrationId != registration.Id) return ArchitectureState.Absent;

        var documents = new List<(ArchitectureLayerDoc Doc, JsonObject Node)>();
        foreach (var entry in catalog.Layers)
        {
            var (node, layerIssues) = Read(layersRoot, dir, entry.Path, validator.ValidateLayer, hash, ContainerOnlyRoot);
            issues = [.. issues, .. layerIssues];
            if (node is not null) documents.Add((node.Deserialize<ArchitectureLayerDoc>(Json)!, node));
        }
        if (issues.Count > 0) return ArchitectureState.Invalid(issues);

        issues = ArchitectureValidator.Check(catalog, documents.Select(d => d.Doc).ToList(), registration);
        return issues.Count > 0
            ? ArchitectureState.Invalid(issues)
            : new ArchitectureState(ArchitectureStatus.Valid, Build(catalog, documents, "sha256:" + Convert.ToHexStringLower(hash.GetHashAndReset())), []);
    }

    // A dangling symlink is present (and then rejected), not absent.
    private static bool Present(string path) => File.Exists(path) || new FileInfo(path).LinkTarget is not null;

    // V-011 (ADDENDA-011.1): the schema already rejects a container-only root (roots require flowRef.handlerId) as V-001; this names
    // the rule as well, so the diagnostic says why. No legacy container-root mode exists.
    private static IReadOnlyList<string> ContainerOnlyRoot(string basename, JsonElement layer) =>
        layer.TryGetProperty("parentLayerId", out var parent) && parent.ValueKind == JsonValueKind.Null
        && layer.TryGetProperty("flowRef", out var flow) && flow.ValueKind == JsonValueKind.Object
        && !(flow.TryGetProperty("handlerId", out var handler) && handler.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(handler.GetString()))
            ? [$"V-011: {basename} root has no flowRef.handlerId (container-only roots are not Explore targets)"]
            : [];

    // Every file read feeds UTF8(basename) + "\0" + bytes into the catalog hash, in read order (index.json, then catalog order).
    private static (JsonObject? Node, IReadOnlyList<string> Issues) Read(string layersRoot, string dir, string basename,
        Func<JsonElement, IReadOnlyList<string>> validate, IncrementalHash hash, Func<string, JsonElement, IReadOnlyList<string>>? rules = null)
    {
        var (path, issue) = ArchitecturePaths.Resolve(layersRoot, dir, basename);
        if (issue is not null) return (null, [issue]);
        try
        {
            var bytes = File.ReadAllBytes(path!);
            hash.AppendData(Encoding.UTF8.GetBytes(basename + "\0"));
            hash.AppendData(bytes);
            using var doc = JsonDocument.Parse(bytes);
            var errors = validate(doc.RootElement);
            if (errors.Count > 0) return (null, [.. errors.Select(e => $"V-001: {basename} {e}"), .. rules?.Invoke(basename, doc.RootElement) ?? []]);
            return (JsonNode.Parse(bytes)!.AsObject(), []);
        }
        catch (JsonException ex) { return (null, [$"V-001: {basename} is not valid JSON (line {(ex.LineNumber ?? 0) + 1})"]); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return (null, [$"V-003: {basename} could not be read"]); }
    }

    private static ArchitectureCatalog Build(ArchitectureCatalogDoc catalog, IReadOnlyList<(ArchitectureLayerDoc Doc, JsonObject Node)> documents,
        string catalogHash)
    {
        var docs = documents.ToDictionary(d => d.Doc.Id, d => d.Doc, StringComparer.Ordinal);

        // Validated forest: every parent walk terminates at a root.
        List<ArchitectureLayerDoc> Chain(ArchitectureLayerDoc layer)
        {
            var chain = new List<ArchitectureLayerDoc> { layer };
            while (chain[^1].ParentLayerId is { } parent) chain.Add(docs[parent]);
            chain.Reverse();
            return chain;
        }

        var layers = new Dictionary<string, ArchitectureLayer>(StringComparer.Ordinal);
        foreach (var (doc, node) in documents)
        {
            var chain = Chain(doc);
            var served = (JsonObject)node.DeepClone();
            served.Remove("$schema"); // relative schema path is a file-system detail
            var children = doc.Nodes.Where(n => n.OpensLayerId is not null)
                .Select(n => new ArchitectureChild(n.OpensLayerId!, docs[n.OpensLayerId!].Title, n.Id))
                .OrderBy(c => c.LayerId, StringComparer.Ordinal)
                .ToList();
            layers[doc.Id] = new ArchitectureLayer(doc.Id, doc.Title, doc.ParentLayerId, JsonSerializer.SerializeToElement(served),
                chain[0].FlowRef!, chain.Select(l => new ArchitectureCrumb(l.Id, l.Title)).ToList(), children);
        }
        var roots = documents.Select(d => d.Doc).Where(d => d.ParentLayerId is null)
            .Select(d => new ArchitectureRoot(d.Id, d.Title, d.FlowRef!))
            .OrderBy(r => r.LayerId, StringComparer.Ordinal)
            .ToList();
        return new ArchitectureCatalog(catalog.RegistrationId, roots, layers, catalogHash);
    }
}
