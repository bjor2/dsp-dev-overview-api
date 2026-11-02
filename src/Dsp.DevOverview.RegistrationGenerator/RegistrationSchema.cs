using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;

namespace Dsp.DevOverview.RegistrationGenerator;

/// <summary>registration.schema.json (JSON Schema 2020-12), same evaluation as the API's RegistrationLoader.</summary>
public sealed class RegistrationSchema
{
    private readonly JsonSchema _schema;

    private RegistrationSchema(JsonSchema schema) => _schema = schema;

    public static RegistrationSchema Load(string schemaPath)
    {
        var full = Path.GetFullPath(schemaPath);
        // Private registry: loading twice (tests, every refresh) must not clash on the schema's $id.
        return new(JsonSchema.FromText(File.ReadAllText(full), new BuildOptions { SchemaRegistry = new SchemaRegistry() }, new Uri(full)));
    }

    public IReadOnlyList<string> Validate(JsonNode record)
    {
        using var doc = JsonDocument.Parse(record.ToJsonString());
        var result = _schema.Evaluate(doc.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });
        if (result.IsValid) return [];
        return (result.Details ?? [])
            .Where(d => d.Errors is { Count: > 0 })
            .SelectMany(d => d.Errors!.Select(e => $"{d.InstanceLocation} ({d.EvaluationPath}): {e.Key} {e.Value}"))
            .DefaultIfEmpty("invalid (no detail)")
            .ToList();
    }
}
