using System.Text.Json;
using System.Text.Json.Nodes;
using Dsp.DevOverview.RegistrationGenerator;
using Json.Schema;

namespace Dsp.DevOverview.Api.Registration;

public sealed class RegistrationValidationException(string path, IReadOnlyList<string> errors)
    : Exception($"Registration {path} does not match registration.schema.json / ADDENDA-008 validators:\n  " + string.Join("\n  ", errors))
{
    public IReadOnlyList<string> Errors { get; } = errors;
}

/// <summary>Loads registration JSON files and validates each against registration.schema.json (JSON Schema 2020-12).</summary>
public sealed class RegistrationLoader
{
    private readonly JsonSchema _schema;

    public RegistrationLoader(string schemaPath)
    {
        var full = Path.GetFullPath(schemaPath);
        // A private registry so loading the schema twice (tests, hot reload) never clashes on its $id.
        _schema = JsonSchema.FromText(File.ReadAllText(full), new BuildOptions { SchemaRegistry = new SchemaRegistry() }, new Uri(full));
    }

    public IReadOnlyList<string> Validate(JsonElement instance)
    {
        var result = _schema.Evaluate(instance, new EvaluationOptions { OutputFormat = OutputFormat.List });
        if (result.IsValid) return [];
        return (result.Details ?? [])
            .Where(d => d.Errors is { Count: > 0 })
            .SelectMany(d => d.Errors!.Select(e => $"{d.InstanceLocation} ({d.EvaluationPath}): {e.Key} {e.Value}"))
            .DefaultIfEmpty("invalid (no detail)")
            .ToList();
    }

    /// <summary>
    /// Schema check, then (v1 = no schemaVersion) an in-memory upgrade to v2 that is schema-checked again, then the
    /// ADDENDA-008 cross-field validators (V-1..V-5, I-1). The file is never rewritten.
    /// </summary>
    public ProjectRegistration Load(string path)
    {
        var full = Path.GetFullPath(path);
        var text = File.ReadAllText(full);
        using (var doc = JsonDocument.Parse(text))
        {
            var errors = Validate(doc.RootElement);
            if (errors.Count > 0) throw new RegistrationValidationException(full, errors);
        }
        var node = JsonNode.Parse(text)!.AsObject();
        IReadOnlyList<string> notes = [];
        if (node["schemaVersion"] is null)
        {
            (node, notes) = RegistrationUpgrade.ToV2(node);
            using var upgraded = JsonDocument.Parse(node.ToJsonString());
            var errors = Validate(upgraded.RootElement);
            if (errors.Count > 0) throw new RegistrationValidationException(full, errors.Select(e => "after v1→v2 upgrade: " + e).ToList());
        }
        var issues = RegistrationValidator.Check(node);
        if (issues.Count > 0) throw new RegistrationValidationException(full, issues.Select(i => i.ToString()).ToList());
        var registration = node.Deserialize<ProjectRegistration>(RegistrationJson.Options)
            ?? throw new InvalidDataException($"Empty registration: {full}");
        registration.SourcePath = full;
        registration.UpgradeNotes = notes;
        return registration;
    }
}
