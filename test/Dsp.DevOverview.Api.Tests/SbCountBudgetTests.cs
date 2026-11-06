using System.Diagnostics;
using Azure.Messaging.ServiceBus;
using Dsp.DevOverview.Api.Health;
using Dsp.DevOverview.Api.Registration;
using Dsp.DevOverview.Api.ServiceBus;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dsp.DevOverview.Api.Tests;

/// <summary>
/// Hang fix (Nov 4): /projects, /flow and /sb/entities must answer fast whatever Service Bus does. Reads are parallel, each
/// read has a budget, and MessagingEntityNotFound is remembered so emulator-only entities on a developer namespace don't
/// open a new link on every poll.
/// </summary>
public class SbCountBudgetTests
{
    private static ServiceBusOptions Options(double timeout = 0.3) => new() { CountTimeoutSeconds = timeout, MissingEntityCacheSeconds = 300, CountsCacheSeconds = 0 };

    [Fact]
    public async Task Missing_entity_is_cached_and_not_read_again()
    {
        var reader = new SbCountReader(Options(), NullLogger.Instance);
        var calls = 0;
        Task<long?> Read(CancellationToken _)
        {
            calls++;
            throw new ServiceBusException("The messaging entity could not be found.", ServiceBusFailureReason.MessagingEntityNotFound);
        }

        Assert.Null(await reader.CountAsync("q:topic-inspect|None", Read, default));
        Assert.Null(await reader.CountAsync("q:topic-inspect|None", Read, default));
        Assert.Equal(1, calls);
        Assert.True(reader.IsKnownMissing("q:topic-inspect|None"));
        Assert.False(reader.IsKnownMissing("q:other|None"));
    }

    [Fact]
    public async Task A_read_that_never_returns_is_unknown_within_the_budget()
    {
        var reader = new SbCountReader(Options(timeout: 0.2), NullLogger.Instance);
        var sw = Stopwatch.StartNew();
        // Ignores its token on purpose (an SDK link open that doesn't honour cancellation).
        var result = await reader.CountAsync("q:slow|None", _ => new TaskCompletionSource<long?>().Task, default);
        Assert.Null(result);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), $"took {sw.Elapsed}");
    }

    [Fact]
    public async Task Auth_failures_still_propagate()
    {
        var reader = new SbCountReader(Options(), NullLogger.Instance);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => reader.CountAsync("q:x|None", _ => throw new UnauthorizedAccessException("401"), default));
    }

    [Fact]
    public async Task Entity_reader_reads_entities_in_parallel()
    {
        var entities = Enumerable.Range(0, 8).Select(i => new RegisteredEntity { Id = $"q:e{i}", Kind = "queue", LocalName = $"e{i}" }).ToList();
        var project = new OverviewProject(new ProjectRegistration { Id = "p.dev", Project = "p", Env = "dev", Entities = entities }, null, null, "test");
        var options = new OverviewOptions { ServiceBus = new ServiceBusOptions { ConnectionString = "Endpoint=sb://localhost;SharedAccessKeyName=k;SharedAccessKey=k;UseDevelopmentEmulator=true" } };

        var sw = Stopwatch.StartNew();
        var list = await SbEntityReader.ReadAsync((_, _, _) => Task.FromResult(true),
            async (e, _, ct) => { await Task.Delay(500, ct); return new EntityCounts(1, e.Id == "q:e3" ? 2 : 0, "fake"); },
            NullLogger.Instance, options, project, default);

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), $"8 × 500 ms serial would be 4 s; took {sw.Elapsed}");
        Assert.Equal(entities.Select(e => e.Id), list.Select(s => s.EntityId)); // order kept
        Assert.Equal(2, SbEntityReader.DlqTotal(list));
    }
}
