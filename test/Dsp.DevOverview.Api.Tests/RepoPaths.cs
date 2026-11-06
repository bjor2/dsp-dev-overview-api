namespace Dsp.DevOverview.Api.Tests;

internal static class RepoPaths
{
    public static readonly string Root = FindRoot();
    public static string Schema => Path.Combine(Root, "architecture", "registration.schema.json");
    public static string Registration => Path.Combine(Root, "architecture", "registrations", "customerproject.dev.json");
    public static string Catalog => Path.Combine(Root, "catalog", "customerproject.samples.json");
    /// <summary>The seed kit (read-only oracle). Only read, never built or executed.</summary>
    public const string Kit = "/workspace/dsp/local-dev/customerproject/seed-messages/Program.cs";

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Dsp.DevOverview.sln"))) return dir.FullName;
        throw new DirectoryNotFoundException("Dsp.DevOverview.sln not found");
    }
}
