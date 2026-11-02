namespace Dsp.DevOverview.Api.AppConsole;

/// <summary>Overview:Console (ADR-004 "Console per app"). The overview is the process manager for local runs.</summary>
public sealed class ConsoleOptions
{
    /// <summary>Ring buffer size per app (lines).</summary>
    public int BufferLines { get; set; } = 10_000;
    /// <summary>Per-app log files go to {DataDir}/{LogDir}/&lt;app&gt;-yyyyMMdd.log; the attach fallback tails {DataDir}/{LogDir}/&lt;app&gt;.log.</summary>
    public string LogDir { get; set; } = "console";
    public string PwshPath { get; set; } = "pwsh";
    /// <summary>Exported to the run scripts when the overview's own environment does not set it.</summary>
    public string CustomerProjectRepo { get; set; } = "/workspace/dsp/dsp-customerproject-localrun";
    /// <summary>Extra environment for launched scripts (wins over the defaults).</summary>
    public Dictionary<string, string> Environment { get; set; } = [];
    /// <summary>Command for docker:&lt;name&gt; sources; {name} is replaced by the container name. Split on spaces.</summary>
    public string DockerLogsCommand { get; set; } = "sudo -n docker logs -f --tail 200 {name}";
    /// <summary>Only containers with this prefix can be followed (the shared stack compose project).</summary>
    public string DockerContainerPrefix { get; set; } = "dsp-shared-";
    /// <summary>Containers listed in GET /console. (No collection default: the config binder would append to it.)</summary>
    public List<string> DockerContainers { get; set; } = [];
    /// <summary>stub:ticker (one line/second, for demos and the UI). Null = Development only.</summary>
    public bool? StubTicker { get; set; }
    public int StubTickerIntervalMs { get; set; } = 1000;
    public int HeartbeatSeconds { get; set; } = 15;
    /// <summary>How long stop waits for the process tree to exit, and restart waits for the port to free.</summary>
    public int StopTimeoutSeconds { get; set; } = 10;

    public static readonly string[] DefaultDockerContainers =
        ["dsp-shared-servicebus-1", "dsp-shared-servicebus-sql-1", "dsp-shared-sql-1", "dsp-shared-azurite-1"];

    public IReadOnlyList<string> Containers => DockerContainers.Count > 0 ? DockerContainers : DefaultDockerContainers;
}
