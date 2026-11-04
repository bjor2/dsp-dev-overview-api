using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using Dsp.DevOverview.Api.Registration;
using Dsp.DevOverview.Catalog;

namespace Dsp.DevOverview.Api.ServiceBus;

public static class PurgePolicy
{
    /// <summary>Purge = receive-and-delete; allowed only against the local emulator (ADR-005, HANDOFF deltas).</summary>
    public static void EnsureAllowed(ServiceBusOptions options)
    {
        if (options.Scope != SbScope.Local || !options.IsSandbox)
            throw new ApiException(StatusCodes.Status403Forbidden, "PURGE_CLOUD_FORBIDDEN",
                "Purge is only allowed against the local Service Bus emulator (UseDevelopmentEmulator=true, scope=local).");
    }

    /// <summary>No cloud writes in v1: inject/send and DLQ retry/discard also require the emulator.</summary>
    public static void EnsureWritable(ServiceBusOptions options, string what = "Sending")
    {
        if (options.Scope != SbScope.Local || !options.IsSandbox)
            throw new ApiException(StatusCodes.Status403Forbidden, "CLOUD_WRITE_FORBIDDEN",
                $"{what} is only allowed against the local Service Bus emulator; cloud writes need Bjor's explicit OK (ADR-005).");
    }
}

public sealed record EntityCounts(long? Active, long? DeadLetter, string Source);

/// <summary>Outcome of a DLQ retry/discard: the sequence numbers acted on and the requested ones not found in the DLQ.</summary>
public sealed record DlqSettleOutcome(IReadOnlyList<long> Settled, IReadOnlyList<long> NotFound);

/// <summary>All Service Bus access for the overview: depth (admin, falling back to peek counts), peek, DLQ list/retry/discard, send, purge.</summary>
public sealed class SbGateway : IAsyncDisposable
{
    private static readonly TimeSpan AdminRetryAfter = TimeSpan.FromSeconds(60);
    private const int MaxBodyBytes = 64 * 1024;

    private readonly ServiceBusOptions _options;
    private readonly ILogger<SbGateway> _log;
    private readonly Lazy<ServiceBusClient> _client;
    private readonly Lazy<ServiceBusAdministrationClient?> _admin;
    private readonly ConcurrentDictionary<string, ServiceBusSender> _senders = new();
    private DateTime _adminUnavailableUntil = DateTime.MinValue;
    private volatile bool _warm; // a probe has succeeded once: steady-state budget applies
    private long _probeOkTicks;  // UTC ticks of the last successful probe
    private readonly SbCountReader _counts;

    public SbGateway(ServiceBusOptions options, ILogger<SbGateway> log)
    {
        _options = options;
        _log = log;
        _counts = new SbCountReader(options, log);
        _client = new(() => CreateClient(_options, Credential));
        _admin = new(CreateAdmin);
    }

    public ServiceBusOptions Options => _options;

    /// <summary>Shared logger (SbEntityReader logs read failures through it).</summary>
    public ILogger Log => _log;

    /// <summary>Overridable for tests; created lazily only in credential mode.</summary>
    public Azure.Core.TokenCredential Credential { get => _credential ??= new Azure.Identity.DefaultAzureCredential(); set => _credential = value; }
    private Azure.Core.TokenCredential? _credential;

    private static ServiceBusClientOptions ClientOptions(ServiceBusOptions o) => new()
    {
        TransportType = o.EffectiveTransport,
        RetryOptions = new ServiceBusRetryOptions { MaxRetries = 1, TryTimeout = TimeSpan.FromSeconds(10) },
    };

    /// <summary>Connection string wins; else FullyQualifiedNamespace + credential; else 503 SB_NOT_CONFIGURED.</summary>
    public static ServiceBusClient CreateClient(ServiceBusOptions options, Azure.Core.TokenCredential credential) => options.AuthMode switch
    {
        SbAuthMode.ConnectionString => new ServiceBusClient(options.ConnectionString, ClientOptions(options)),
        SbAuthMode.Credential => new ServiceBusClient(options.NormalizedNamespace, credential, ClientOptions(options)),
        _ => throw NotConfigured(options),
    };

    public static ApiException NotConfigured(ServiceBusOptions options) =>
        new(StatusCodes.Status503ServiceUnavailable, "SB_NOT_CONFIGURED", options.NotConfiguredMessage);

    private string RequireConnectionString() => string.IsNullOrWhiteSpace(_options.ConnectionString) ? throw NotConfigured(_options) : _options.ConnectionString;

    private string Name(RegisteredEntity e) => _options.EntityName(e);

    private ServiceBusAdministrationClient? CreateAdmin()
    {
        var cs = _options.AdminConnectionString;
        if (string.IsNullOrWhiteSpace(cs) && _options.AuthMode == SbAuthMode.Credential)
        {
            var o = new ServiceBusAdministrationClientOptions();
            o.Retry.MaxRetries = 0;
            o.Retry.NetworkTimeout = TimeSpan.FromSeconds(5);
            return new ServiceBusAdministrationClient(_options.NormalizedNamespace, Credential, o);
        }
        if (string.IsNullOrWhiteSpace(cs) && _options.IsEmulator)
            cs = System.Text.RegularExpressions.Regex.Replace(RequireConnectionString(), @"Endpoint=sb://([^;/:]+)/?;",
                $"Endpoint=sb://$1:{_options.EmulatorAdminPort};", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        cs ??= RequireConnectionString();
        try
        {
            var options = new ServiceBusAdministrationClientOptions();
            options.Retry.MaxRetries = 0;
            options.Retry.NetworkTimeout = TimeSpan.FromSeconds(5);
            return new ServiceBusAdministrationClient(cs, options);
        }
        catch (Exception ex)
        {
            _log.LogWarning("Service Bus admin client unavailable, using peek counts: {Message}", ex.Message);
            return null;
        }
    }

    // ---------- depth ----------

    /// <summary>Depth of one entity: bounded, shared for a few seconds, unknown (null) on timeout or a missing entity.</summary>
    /// <remarks>
    /// Source: admin | peek | n/a (topic: depths live on subscriptions / forward queues) | missing (MessagingEntityNotFound on
    /// this namespace, ADDENDA-009; remembered for MissingEntityCacheSeconds) | none (blobContainer: never a Service Bus call).
    /// </remarks>
    public Task<EntityCounts> GetCountsAsync(RegisteredEntity entity, ProjectRegistration registration, CancellationToken ct) =>
        !entity.IsServiceBus ? Task.FromResult(new EntityCounts(null, null, "none"))
        : entity.Kind == "topic" ? _counts.CountsAsync($"{registration.Id}|{entity.Id}", c => TopicStateAsync(entity, registration, c), ct)
        : _counts.CountsAsync($"{registration.Id}|{entity.Id}", c => ReadCountsAsync(entity, registration, c), ct);

    /// <summary>Topics have no depth; with an admin client a budgeted existence check tells "missing" apart. Any failure = n/a.</summary>
    private async Task<EntityCounts> TopicStateAsync(RegisteredEntity entity, ProjectRegistration registration, CancellationToken ct)
    {
        var key = CountKey(entity, registration, SubQueue.None);
        if (_counts.IsKnownMissing(key)) return new(null, null, "missing");
        var admin = _options.AdminCountsEnabled && DateTime.UtcNow >= _adminUnavailableUntil ? _admin.Value : null;
        if (admin is null) return new(null, null, "n/a");
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(Math.Max(0.1, _options.CountTimeoutSeconds)));
        try
        {
            if ((await admin.TopicExistsAsync(Name(entity), budget.Token).WaitAsync(budget.Token)).Value) return new(null, null, "n/a");
            _counts.MarkMissing(key, new InvalidOperationException($"topic {Name(entity)} does not exist"));
            return new(null, null, "missing");
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _log.LogDebug("Topic existence check for {Entity} failed: {Message}", entity.Id, FirstLine(ex.Message));
            return new(null, null, "n/a");
        }
    }

    /// <summary>Depths for many entities in parallel (<see cref="ServiceBusOptions.CountParallelism"/>), in input order.</summary>
    public Task<IReadOnlyList<EntityCounts>> GetCountsAsync(IReadOnlyList<RegisteredEntity> entities, ProjectRegistration registration, CancellationToken ct) =>
        SbCountReader.ForAllAsync(entities, _options.CountParallelism, (e, c) => GetCountsAsync(e, registration, c), ct);

    private async Task<EntityCounts> ReadCountsAsync(RegisteredEntity entity, ProjectRegistration registration, CancellationToken ct)
    {
        var activeKey = CountKey(entity, registration, SubQueue.None);
        var deadKey = CountKey(entity, registration, SubQueue.DeadLetter);
        if (_counts.IsKnownMissing(activeKey)) return new(null, null, "missing");
        var admin = _options.AdminCountsEnabled && DateTime.UtcNow >= _adminUnavailableUntil ? _admin.Value : null;
        if (admin is not null)
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
            budget.CancelAfter(TimeSpan.FromSeconds(Math.Max(0.1, _options.CountTimeoutSeconds)));
            try
            {
                if (entity.Kind == "queue")
                {
                    var p = (await admin.GetQueueRuntimePropertiesAsync(Name(entity), budget.Token).WaitAsync(budget.Token)).Value;
                    return new(p.ActiveMessageCount, p.DeadLetterMessageCount, "admin");
                }
                var (topic, sub) = SubscriptionPath(entity, registration);
                var s = (await admin.GetSubscriptionRuntimePropertiesAsync(topic, sub, budget.Token).WaitAsync(budget.Token)).Value;
                return new(s.ActiveMessageCount, s.DeadLetterMessageCount, "admin");
            }
            catch (Exception ex) when (SbCountReader.IsNotFound(ex))
            {
                // One missing entity must not switch every other entity to (slower) peek counts.
                _counts.MarkMissing(activeKey, ex);
                _counts.MarkMissing(deadKey, ex);
                return new(null, null, "missing");
            }
            catch (Exception ex) when (IsAuthFailure(ex) && !IsAdminOnlyDenial(ex))
            {
                throw AuthFailed(ex);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                _adminUnavailableUntil = DateTime.UtcNow + AdminRetryAfter;
                _log.LogWarning("Admin runtime properties failed for {Entity}, falling back to peek counts: {Message}", entity.Id, FirstLine(ex.Message));
            }
        }
        var active = _counts.CountAsync(activeKey, c => PeekCountAsync(entity, registration, SubQueue.None, c), ct);
        var dead = _counts.CountAsync(deadKey, c => PeekCountAsync(entity, registration, SubQueue.DeadLetter, c), ct);
        var (a, d) = (await active, await dead);
        return _counts.IsKnownMissing(activeKey) ? new(null, null, "missing") : new(a, d, "peek");
    }

    /// <summary>Negative-cache key: entity path on the namespace (subscriptions include their topic, so equal sub names never collide).</summary>
    private string CountKey(RegisteredEntity entity, ProjectRegistration registration, SubQueue subQueue) =>
        (entity.Kind == "subscription" && SubscriptionPathOrNull(entity, registration) is { } sp ? $"{sp.Topic}/Subscriptions/{sp.Subscription}" : Name(entity))
        + "|" + subQueue;

    private (string Topic, string Subscription)? SubscriptionPathOrNull(RegisteredEntity entity, ProjectRegistration registration) =>
        entity.Topic is { } t && registration.FindEntity(t) is { } topic ? (Name(topic), Name(entity)) : null;

    /// <summary>
    /// Admin (management) calls need Manage rights; a data-plane-only role (Data Sender/Receiver) is denied there but can still
    /// peek. Such a denial falls back to peek counts instead of failing as SB_AUTH_FAILED (the peek path reports real auth failures).
    /// </summary>
    private static bool IsAdminOnlyDenial(Exception ex) => ex is Azure.RequestFailedException { Status: 401 or 403 };

    private async Task<long?> PeekCountAsync(RegisteredEntity entity, ProjectRegistration registration, SubQueue subQueue, CancellationToken ct)
    {
        try
        {
            await using var receiver = CreateReceiver(entity, registration, subQueue, ServiceBusReceiveMode.PeekLock);
            long count = 0;
            long? from = null;
            while (count < _options.PeekCountCap)
            {
                var batch = await receiver.PeekMessagesAsync((int)Math.Min(100, _options.PeekCountCap - count), from, ct);
                if (batch.Count == 0) break;
                count += batch.Count;
                from = batch[^1].SequenceNumber + 1;
            }
            return count;
        }
        catch (Exception ex) when (IsAuthFailure(ex)) { throw AuthFailed(ex); }
        catch (Exception ex) when (IsUnreachable(ex)) { throw Unreachable(ex); }
        catch (InvalidOperationException ex) when (IsAutoForwarding(ex))
        {
            return null; // auto-forwarding entities cannot be browsed; depth lives on the forward queue
        }
        catch (Exception ex) when (SbCountReader.IsNotFound(ex) || (ex is OperationCanceledException && ct.IsCancellationRequested))
        {
            throw; // SbCountReader: negative cache / budget
        }
        catch (Exception ex)
        {
            _log.LogWarning("Peek count failed for {Entity} ({SubQueue}): {Message}", entity.Id, subQueue, FirstLine(ex.Message));
            return null;
        }
    }

    /// <summary>Cheap reachability probe: peek one message from a queue.</summary>
    public async Task<bool> IsReachableAsync(RegisteredEntity probe, ProjectRegistration registration, CancellationToken ct)
    {
        if (DateTime.UtcNow.Ticks - Interlocked.Read(ref _probeOkTicks) < TimeSpan.FromSeconds(Math.Max(0, _options.ProbeCacheSeconds)).Ticks)
            return true;
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var seconds = _warm ? _options.ProbeTimeoutSeconds : Math.Max(_options.ProbeTimeoutSeconds, _options.ColdProbeTimeoutSeconds);
        budget.CancelAfter(TimeSpan.FromSeconds(Math.Max(0.1, seconds)));
        async Task Peek(CancellationToken c)
        {
            await using var receiver = CreateReceiver(probe, registration, SubQueue.None, ServiceBusReceiveMode.PeekLock);
            await receiver.PeekMessageAsync(cancellationToken: c);
        }
        try
        {
            await Peek(budget.Token).WaitAsync(budget.Token); // link close happens in the background after a timeout
            _warm = true;
            Interlocked.Exchange(ref _probeOkTicks, DateTime.UtcNow.Ticks);
            return true;
        }
        catch (Exception ex) when (SbCountReader.IsNotFound(ex))
        {
            _warm = true;
            Interlocked.Exchange(ref _probeOkTicks, DateTime.UtcNow.Ticks);
            return true; // the namespace answered (token accepted, entity lookup done); only this entity is missing
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _log.LogWarning("Service Bus probe exceeded {Seconds}s; treated as unreachable.", seconds);
            return false;
        }
        catch (Exception ex) when (IsAuthFailure(ex))
        {
            _log.LogWarning("Service Bus probe auth failed: {Message}", FirstLine(ex.Message));
            throw AuthFailed(ex);
        }
        catch (Exception ex)
        {
            _log.LogWarning("Service Bus probe failed: {Message}", FirstLine(ex.Message));
            return false;
        }
    }

    // ---------- peek ----------

    public async Task<IReadOnlyList<PeekedMessage>> PeekAsync(RegisteredEntity entity, ProjectRegistration registration, int max, SubQueue subQueue, CancellationToken ct) =>
        (await PeekRawAsync(entity, registration, max, subQueue, ct)).Select(ToPeeked).ToList();

    /// <summary>DLQ list (ADR-005): peek on <see cref="SubQueue.DeadLetter"/>, with the broker's dead-letter reason/description/source.</summary>
    public async Task<IReadOnlyList<DlqMessage>> PeekDeadLettersAsync(RegisteredEntity entity, ProjectRegistration registration, int max, CancellationToken ct) =>
        (await PeekRawAsync(entity, registration, max, SubQueue.DeadLetter, ct)).Select(ToDlq).ToList();

    private async Task<IReadOnlyList<ServiceBusReceivedMessage>> PeekRawAsync(RegisteredEntity entity, ProjectRegistration registration, int max, SubQueue subQueue, CancellationToken ct)
    {
        if (entity.Kind == "topic")
            throw new ApiException(StatusCodes.Status400BadRequest, "ENTITY_NOT_PEEKABLE", "Topics hold no messages; peek a subscription or its forward queue.");
        try
        {
            await using var receiver = CreateReceiver(entity, registration, subQueue, ServiceBusReceiveMode.PeekLock);
            return await receiver.PeekMessagesAsync(max, cancellationToken: ct);
        }
        catch (Exception ex) when (IsAuthFailure(ex)) { throw AuthFailed(ex); }
        catch (Exception ex) when (IsUnreachable(ex)) { throw Unreachable(ex); }
        catch (InvalidOperationException ex) when (IsAutoForwarding(ex))
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "ENTITY_NOT_PEEKABLE",
                $"{entity.Id} auto-forwards{(entity.ForwardTo is null ? "" : $" to {entity.ForwardTo}")}; peek that entity instead (the emulator cannot browse forwarding entities).");
        }
        catch (ServiceBusException ex) when (ex.Reason == ServiceBusFailureReason.MessagingEntityNotFound)
        {
            throw new ApiException(StatusCodes.Status404NotFound, "ENTITY_NOT_FOUND", $"{Name(entity)} not found on the Service Bus namespace: {FirstLine(ex.Message)}");
        }
    }

    public static PeekedMessage ToPeeked(ServiceBusReceivedMessage m)
    {
        var bytes = m.Body.ToMemory();
        var truncated = bytes.Length > MaxBodyBytes;
        object? body;
        if (truncated)
            body = Encoding.UTF8.GetString(bytes.Span[..MaxBodyBytes]);
        else
        {
            try { body = JsonDocument.Parse(bytes).RootElement.Clone(); }
            catch (JsonException)
            {
                try { body = new UTF8Encoding(false, true).GetString(bytes.Span); }
                catch (DecoderFallbackException) { body = "base64:" + Convert.ToBase64String(bytes.Span); }
            }
        }
        return new PeekedMessage(m.SequenceNumber, m.MessageId ?? "", m.Subject, m.ContentType, m.CorrelationId,
            m.EnqueuedTime.UtcDateTime.ToString("O"), m.DeliveryCount,
            m.ApplicationProperties.ToDictionary(p => p.Key, p => p.Value), body, truncated);
    }

    public static DlqMessage ToDlq(ServiceBusReceivedMessage m)
    {
        var p = ToPeeked(m);
        return new DlqMessage(p.SequenceNumber, p.MessageId, p.Subject, p.ContentType, p.CorrelationId, p.EnqueuedTime, p.DeliveryCount,
            p.ApplicationProperties, p.Body, p.BodyTruncated, m.DeadLetterReason, m.DeadLetterErrorDescription, m.DeadLetterSource);
    }

    // ---------- DLQ retry / discard ----------

    public const string RetryOfProperty = "x-overview-retry-of";
    /// <summary>Set by the broker on dead-lettering; they describe the DLQ entry, not the message, so a retry drops them.</summary>
    private static readonly string[] DeadLetterProperties = ["DeadLetterReason", "DeadLetterErrorDescription"];
    private const int RetryAllCap = 5000;
    private static readonly TimeSpan DlqReceiveWait = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Retry (<paramref name="resendTo"/> set) or discard (null) DLQ messages by sequence number, ADR-005. Service Bus has no
    /// receive-by-sequence for dead letters, so this PeekLock-receives the DLQ and acts on the selected messages: retry sends
    /// <see cref="CloneForRetry"/> copies to <paramref name="resendTo"/> in batches and then Completes the originals; discard
    /// only Completes. Everything else stays locked until the end (so the loop keeps getting new messages) and is then
    /// abandoned, which leaves it in the DLQ. "all" = the sequence numbers peeked at the start (cap 5000), so messages
    /// dead-lettered again during the run are not picked up twice.
    /// </summary>
    public async Task<DlqSettleOutcome> SettleDeadLettersAsync(RegisteredEntity entity, ProjectRegistration registration, DlqSelection selection,
        RegisteredEntity? resendTo, CancellationToken ct)
    {
        PurgePolicy.EnsureWritable(_options, resendTo is null ? "DLQ discard" : "DLQ retry");
        if (entity.Kind == "topic")
            throw new ApiException(StatusCodes.Status400BadRequest, "ENTITY_HAS_NO_DLQ", "Topics have no dead-letter queue; use a subscription.");

        var pending = selection.All
            ? (await PeekRawAsync(entity, registration, RetryAllCap, SubQueue.DeadLetter, ct)).Select(m => m.SequenceNumber).ToHashSet()
            : selection.SequenceNumbers.ToHashSet();
        var requested = pending.ToList();
        var settled = new List<long>();
        if (pending.Count == 0) return new([], []);

        try
        {
            await using var receiver = CreateReceiver(entity, registration, SubQueue.DeadLetter, ServiceBusReceiveMode.PeekLock);
            var sender = resendTo is null ? null : Sender(Name(resendTo));
            var locked = new HashSet<ServiceBusReceivedMessage>();
            try
            {
                while (pending.Count > 0 && locked.Count < RetryAllCap)
                {
                    var received = await receiver.ReceiveMessagesAsync(100, DlqReceiveWait, ct);
                    if (received.Count == 0) break;
                    locked.UnionWith(received);
                    var selected = new Queue<ServiceBusReceivedMessage>(received.Where(m => pending.Contains(m.SequenceNumber)));
                    while (selected.Count > 0)
                    {
                        var chunk = new List<ServiceBusReceivedMessage>();
                        if (sender is null) while (selected.Count > 0) chunk.Add(selected.Dequeue());
                        else
                        {
                            using var batch = await sender.CreateMessageBatchAsync(ct);
                            while (selected.TryPeek(out var next) && batch.TryAddMessage(CloneForRetry(next))) chunk.Add(selected.Dequeue());
                            if (chunk.Count == 0)
                                throw new ApiException(StatusCodes.Status422UnprocessableEntity, "MESSAGE_TOO_LARGE",
                                    $"DLQ message {selected.Peek().SequenceNumber} is too large to resend; nothing was completed for it.");
                            await sender.SendMessagesAsync(batch, ct);
                        }
                        foreach (var m in chunk)
                        {
                            await receiver.CompleteMessageAsync(m, ct);
                            locked.Remove(m);
                            pending.Remove(m.SequenceNumber);
                            settled.Add(m.SequenceNumber);
                        }
                    }
                }
            }
            finally
            {
                foreach (var m in locked)
                {
                    try { await receiver.AbandonMessageAsync(m, cancellationToken: CancellationToken.None); }
                    catch (Exception ex) { _log.LogWarning("Abandon of DLQ message {Seq} on {Entity} failed (its lock expires on its own): {Message}", m.SequenceNumber, entity.Id, FirstLine(ex.Message)); }
                }
            }
        }
        catch (Exception ex) when (IsAuthFailure(ex)) { throw AuthFailed(ex); }
        catch (Exception ex) when (IsUnreachable(ex)) { throw Unreachable(ex); }
        catch (ServiceBusException ex) when (ex.Reason == ServiceBusFailureReason.MessagingEntityNotFound)
        {
            throw new ApiException(StatusCodes.Status404NotFound, "ENTITY_NOT_FOUND", $"{Name(entity)} not found on the Service Bus namespace: {FirstLine(ex.Message)}");
        }
        _log.LogInformation("DLQ {Action} on {Entity}: {Settled} settled, {NotFound} not found", resendTo is null ? "discard" : $"retry to {resendTo.Id}",
            entity.Id, settled.Count, pending.Count);
        return new(settled, requested.Where(pending.Contains).ToList());
    }

    /// <summary>
    /// The resend copy of a dead-lettered message: body, ApplicationProperties (minus the broker's DeadLetterReason /
    /// DeadLetterErrorDescription), Subject, ContentType, CorrelationId, plus <c>x-overview-retry-of=&lt;DLQ sequence number&gt;</c>.
    /// New MessageId and enqueue time; delivery count starts at 0.
    /// </summary>
    public static ServiceBusMessage CloneForRetry(ServiceBusReceivedMessage m)
    {
        var clone = new ServiceBusMessage(m.Body) { Subject = m.Subject, ContentType = m.ContentType, CorrelationId = m.CorrelationId };
        foreach (var (key, value) in m.ApplicationProperties)
            if (!DeadLetterProperties.Contains(key)) clone.ApplicationProperties[key] = value;
        clone.ApplicationProperties[RetryOfProperty] = m.SequenceNumber;
        return clone;
    }

    // ---------- send ----------

    public async Task<IReadOnlyList<SentToEntity>> SendAsync(IReadOnlyList<BuiltMessage> messages, ProjectRegistration registration, CancellationToken ct)
    {
        PurgePolicy.EnsureWritable(_options);
        var targets = messages.Select(m => m.TargetEntityId).Distinct().ToDictionary(id => id, id => ResolveSendTarget(id, registration));
        var sent = new Dictionary<string, int>();
        try
        {
            // One by one, in catalog order, like the kit (the rename/stale/duplicate sequence depends on order).
            foreach (var m in messages)
            {
                var name = Name(targets[m.TargetEntityId]);
                await Sender(name).SendMessageAsync(ToServiceBusMessage(m), ct);
                sent[m.TargetEntityId] = sent.GetValueOrDefault(m.TargetEntityId) + 1;
            }
        }
        catch (Exception ex) when (IsAuthFailure(ex)) { throw AuthFailed(ex); }
        catch (Exception ex) when (IsUnreachable(ex)) { throw Unreachable(ex); }
        return sent.Select(kv => new SentToEntity(kv.Key, Name(targets[kv.Key]), kv.Value)).ToList();
    }

    public static ServiceBusMessage ToServiceBusMessage(BuiltMessage m)
    {
        var message = new ServiceBusMessage(BinaryData.FromBytes(m.Body)) { Subject = m.Subject };
        if (m.ContentType is not null) message.ContentType = m.ContentType;
        foreach (var (key, value) in m.ApplicationProperties)
            message.ApplicationProperties[key] = value;
        return message;
    }

    private static RegisteredEntity ResolveSendTarget(string entityId, ProjectRegistration registration)
    {
        var entity = registration.FindEntity(entityId)
            ?? throw new ApiException(StatusCodes.Status400BadRequest, "UNKNOWN_ENTITY", $"{entityId} is not in registration {registration.Id}.");
        if (entity.Kind == "subscription")
            throw new ApiException(StatusCodes.Status400BadRequest, "ENTITY_NOT_SENDABLE", $"{entityId} is a subscription; send to its topic.");
        if (!entity.ExistsIn(SbScope.Local))
            throw new ApiException(StatusCodes.Status400BadRequest, "ENTITY_NOT_LOCAL", $"{entityId} is scope '{entity.Scope}', not on the local emulator.");
        return entity;
    }

    // ---------- purge ----------

    public async Task<PurgeResult> PurgeAsync(IEnumerable<string> entityIds, ProjectRegistration registration, CancellationToken ct)
    {
        PurgePolicy.EnsureAllowed(_options);
        var results = new List<PurgedEntity>();
        try
        {
            foreach (var id in entityIds)
            {
                var entity = registration.FindEntity(id) ?? throw new ApiException(StatusCodes.Status500InternalServerError, "UNKNOWN_ENTITY", $"Purge list entry {id} is not in the registration.");
                foreach (var subQueue in new[] { SubQueue.None, SubQueue.DeadLetter })
                {
                    await using var receiver = CreateReceiver(entity, registration, subQueue, ServiceBusReceiveMode.ReceiveAndDelete);
                    var removed = 0;
                    if (await receiver.PeekMessageAsync(cancellationToken: ct) is not null)
                        while (true)
                        {
                            var batch = await receiver.ReceiveMessagesAsync(100, TimeSpan.FromSeconds(2), ct);
                            if (batch.Count == 0) break;
                            removed += batch.Count;
                        }
                    results.Add(new PurgedEntity(id, Name(entity), subQueue == SubQueue.DeadLetter, removed));
                }
            }
        }
        catch (Exception ex) when (IsAuthFailure(ex)) { throw AuthFailed(ex); }
        catch (Exception ex) when (IsUnreachable(ex)) { throw Unreachable(ex); }
        return new PurgeResult(results.Sum(r => r.Removed), results);
    }

    // ---------- helpers ----------

    private ServiceBusSender Sender(string entityName) => _senders.GetOrAdd(entityName, n => _client.Value.CreateSender(n));

    private ServiceBusReceiver CreateReceiver(RegisteredEntity entity, ProjectRegistration registration, SubQueue subQueue, ServiceBusReceiveMode mode)
    {
        var options = new ServiceBusReceiverOptions { SubQueue = subQueue, ReceiveMode = mode };
        if (entity.Kind == "subscription")
        {
            var (topic, sub) = SubscriptionPath(entity, registration);
            return _client.Value.CreateReceiver(topic, sub, options);
        }
        return _client.Value.CreateReceiver(Name(entity), options);
    }

    private (string Topic, string Subscription) SubscriptionPath(RegisteredEntity entity, ProjectRegistration registration)
    {
        var topic = entity.Topic is null ? null : registration.FindEntity(entity.Topic);
        return (topic is null ? throw new InvalidDataException($"Subscription {entity.Id} has no resolvable topic") : Name(topic), Name(entity));
    }

    private static bool IsAutoForwarding(Exception ex) => ex.Message.Contains("auto-forwarding", StringComparison.OrdinalIgnoreCase);

    private static bool IsUnreachable(Exception ex) => ex switch
    {
        ServiceBusException sb => sb.Reason is ServiceBusFailureReason.ServiceCommunicationProblem or ServiceBusFailureReason.ServiceTimeout or ServiceBusFailureReason.ServiceBusy,
        SocketException or TimeoutException => true,
        UnauthorizedAccessException => false,
        _ => ex.InnerException is not null && IsUnreachable(ex.InnerException),
    };

    /// <summary>Credential / authorization failures (AAD token, 401/403, missing RBAC): SB_AUTH_FAILED, distinct from SB_UNREACHABLE.</summary>
    public static bool IsAuthFailure(Exception ex) => ex switch
    {
        Azure.Identity.AuthenticationFailedException or Azure.Identity.CredentialUnavailableException or UnauthorizedAccessException => true,
        Azure.RequestFailedException { Status: 401 or 403 } => true,
        ServiceBusException sb when sb.Message.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase)
            || sb.Message.Contains("401", StringComparison.Ordinal) || sb.Message.Contains("403", StringComparison.Ordinal) => true,
        _ => ex.InnerException is not null && IsAuthFailure(ex.InnerException),
    };

    public ApiException AuthFailed(Exception ex) => new(StatusCodes.Status503ServiceUnavailable, "SB_AUTH_FAILED",
        $"{_options.AuthFailedMessage} ({FirstLine(ex.Message)})", retryable: false);

    private ApiException Unreachable(Exception ex) => new(StatusCodes.Status503ServiceUnavailable, "SB_UNREACHABLE",
        _options.HostedOnRender ? $"{_options.UnreachableMessage} ({FirstLine(ex.Message)})" : $"Service Bus unreachable: {FirstLine(ex.Message)}", retryable: true);

    private static string FirstLine(string s) => s.Split('\n')[0].Trim();

    public async ValueTask DisposeAsync()
    {
        foreach (var s in _senders.Values) await s.DisposeAsync();
        if (_client.IsValueCreated) await _client.Value.DisposeAsync();
    }
}
