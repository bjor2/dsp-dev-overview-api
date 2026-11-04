using System.Collections.Concurrent;
using Azure.Messaging.ServiceBus;

namespace Dsp.DevOverview.Api.ServiceBus;

/// <summary>
/// Bounded depth reads: every read gets <see cref="ServiceBusOptions.CountTimeoutSeconds"/> (over budget = null, i.e.
/// unknown), MessagingEntityNotFound is cached as missing for <see cref="ServiceBusOptions.MissingEntityCacheSeconds"/>
/// (no new link per poll for an entity the namespace doesn't have), and results are shared for a few seconds between
/// /flow, /sb/entities and /projects. Auth and unreachable failures still propagate (SB_AUTH_FAILED / SB_UNREACHABLE).
/// </summary>
public sealed class SbCountReader(ServiceBusOptions options, ILogger log, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _missing = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, (DateTimeOffset At, EntityCounts Counts)> _recent = new(StringComparer.Ordinal);

    public bool IsKnownMissing(string key) => _missing.TryGetValue(key, out var until) && _clock.GetUtcNow() < until;

    /// <summary>One sub-queue count under the per-call budget; null when missing (cached), timed out or failed softly.</summary>
    public async Task<long?> CountAsync(string key, Func<CancellationToken, Task<long?>> read, CancellationToken ct)
    {
        if (IsKnownMissing(key)) return null;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(0.1, options.CountTimeoutSeconds)));
        try
        {
            // WaitAsync: the budget holds even if the SDK call ignores the token while opening a link.
            return await read(cts.Token).WaitAsync(cts.Token);
        }
        catch (Exception ex) when (IsNotFound(ex))
        {
            MarkMissing(key, ex);
            return null;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            log.LogWarning("Depth read for {Key} exceeded {Seconds}s; reported as unknown.", key, options.CountTimeoutSeconds);
            return null;
        }
    }

    public void MarkMissing(string key, Exception ex)
    {
        var ttl = TimeSpan.FromSeconds(Math.Max(0, options.MissingEntityCacheSeconds));
        if (_missing.TryGetValue(key, out var until) && _clock.GetUtcNow() < until) return;
        _missing[key] = _clock.GetUtcNow() + ttl;
        log.LogWarning("{Key} not found on the Service Bus namespace; depth reported as unknown for {Ttl}s ({Message}).",
            key, ttl.TotalSeconds, ex.Message.Split('\n')[0].Trim());
    }

    /// <summary>Recent (shared) counts for an entity, read through <paramref name="read"/> when stale.</summary>
    public async Task<EntityCounts> CountsAsync(string entityKey, Func<CancellationToken, Task<EntityCounts>> read, CancellationToken ct)
    {
        var ttl = TimeSpan.FromSeconds(Math.Max(0, options.CountsCacheSeconds));
        if (_recent.TryGetValue(entityKey, out var hit) && _clock.GetUtcNow() - hit.At < ttl) return hit.Counts;
        var counts = await read(ct);
        if (ttl > TimeSpan.Zero) _recent[entityKey] = (_clock.GetUtcNow(), counts);
        return counts;
    }

    /// <summary>Runs <paramref name="read"/> for every item with bounded parallelism; results keep the input order.</summary>
    public static async Task<IReadOnlyList<TResult>> ForAllAsync<T, TResult>(IReadOnlyList<T> items, int parallelism, Func<T, CancellationToken, Task<TResult>> read, CancellationToken ct)
    {
        using var gate = new SemaphoreSlim(Math.Max(1, parallelism));
        return await Task.WhenAll(items.Select(async item =>
        {
            await gate.WaitAsync(ct);
            try { return await read(item, ct); }
            finally { gate.Release(); }
        }));
    }

    public static bool IsNotFound(Exception ex) => ex switch
    {
        ServiceBusException { Reason: ServiceBusFailureReason.MessagingEntityNotFound } => true,
        Azure.RequestFailedException { Status: 404 } => true,
        _ => ex.Message.Contains("MessagingEntityNotFound", StringComparison.Ordinal)
             || (ex.InnerException is not null && IsNotFound(ex.InnerException)),
    };
}
