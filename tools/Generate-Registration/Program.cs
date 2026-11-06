// Generates the registration record for a project (ADR-002) from read-only sources and merges it into the current one.
//
//   dotnet run --project tools/Generate-Registration -- [--project customerproject] [--env dev] [--out <path>] [options]
//
//   --out <path>          where to write (default: the file the API loads, from appsettings.json Overview:Registrations)
//   --current <path>      record to merge into (default: --out); its `overrides` and manual edges are preserved
//   --dry-run             print diff + gaps, write nothing
//   --stdout              print the generated JSON instead of writing it
//   --include-external    also emit cloud subscriptions linking the project to other services (foreign topics/queues)
//   --tf-dir <dir>        Terraform env dir (default /workspace/claranet/terraform/870-brodrene-dahl-dsp/dev/eu-west)
//   --repo <dir>          service repo (default /workspace/dsp/dsp-<project>-localrun)
//   --local-dev <dir>     local-dev folder (default /workspace/dsp/local-dev/<project>)
//   --catalog <path>      samples.catalog value (default catalog/<project>.samples.json, relative to the overview root)
//   --root <dir>          dev-overview root (default: folder holding Dsp.DevOverview.sln)
//
// Exit codes: 0 ok, 1 output fails registration.schema.json (nothing written), 2 usage, 3 missing input,
//             4 ADDENDA-008 validator issue (V-1..V-5, I1_UNRESOLVED / I1_VIOLATION; nothing written).
//
//   dotnet run --project tools/Generate-Registration -- export-layout [--api http://127.0.0.1:5090] [--id customerproject.dev]
//                                                       [--out <path>] [--dry-run] [--root <dir>]
//   Fetches GET {api}/api/projects/{id}/flow/positions/export (defaults ← runtime positions) and writes it into the
//   registration's layoutDefaults (sorted keys, whole pixels; identical content = no write). Exit 0 ok, 2 usage, 3 API error.
using System.Text.Json.Nodes;
using Dsp.DevOverview.RegistrationGenerator;

if (args.Length > 0 && args[0] == "export-layout") return await ExportLayout(args[1..]);

var opts = new Dictionary<string, string?>(StringComparer.Ordinal);
string[] flags = ["--dry-run", "--stdout", "--include-external", "--help", "-h"];
for (var i = 0; i < args.Length; i++)
{
    if (flags.Contains(args[i])) { opts[args[i]] = "true"; continue; }
    if (!args[i].StartsWith("--", StringComparison.Ordinal) || i + 1 >= args.Length) return Usage($"unexpected argument '{args[i]}'");
    opts[args[i]] = args[++i];
}
if (opts.ContainsKey("--help") || opts.ContainsKey("-h")) return Usage(null);

var root = opts.GetValueOrDefault("--root") is { } r ? Path.GetFullPath(r) : FindRoot();
var inputs = new GeneratorInputs
{
    Project = opts.GetValueOrDefault("--project") ?? "customerproject",
    Env = opts.GetValueOrDefault("--env") ?? "dev",
    OverviewRoot = root,
    TerraformEnvDir = opts.GetValueOrDefault("--tf-dir") ?? GeneratorInputs.DefaultTerraformEnvDir,
    ServiceRepo = opts.GetValueOrDefault("--repo"),
    LocalDevDir = opts.GetValueOrDefault("--local-dev"),
    SamplesCatalog = opts.GetValueOrDefault("--catalog"),
    IncludeExternalSubscriptions = opts.ContainsKey("--include-external"),
};
var outPath = Path.GetFullPath(opts.GetValueOrDefault("--out") ?? RegistrationWriter.LoadedRegistrationPath(root, inputs.Id));
var currentPath = Path.GetFullPath(opts.GetValueOrDefault("--current") ?? outPath);

GenerationResult result;
try
{
    result = RegistrationGenerator.Generate(inputs, RegistrationGenerator.ReadCurrent(currentPath), DateTimeOffset.Now);
}
catch (Exception ex) when (ex is GeneratorInputException or FormatException or DirectoryNotFoundException or FileNotFoundException)
{
    Console.Error.WriteLine($"error: {ex.Message}");
    return 3;
}

var log = opts.ContainsKey("--stdout") ? Console.Error : Console.Out;
log.WriteLine($"registration {inputs.Id} (current: {(File.Exists(currentPath) ? currentPath : "none")})");
log.WriteLine($"diff vs current:");
foreach (var line in result.Diff) log.WriteLine($"  {line}");
log.WriteLine($"gaps ({result.Gaps.Count}):");
foreach (var gap in result.Gaps) log.WriteLine($"  - {gap}");

if (result.Issues.Count > 0)
{
    Console.Error.WriteLine($"validation: {result.Issues.Count} issue(s) — nothing written:");
    foreach (var issue in result.Issues) Console.Error.WriteLine($"  {issue}");
    if (result.SchemaErrors.Count == 0) return 4;
}
if (result.SchemaErrors.Count > 0)
{
    Console.Error.WriteLine($"schema: INVALID against {inputs.SchemaPath} — nothing written:");
    foreach (var e in result.SchemaErrors) Console.Error.WriteLine($"  {e}");
    return 1;
}
if (result.Issues.Count > 0) return 4;
log.WriteLine($"schema: valid ({inputs.SchemaPath}); validators V-1..V-5 + I-1: ok");

if (opts.ContainsKey("--stdout")) { Console.Out.Write(result.ToJson()); return 0; }
if (opts.ContainsKey("--dry-run")) { log.WriteLine("dry run: nothing written"); return 0; }

var backup = RegistrationWriter.Write(outPath, result, Path.Combine(root, "data", "registrations", "history"), DateTimeOffset.Now);
log.WriteLine(backup is null ? $"wrote {outPath} (new, or identical to the current file)" : $"wrote {outPath}; previous record saved to {backup}");
log.WriteLine("hot-reload a running API: curl -X POST http://127.0.0.1:5090/api/projects/" + inputs.Id + "/registration/refresh?source=file");
return 0;

static int Usage(string? error)
{
    if (error is not null) Console.Error.WriteLine($"error: {error}");
    Console.Error.WriteLine("usage: Generate-Registration [--project customerproject] [--env dev] [--out <path>] [--current <path>] [--dry-run] [--stdout]");
    Console.Error.WriteLine("                             [--include-external] [--tf-dir <dir>] [--repo <dir>] [--local-dev <dir>] [--catalog <path>] [--root <dir>]");
    return error is null ? 0 : 2;
}

static string FindRoot()
{
    foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Dsp.DevOverview.sln"))) return dir.FullName;
    throw new DirectoryNotFoundException("Dsp.DevOverview.sln not found; pass --root");
}

static async Task<int> ExportLayout(string[] args)
{
    var o = new Dictionary<string, string?>(StringComparer.Ordinal);
    for (var i = 0; i < args.Length; i++)
    {
        if (args[i] is "--dry-run") { o[args[i]] = "true"; continue; }
        if (!args[i].StartsWith("--", StringComparison.Ordinal) || i + 1 >= args.Length)
        {
            Console.Error.WriteLine($"error: unexpected argument '{args[i]}'");
            Console.Error.WriteLine("usage: Generate-Registration export-layout [--api http://127.0.0.1:5090] [--id customerproject.dev] [--out <path>] [--dry-run] [--root <dir>]");
            return 2;
        }
        o[args[i]] = args[++i];
    }
    var root = o.GetValueOrDefault("--root") is { } r ? Path.GetFullPath(r) : FindRoot();
    var id = o.GetValueOrDefault("--id") ?? "customerproject.dev";
    var api = (o.GetValueOrDefault("--api") ?? Environment.GetEnvironmentVariable("DSP_OVERVIEW_API") ?? "http://127.0.0.1:5090").TrimEnd('/');
    var outPath = Path.GetFullPath(o.GetValueOrDefault("--out") ?? RegistrationWriter.LoadedRegistrationPath(root, id));

    JsonObject export;
    var url = $"{api}/api/projects/{Uri.EscapeDataString(id)}/flow/positions/export";
    try
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        using var response = await http.GetAsync(url);
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode) { Console.Error.WriteLine($"error: GET {url} → {(int)response.StatusCode}: {body[..Math.Min(body.Length, 300)]}"); return 3; }
        export = JsonNode.Parse(body) as JsonObject ?? throw new FormatException("export is not a JSON object");
    }
    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or FormatException)
    {
        Console.Error.WriteLine($"error: GET {url} failed: {ex.Message}");
        return 3;
    }
    if (export["positions"] is not JsonObject positions) { Console.Error.WriteLine("error: export has no positions object"); return 3; }

    var record = RegistrationGenerator.ReadCurrent(outPath);
    if (record is null) { Console.Error.WriteLine($"error: registration {outPath} not found"); return 3; }
    var updated = LayoutDefaults.Apply(record, positions.Select(kv =>
        KeyValuePair.Create(kv.Key, (kv.Value!["x"]!.GetValue<double>(), kv.Value!["y"]!.GetValue<double>()))));
    var json = RegistrationGenerator.Serialize(updated);

    Console.WriteLine($"layout export {url}: {positions.Count} positions ({export["fromStored"]} runtime, {export["fromDefaults"]} defaults)");
    foreach (var w in LayoutDefaults.Warnings(updated)) Console.WriteLine($"  warning: {w}");
    var schema = RegistrationSchema.Load(Path.Combine(root, "architecture", "registration.schema.json")).Validate(updated);
    if (schema.Count > 0) { foreach (var e in schema) Console.Error.WriteLine($"  schema: {e}"); return 1; }
    var before = File.ReadAllText(outPath);
    if (before == json) { Console.WriteLine($"{outPath}: layoutDefaults already up to date (no write)"); return 0; }
    if (o.ContainsKey("--dry-run")) { Console.WriteLine($"dry run: {outPath} would change (layoutDefaults)"); return 0; }
    var backup = RegistrationWriter.WriteJson(outPath, json, id, Path.Combine(root, "data", "registrations", "history"), DateTimeOffset.Now);
    Console.WriteLine($"wrote layoutDefaults into {outPath}" + (backup is null ? "" : $"; previous record saved to {backup}"));
    Console.WriteLine($"hot-reload a running API: curl -X POST {api}/api/projects/{id}/registration/refresh?source=file");
    return 0;
}
