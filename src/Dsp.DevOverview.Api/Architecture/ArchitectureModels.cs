using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dsp.DevOverview.Api.Architecture;

// ADDENDA-011 documents (architecture-layer.schema.json). Deserialized only after the schema accepted them, so the shape is
// guaranteed; these typed views carry just what the cross-document checks (V-002..V-008, V-011) need.

public sealed record ArchitectureCatalogDoc(string Project, string RegistrationId, IReadOnlyList<ArchitectureCatalogEntryDoc> Layers);

public sealed record ArchitectureCatalogEntryDoc(string Id, string Path);

public sealed record ArchitectureLayerDoc(
    string Id, string Project, string Title, string? ParentLayerId, ArchitectureFlowRef? FlowRef, string Scope,
    IReadOnlyList<ArchitectureSourceDoc> Sources, IReadOnlyList<ArchitectureNodeDoc> Nodes, IReadOnlyList<ArchitectureEdgeDoc> Edges);

public sealed record ArchitectureNodeDoc(
    string Id, string Kind, string Boundary, string? OpensLayerId, string? FlowEntityId, IReadOnlyList<ArchitectureSourceDoc> Sources);

public sealed record ArchitectureEdgeDoc(string Id, string From, string To, string Kind, IReadOnlyList<ArchitectureSourceDoc> Sources);

public sealed record ArchitectureSourceDoc(string Kind, string Ref, string Confidence, string? Note);

/// <summary>
/// Root anchor in Flow (ADDENDA-011.1): one handler item and the container it sits in. Both are required and always serialized;
/// container-only roots are rejected (V-011), so a valid catalog never has a blank <see cref="HandlerId"/>.
/// </summary>
public sealed record ArchitectureFlowRef(string ContainerId, string HandlerId);

public sealed record ArchitectureCrumb(string LayerId, string Title);

public sealed record ArchitectureChild(string LayerId, string Title, string OpenedByNodeId);

/// <summary>One validated layer with its derived navigation. <see cref="Document"/> is the layer JSON minus <c>$schema</c>.</summary>
public sealed record ArchitectureLayer(
    string Id, string Title, string? ParentLayerId, JsonElement Document, ArchitectureFlowRef RootFlowRef,
    IReadOnlyList<ArchitectureCrumb> Breadcrumbs, IReadOnlyList<ArchitectureChild> Children);

public sealed record ArchitectureRoot(string LayerId, string Title, ArchitectureFlowRef FlowRef);

/// <summary>
/// Immutable, fully validated catalog snapshot for one registration. <see cref="CatalogHash"/> is <c>sha256:</c> + lowercase hex over
/// <c>UTF8(basename) + "\0" + bytes</c> of index.json then each catalog entry, in catalog order (no machine paths).
/// </summary>
public sealed record ArchitectureCatalog(
    string RegistrationId, IReadOnlyList<ArchitectureRoot> Roots, IReadOnlyDictionary<string, ArchitectureLayer> Layers, string CatalogHash)
{
    /// <summary>Flow handler id → root layer id. The only Flow drill links: containers are never Explore targets (ADDENDA-011.1).</summary>
    public IReadOnlyDictionary<string, string> HandlerLinks { get; } =
        Roots.ToDictionary(r => r.FlowRef.HandlerId, r => r.LayerId, StringComparer.Ordinal);
}

public enum ArchitectureStatus { Absent, Valid, Invalid }

/// <summary>Load outcome. <see cref="Issues"/> are V-* rule texts with layer ids / catalog basenames only, never machine paths.</summary>
public sealed record ArchitectureState(ArchitectureStatus Status, ArchitectureCatalog? Catalog, IReadOnlyList<string> Issues)
{
    public const string InvalidCode = "ARCHITECTURE_INVALID";
    private const int ShownIssues = 3;

    public static readonly ArchitectureState Absent = new(ArchitectureStatus.Absent, null, []);

    public static ArchitectureState Invalid(IReadOnlyList<string> issues) => new(ArchitectureStatus.Invalid, null, issues);

    public string? InvalidMessage => Status != ArchitectureStatus.Invalid ? null
        : "Architecture catalog rejected; drill-down disabled, Flow unaffected. "
          + string.Join("; ", Issues.Take(ShownIssues))
          + (Issues.Count > ShownIssues ? $" (+{Issues.Count - ShownIssues} more)" : "");

    public IReadOnlyList<string> Warnings => InvalidMessage is { } m ? [$"{InvalidCode}: {m}"] : [];
}

// Wire DTOs (NOTE-ADDENDA-011-api-contract.md).

public sealed record ArchitectureIndexResponse(
    string ProjectId, int SchemaVersion, IReadOnlyList<ArchitectureRoot> Roots, IReadOnlyList<string> Warnings,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? CatalogHash = null);

public sealed record ArchitectureLayerResponse(
    string ProjectId, int SchemaVersion, JsonElement Layer, ArchitectureFlowRef RootFlowRef,
    IReadOnlyList<ArchitectureCrumb> Breadcrumbs, IReadOnlyList<ArchitectureChild> Children, IReadOnlyList<string> Warnings);
