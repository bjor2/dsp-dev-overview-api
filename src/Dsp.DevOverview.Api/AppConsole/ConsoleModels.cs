using System.Text.Json.Serialization;

namespace Dsp.DevOverview.Api.AppConsole;

// ConsoleChunk from HANDOFF-ui-ux-data-contracts.md; /restart from ADDENDUM-ui-wireframe-q.md.

public sealed record ConsoleChunk(string AppId, long Seq, DateTimeOffset Timestamp, string Stream, IReadOnlyList<string> Lines, bool Attached);

public sealed record ConsoleAttachResult(bool Attached);

/// <summary>
/// One row of GET /console. <c>source</c>: process (pwsh run script) | tail (fallback log file) | docker | stub.
/// <c>managed</c> = the overview started it and it is running (what Flow shows as <c>app.attached</c>).
/// </summary>
public sealed record ConsoleAppStatus(
    string AppId, string Name, string Source, bool Attached, bool Managed,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Pid,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Port,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? StartedAt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? ExitCode,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Command,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? LogFile,
    long LastSeq);

/// <summary>Read side used by Flow: which apps run as overview-managed processes.</summary>
public interface IConsoleStatusProvider
{
    bool IsManagedRunning(string projectId, string appId);
}
