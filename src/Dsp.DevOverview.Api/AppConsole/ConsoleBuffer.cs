using System.Threading.Channels;

namespace Dsp.DevOverview.Api.AppConsole;

/// <summary>
/// Per-app ring buffer of ConsoleChunks (one line per stdout/stderr chunk, so capacity = lines) plus live subscribers.
/// Seq is monotonic for the life of the overview process, across restarts, so Last-Event-ID replay works.
/// </summary>
public sealed class ConsoleBuffer(string appId, int capacity)
{
    private readonly ConsoleChunk[] _ring = new ConsoleChunk[Math.Max(1, capacity)];
    private readonly List<Channel<ConsoleChunk>> _subscribers = [];
    private readonly object _gate = new();
    private int _start;
    private int _count;
    private long _seq;

    public long LastSeq { get { lock (_gate) return _seq; } }
    public int Count { get { lock (_gate) return _count; } }

    public ConsoleChunk Append(string stream, IReadOnlyList<string> lines, bool attached)
    {
        lock (_gate)
        {
            var chunk = new ConsoleChunk(appId, ++_seq, DateTimeOffset.UtcNow, stream, lines, attached);
            if (_count < _ring.Length) _ring[(_start + _count++) % _ring.Length] = chunk;
            else { _ring[_start] = chunk; _start = (_start + 1) % _ring.Length; }
            foreach (var s in _subscribers) s.Writer.TryWrite(chunk);
            return chunk;
        }
    }

    /// <summary>Chunks with seq &gt; afterSeq still in the ring (all when null).</summary>
    public IReadOnlyList<ConsoleChunk> Since(long? afterSeq)
    {
        lock (_gate) return SinceLocked(afterSeq);
    }

    private List<ConsoleChunk> SinceLocked(long? afterSeq)
    {
        var list = new List<ConsoleChunk>(_count);
        for (var i = 0; i < _count; i++)
        {
            var c = _ring[(_start + i) % _ring.Length];
            if (afterSeq is null || c.Seq > afterSeq) list.Add(c);
        }
        return list;
    }

    /// <summary>Atomically snapshot the replay and subscribe, so no chunk is lost or duplicated between the two.</summary>
    public (IReadOnlyList<ConsoleChunk> Replay, ChannelReader<ConsoleChunk> Live, IDisposable Subscription) Subscribe(long? afterSeq)
    {
        var channel = Channel.CreateBounded<ConsoleChunk>(new BoundedChannelOptions(Math.Max(1, _ring.Length))
        { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
        lock (_gate)
        {
            _subscribers.Add(channel);
            return (SinceLocked(afterSeq), channel.Reader, new Unsubscribe(this, channel));
        }
    }

    private sealed class Unsubscribe(ConsoleBuffer buffer, Channel<ConsoleChunk> channel) : IDisposable
    {
        public void Dispose()
        {
            lock (buffer._gate) buffer._subscribers.Remove(channel);
            channel.Writer.TryComplete();
        }
    }
}
