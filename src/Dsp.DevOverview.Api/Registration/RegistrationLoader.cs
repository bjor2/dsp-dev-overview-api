using System.Text.Json;
using Json.Schema;

namespace Dsp.DevOverview.Api.Registration;

public sealed class RegistrationValidationException(string path, IReadOnlyList<string> errors)
    : Exception($"Registration {path} does not match registration.schema.json:\n  " + string.Join("\n  ", errors))
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

    public ProjectRegistration Load(string path)
    {
        var full = Path.GetFullPath(path);
        using var doc = JsonDocument.Parse(File.ReadAllText(full));
        var errors = Validate(doc.RootElement);
        if (errors.Count > 0) throw new RegistrationValidationException(full, errors);
        var registration = doc.RootElement.Deserialize<ProjectRegistration>(RegistrationJson.Options)
            ?? throw new InvalidDataException($"Empty registration: {full}");
        registration.SourcePath = full;
        return registration;
    }
}
