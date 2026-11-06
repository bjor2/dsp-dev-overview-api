using System.Text.RegularExpressions;

namespace Dsp.DevOverview.Api.Architecture;

/// <summary>
/// V-003 safe resolution: catalog paths are sibling basenames, and every file actually read (index.json included) must resolve,
/// after following symlinks, to a regular file under the project's architecture directory, itself under the layers root.
/// </summary>
public static partial class ArchitecturePaths
{
    public const long MaxDocumentBytes = 1024 * 1024;

    [GeneratedRegex(@"^[a-z0-9][a-z0-9.-]*\.json$")]
    private static partial Regex Basename();

    [GeneratedRegex(@"^[a-z0-9-]+$")]
    private static partial Regex ProjectName();

    public static bool IsSafeBasename(string path) => Basename().IsMatch(path);

    /// <summary>
    /// Directory for a registration's project, or null when the project name could never be a safe directory. A <c>_</c>-prefixed
    /// sibling (e.g. <c>_obsolete/</c>, retired layers kept for reference) never matches, and catalog paths are sibling basenames
    /// that cannot start with <c>_</c> or name a subdirectory, so such a directory is never a project or a layer source.
    /// </summary>
    public static string? ProjectDir(string layersRoot, string project) =>
        ProjectName().IsMatch(project) ? Path.Combine(Path.GetFullPath(layersRoot), project) : null;

    /// <summary>Contained real path of <paramref name="basename"/> in <paramref name="projectDir"/>, or an issue (basename only, no machine path).</summary>
    public static (string? Path, string? Issue) Resolve(string layersRoot, string projectDir, string basename)
    {
        if (!IsSafeBasename(basename)) return (null, $"V-003: catalog path '{Shown(basename)}' is not a sibling .json basename");
        try
        {
            var realRoot = RealPath(layersRoot);
            var realDir = RealPath(projectDir);
            if (!IsUnder(realDir, realRoot)) return (null, "V-003: project architecture directory resolves outside the layers root");
            var real = RealPath(Path.Combine(projectDir, basename));
            if (!IsUnder(real, realDir)) return (null, $"V-003: '{basename}' resolves outside the project architecture directory");
            if (!File.Exists(real)) return (null, $"V-003: '{basename}' is not a readable file");
            if (new FileInfo(real).Length > MaxDocumentBytes) return (null, $"V-003: '{basename}' exceeds {MaxDocumentBytes} bytes");
            return (real, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (null, $"V-003: '{basename}' could not be resolved");
        }
    }

    /// <summary>Lexically normalised path with every symlink component resolved (relative targets against the link's directory).</summary>
    public static string RealPath(string path, int depth = 0)
    {
        if (depth > 32) throw new IOException("Too many levels of symbolic links.");
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full)!;
        var current = root;
        foreach (var part in full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            if (new FileInfo(current).LinkTarget is { } target)
                current = RealPath(Path.GetFullPath(target, Path.GetDirectoryName(current)!), depth + 1);
        }
        return current;
    }

    private static bool IsUnder(string path, string dir) =>
        path.StartsWith(Path.TrimEndingDirectorySeparator(dir) + Path.DirectorySeparatorChar, StringComparison.Ordinal);

    // Rejected input is echoed only when it is short and has no separators, so an issue never carries a path.
    private static string Shown(string value) =>
        value.Length <= 64 && value.IndexOfAny(['/', '\\', ':']) < 0 ? value : "(rejected)";
}
