using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Messaging.ServiceBus;
using Dsp.DevOverview.Api.AppConsole;
using Dsp.DevOverview.Api.AppInsights;
using Dsp.DevOverview.Api.Health;
using Dsp.DevOverview.Api.Registration;
using Dsp.DevOverview.Api.ServiceBus;
using Dsp.DevOverview.Catalog;

namespace Dsp.DevOverview.Api;

/// <summary>Endpoint logic: maps registration + catalog + live Service Bus state onto the UI contracts.</summary>
public sealed class OverviewService(ProjectStore store, SbGateway sb, OverviewOptions options, IAiQueryProbe? aiProbe = null,
    ProjectHealthService? health = null)
{
    private readonly ProjectHealthService _health = health
        ?? new ProjectHealthService(store, new CachedSbHealthProbe(sb, options), aiProbe, console: null, new TcpPortProbe(), options);

    public string CommandPropertyProfile(OverviewProject p) =>
        options.CommandPropertyProfile ?? p.Catalog?.Commands.DefaultPropertyProfile ?? "kit";

    public OverviewProject Project(string id) => store.Find(id)
        ?? throw new ApiException(StatusCodes.Status404NotFound, "PROJECT_NOT_FOUND", $"No registration with id '{id}'. Known: {string.Join(", ", store.All.Select(p => p.Id))}");

    private static SampleCatalog Catalog(OverviewProject p) => p.Catalog
        ?? throw new ApiException(StatusCodes.Status404NotFound, "CATALOG_MISSING", p.CatalogError ?? "No sample catalog.");

    private SbScope Scope => options.ServiceBus.Scope;

    public IEnumerable<RegisteredEntity> ActiveEntities(OverviewProject p) => ActiveEntities(p, Scope);

    public static IEnumerable<RegisteredEntity> ActiveEntities(OverviewProject p, SbScope scope) =>
        p.Registration.Entities.Where(e => e.ExistsIn(scope) && e.LocalName is { Length: > 0 } and not "n/a");

    public RegisteredEntity Entity(OverviewProject p, string entityId)
    {
        var id = Uri.UnescapeDataString(entityId);
        var entity = p.Registration.FindEntity(id)
            ?? throw new ApiException(StatusCodes.Status404NotFound, "UNKNOWN_ENTITY", $"{id} is not in registration {p.Id}.");
        if (!entity.ExistsIn(Scope))
            throw new ApiException(StatusCodes.Status404NotFound, "ENTITY_NOT_IN_SCOPE", $"{id} is scope '{entity.Scope}', not available in scope '{Scope.ToString().ToLowerInvariant()}'.");
        return entity;
    }

    // GET /api/projects (slice 7: live health aggregation in ProjectHealthService)
    public Task<IReadOnlyList<ProjectSummary>> ProjectsAsync(CancellationToken ct) => _health.SummariesAsync(ct);

    // GET /api/projects/{id}/sb/entities
    public async Task<IReadOnlyList<EntityStatus>> EntitiesAsync(string id, CancellationToken ct)
    {
        var p = Project(id);
        var entities = ActiveEntities(p).ToList();
        var probe = entities.FirstOrDefault(e => e.Kind == "queue");
        if (probe is null || !await sb.IsReachableAsync(probe, p.Registration, ct))
            throw new ApiException(StatusCodes.Status503ServiceUnavailable, "SB_UNREACHABLE", "Service Bus emulator unreachable (is the shared stack up? pwsh /workspace/dsp/shared-stack/Test-DspStack.ps1).", retryable: true);

        var result = new List<EntityStatus>();
        foreach (var e in entities)
        {
            var c = await sb.GetCountsAsync(e, p.Registration, ct);
            result.Add(new EntityStatus(e.Id, e.Kind, e.LocalName, c.Active, c.DeadLetter,
                DlqHot: c.DeadLetter is > 0, Owner: e.Owner ?? "project", ParentEntityId: e.Kind == "subscription" ? e.Topic : null));
        }
        return result;
    }

    // GET /api/projects/{id}/sb/{entityId}/peek?max=20
    public async Task<QueuePeek> PeekAsync(string id, string entityId, int max, bool deadLetter, CancellationToken ct)
    {
        var p = Project(id);
        var e = Entity(p, entityId);
        if (max is < 1 or > 250) throw new ApiException(StatusCodes.Status400BadRequest, "INVALID_MAX", "max must be 1..250.");
        var messages = await sb.PeekAsync(e, p.Registration, max, deadLetter ? SubQueue.DeadLetter : SubQueue.None, ct);
        var counts = await sb.GetCountsAsync(e, p.Registration, ct);
        return new QueuePeek(e.Id, counts.Active, counts.DeadLetter, Scope.ToString().ToLowerInvariant(), e.MaxDeliveryCountFor(Scope), messages);
    }

    // GET /api/projects/{id}/sb/{entityId}/dlq?max=50
    public async Task<IReadOnlyList<DlqMessage>> DeadLettersAsync(string id, string entityId, int max, CancellationToken ct)
    {
        var p = Project(id);
        var e = Entity(p, entityId);
        if (max is < 1 or > 250) throw new ApiException(StatusCodes.Status400BadRequest, "INVALID_MAX", "max must be 1..250.");
        return await sb.PeekDeadLettersAsync(e, p.Registration, max, ct);
    }

    // POST /api/projects/{id}/sb/{entityId}/dlq/retry  {sequenceNumbers:[...]|"all"}
    public async Task<DlqRetryResult> RetryDeadLettersAsync(string id, string entityId, DlqActionBody body, CancellationToken ct)
    {
        var p = Project(id);
        var e = Entity(p, entityId);
        PurgePolicy.EnsureWritable(options.ServiceBus, "DLQ retry");
        var target = RetryTarget(p, e);
        var selection = DlqSelection.Parse(body.SequenceNumbers, allowAll: true);
        var outcome = await sb.SettleDeadLettersAsync(e, p.Registration, selection, target, ct);
        return new DlqRetryResult(outcome.Settled.Count, e.Id, target.Id, outcome.Settled, outcome.NotFound);
    }

    // POST /api/projects/{id}/sb/{entityId}/dlq/discard  {sequenceNumbers:[...]}
    public async Task<DlqDiscardResult> DiscardDeadLettersAsync(string id, string entityId, DlqActionBody body, CancellationToken ct)
    {
        var p = Project(id);
        var e = Entity(p, entityId);
        PurgePolicy.EnsureWritable(options.ServiceBus, "DLQ discard");
        var selection = DlqSelection.Parse(body.SequenceNumbers, allowAll: false);
        var outcome = await sb.SettleDeadLettersAsync(e, p.Registration, selection, resendTo: null, ct);
        return new DlqDiscardResult(outcome.Settled.Count, e.Id, outcome.Settled, outcome.NotFound);
    }

    /// <summary>
    /// The "main entity" a DLQ message is resent to: a queue's own DLQ goes back to the queue; a subscription's goes to its
    /// topic (Service Bus can't send to one subscription), so every subscription whose filter matches gets a copy. Locally
    /// the topic has only local-inspect.
    /// </summary>
    public RegisteredEntity RetryTarget(OverviewProject p, RegisteredEntity e) => e.Kind switch
    {
        "queue" => e,
        "subscription" => e.Topic is { } t && p.Registration.FindEntity(t) is { } topic && topic.ExistsIn(Scope) ? topic
            : throw new ApiException(StatusCodes.Status400BadRequest, "NO_RETRY_TARGET", $"{e.Id} has no topic in scope '{Scope.ToString().ToLowerInvariant()}' to resend to."),
        _ => throw new ApiException(StatusCodes.Status400BadRequest, "ENTITY_HAS_NO_DLQ", $"{e.Id} is a {e.Kind}; topics have no dead-letter queue, use a subscription."),
    };

    // GET /api/projects/{id}/samples
    public SampleMessageCatalogDto Samples(string id)
    {
        var p = Project(id);
        var c = Catalog(p);
        var profileName = CommandPropertyProfile(p);
        var profile = c.Commands.PropertyProfiles.GetValueOrDefault(profileName)
            ?? throw new ApiException(StatusCodes.Status500InternalServerError, "UNKNOWN_PROPERTY_PROFILE", $"Command property profile '{profileName}' not in catalog.");

        var messages = c.Messages.OrderBy(m => m.Seq).Select(m =>
        {
            IReadOnlyDictionary<string, object> props;
            IReadOnlyList<string> runtime = [];
            if (m.PresetId == PresetIds.Commands)
            {
                props = profile.Properties.ToDictionary(x => x.Name, x => x.Value is { } v ? SampleMessageBuilder.FromJson(v) : $"<runtime:{x.Runtime}>");
                runtime = profile.Properties.Where(x => x.Runtime is not null).Select(x => x.Name).ToList();
            }
            else props = m.ApplicationProperties.ToDictionary(x => x.Key, x => SampleMessageBuilder.FromJson(x.Value));
            return new SampleMessageDto(m.PresetId, m.Subject, m.TargetEntityId, props, m.Body, m.Seq, m.ContentType, m.ProjectIndex, m.Variant, runtime);
        }).ToList();

        return new SampleMessageCatalogDto(c.ProjectId, c.Source, c.MirroredAt, c.ReferenceUtc, profileName,
            c.SampleProjects.Select(s => new SampleProjectDto(s.Index, s.CustomerId, s.Name, s.OrderNumber, s.Active)).ToList(),
            c.Presets, messages);
    }

    // POST /api/projects/{id}/samples/send
    public async Task<object> SendAsync(string id, SampleSendBody body, CancellationToken ct)
    {
        var p = Project(id);
        var c = Catalog(p);
        if (string.Equals(body.Mode, "purge", StringComparison.OrdinalIgnoreCase))
            return await PurgeAsync(id, ct);

        var request = new SampleSendRequest(body.Mode ?? "",
            body.Event, body.DataVersion, body.ProjectIndex,
            body.BodyOverride is { ValueKind: not JsonValueKind.Undefined and not JsonValueKind.Null } b ? JsonNode.Parse(b.GetRawText()) : null,
            body.ApplicationPropertiesOverride?.ToDictionary(kv => kv.Key, kv => SampleMessageBuilder.FromJson(kv.Value)),
            string.IsNullOrWhiteSpace(body.TargetEntityId) ? null : body.TargetEntityId);

        var profile = CommandPropertyProfile(p);
        IReadOnlyList<BuiltMessage> built;
        try { built = new SampleMessageBuilder(c, commandPropertyProfile: profile).Build(request); }
        catch (SampleRequestException ex) { throw new ApiException(StatusCodes.Status400BadRequest, ex.Code, ex.Message); }

        var sent = await sb.SendAsync(built, p.Registration, ct);
        return new SampleSendResult(sent.Sum(s => s.Sent), sent.Count == 1 ? sent[0].EntityId : null, sent,
            built.Any(m => c.CommandMessages.Any(x => x.Seq == m.CatalogSeq)) ? profile : null);
    }

    // POST /api/projects/{id}/sb/purge
    public async Task<PurgeResult> PurgeAsync(string id, CancellationToken ct)
    {
        PurgePolicy.EnsureAllowed(options.ServiceBus);
        var p = Project(id);
        var c = Catalog(p);
        return await sb.PurgeAsync(c.PurgeEntityIds, p.Registration, ct);
    }
}
