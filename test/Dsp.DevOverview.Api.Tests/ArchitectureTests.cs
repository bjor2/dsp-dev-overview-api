using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dsp.DevOverview.Api.Architecture;
using Dsp.DevOverview.Api.Flow;
using Dsp.DevOverview.Api.Registration;
using Dsp.DevOverview.Api.ServiceBus;
using Xunit.Abstractions;

namespace Dsp.DevOverview.Api.Tests;

/// <summary>Writable copy of a real architecture catalog in a temp layers root (the real files are only read).</summary>
internal sealed class LayerTree : IDisposable
{
    public const string RootLayer = "layer:customerproject.fn.ingress";
    public const string RootHandler = "customerproject.fn.ingress";
    public const string IngressContainer = "container:customerproject.ingress-app";

    public string Project { get; }
    public ProjectRegistration TreeRegistration { get; }
    public string LayersRoot { get; }
    public string ProjectDir => System.IO.Path.Combine(LayersRoot, Project);
    public JsonObject Catalog { get; }
    public List<JsonObject> Layers { get; }

    private static readonly Lazy<ArchitectureSchema> SharedSchema = new(() => new ArchitectureSchema(RepoPaths.ArchitectureSchema));
    private static readonly Lazy<ProjectRegistration> SharedRegistration = new(() => new RegistrationLoader(RepoPaths.Schema).Load(RepoPaths.Registration));
    private static readonly Lazy<ProjectRegistration> SharedPriceRegistration = new(() => new RegistrationLoader(RepoPaths.Schema).Load(RepoPaths.PriceRegistration));
    public static ProjectRegistration Registration => SharedRegistration.Value;
    public static ProjectRegistration PriceRegistration => SharedPriceRegistration.Value;
    public static ArchitectureSchema Schema => SharedSchema.Value;

    private LayerTree(string project, ProjectRegistration registration, string? layersRoot, JsonObject catalog, List<JsonObject> layers) =>
        (Project, TreeRegistration, LayersRoot, Catalog, Layers) =
            (project, registration, layersRoot ?? System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dsp-arch-" + Guid.NewGuid().ToString("N")), catalog, layers);

    private static LayerTree FromRepo(string project, ProjectRegistration registration, string? layersRoot)
    {
        var dir = System.IO.Path.Combine(RepoPaths.ArchitectureLayers, project);
        var catalog = JsonNode.Parse(File.ReadAllText(System.IO.Path.Combine(dir, "index.json")))!.AsObject();
        var layers = catalog["layers"]!.AsArray()
            .Select(e => JsonNode.Parse(File.ReadAllText(System.IO.Path.Combine(dir, e!["path"]!.GetValue<string>())))!.AsObject()).ToList();
        return new LayerTree(project, registration, layersRoot, catalog, layers);
    }

    /// <summary>The full checked-in customerproject catalog (5 handler roots, 6 depth-2 children).</summary>
    public static LayerTree Real(string? layersRoot = null) => FromRepo("customerproject", Registration, layersRoot);

    /// <summary>The full checked-in price catalog (8 handler roots, deepen children down to the depth-5 calculator discount relations), validated against price.dev.</summary>
    public static LayerTree PriceReal(string? layersRoot = null) => FromRepo("price", PriceRegistration, layersRoot);

    /// <summary>
    /// Synthetic forest built from the INGRESS handler root only: it is the sole catalog entry, followed by a chain of children
    /// (depth 2..<paramref name="depth"/>), each opened by its parent's inside import component (node 1).
    /// Layer/Entry indexes are therefore 0 = root, 1 = depth 2, 2 = depth 3, …
    /// </summary>
    public static LayerTree Deep(int depth)
    {
        var tree = Real();
        var root = tree.Layers.Single(l => l["id"]!.GetValue<string>() == RootLayer);
        var rootEntry = tree.Catalog["layers"]!.AsArray().Single(e => e!["id"]!.GetValue<string>() == RootLayer)!.DeepClone();
        tree.Layers.Clear();
        tree.Layers.Add(root);
        tree.Catalog["layers"] = new JsonArray(rootEntry);
        var template = root.DeepClone().AsObject();
        for (var d = 2; d <= depth; d++)
        {
            var suffix = ".test" + d;
            var child = template.DeepClone().AsObject();
            child["id"] = RootLayer + suffix;
            child["title"] = "Depth " + d;
            child["parentLayerId"] = tree.Layers[^1]["id"]!.GetValue<string>();
            child.Remove("flowRef");
            foreach (var n in child["nodes"]!.AsArray()) n!["id"] = n["id"]!.GetValue<string>() + suffix;
            foreach (var e in child["edges"]!.AsArray())
                foreach (var key in new[] { "id", "from", "to" }) e![key] = e[key]!.GetValue<string>() + suffix;
            tree.Layers[^1]["nodes"]![1]!["opensLayerId"] = child["id"]!.GetValue<string>();
            tree.Catalog["layers"]!.AsArray().Add(new JsonObject { ["id"] = child["id"]!.GetValue<string>(), ["path"] = $"test{d}.json" });
            tree.Layers.Add(child);
        }
        return tree;
    }

    public JsonObject Layer(int i) => Layers[i];
    public JsonObject Node(int layer, int node) => Layers[layer]["nodes"]![node]!.AsObject();
    public JsonObject Edge(int layer, int edge) => Layers[layer]["edges"]![edge]!.AsObject();
    public JsonObject Entry(int i) => Catalog["layers"]![i]!.AsObject();
    public int LayerIndex(string layerId) => Layers.FindIndex(l => l["id"]!.GetValue<string>() == layerId);

    /// <summary>Second inside component in <paramref name="layer"/>: a copy of its import component (node 1) under a new id, opening <paramref name="opens"/>.</summary>
    public void AddOpener(int layer, string idSuffix, string opens)
    {
        var twin = Node(layer, 1).DeepClone().AsObject();
        twin["id"] = twin["id"]!.GetValue<string>() + idSuffix;
        twin["opensLayerId"] = opens;
        Layers[layer]["nodes"]!.AsArray().Add(twin);
    }
    public string EntryPath(int i) => Entry(i)["path"]!.GetValue<string>();

    public LayerTree Write()
    {
        Directory.CreateDirectory(ProjectDir);
        File.WriteAllText(System.IO.Path.Combine(ProjectDir, "index.json"), Catalog.ToJsonString());
        var written = new HashSet<string>();
        for (var i = 0; i < Layers.Count; i++)
        {
            var path = EntryPath(i);
            if (ArchitecturePaths.IsSafeBasename(path) && written.Add(path))
                File.WriteAllText(System.IO.Path.Combine(ProjectDir, path), Layers[i].ToJsonString());
        }
        return this;
    }

    public ArchitectureState Load(ProjectRegistration? registration = null) =>
        ArchitectureLoader.Load(LayersRoot, registration ?? TreeRegistration, () => SharedSchema.Value);

    public ArchitectureState WriteAndLoad() => Write().Load();

    public void Dispose()
    {
        if (Directory.Exists(LayersRoot)) Directory.Delete(LayersRoot, recursive: true);
    }
}

public sealed class ArchitectureLoaderTests(ITestOutputHelper output)
{
    private static ArchitectureState LoadReal() =>
        ArchitectureLoader.Load(RepoPaths.ArchitectureLayers, LayerTree.Registration, () => LayerTree.Schema);

    private static ArchitectureState LoadPrice() =>
        ArchitectureLoader.Load(RepoPaths.ArchitectureLayers, LayerTree.PriceRegistration, () => LayerTree.Schema);

    private const string DomainLayer = "layer:customerproject.fn.domain";
    private const string DomainUpdateLayer = "layer:customerproject.func-app.domain-update";
    private const string ApiLayer = "layer:customerproject.api";
    private const string FuncContainer = "container:customerproject.func-app";

    /// <summary>ADDENDA-011.1 customerproject: every Flow handler has exactly one root (handler id → root layer id).</summary>
    public static readonly IReadOnlyDictionary<string, string> CustomerprojectHandlerRoots = new Dictionary<string, string>
    {
        ["customerproject.api.http"] = ApiLayer,
        ["customerproject.fn.commands"] = "layer:customerproject.fn.commands",
        ["customerproject.fn.domain"] = DomainLayer,
        [LayerTree.RootHandler] = LayerTree.RootLayer,
        ["customerproject.fn.snapshot"] = "layer:customerproject.fn.snapshot",
    };

    /// <summary>ADDENDA-011.1 price: 8 handler roots. The other 10 handlers (subscriptions, 5 sub-*, 4 timers) are deferred/no-layer.</summary>
    public static readonly IReadOnlyDictionary<string, string> PriceHandlerRoots = new Dictionary<string, string>
    {
        ["price.api.http"] = "layer:price.api",
        ["price.api-batch.http"] = "layer:price.api-batch",
        ["price.fn.blob-optimera"] = "layer:price.fn.blob-optimera",
        ["price.fn.commands"] = "layer:price.fn.commands",
        ["price.fn.domain"] = "layer:price.fn.domain",
        ["price.fn.domain-ingress"] = "layer:price.fn.domain-ingress",
        ["price.fn.ingress"] = "layer:price.fn.ingress",
        ["price.fn.snapshot"] = "layer:price.fn.snapshot",
    };

    public static readonly string[] PriceHandlersWithoutRoot =
    [
        "price.fn.subscriptions", "price.fn.sub-customerproject", "price.fn.sub-customer", "price.fn.sub-warehouse", "price.fn.sub-currency",
        "price.fn.sub-product", "price.fn.timer-snapshot", "price.fn.timer-check-campaigns", "price.fn.timer-campaign-lowest",
        "price.fn.timer-reload-m3cloud",
    ];

    [Fact]
    public void Real_customerproject_catalog_has_five_handler_roots_and_six_depth_two_children()
    {
        var state = LoadReal();
        Assert.True(state.Status == ArchitectureStatus.Valid, string.Join("\n", state.Issues));
        Assert.Empty(state.Issues);
        var c = state.Catalog!;
        Assert.Equal(CustomerprojectHandlerRoots.Values.Order(StringComparer.Ordinal), c.Roots.Select(r => r.LayerId)); // ordinal by layerId
        Assert.Equal(11, c.Layers.Count);

        // Every root binds one handler in that handler's own container (ADDENDA-011.1); no container-only root exists.
        var handlers = LayerTree.Registration.Handlers.ToDictionary(h => h.Id);
        Assert.All(c.Roots, r =>
        {
            Assert.False(string.IsNullOrEmpty(r.FlowRef.HandlerId));
            Assert.Equal(handlers[r.FlowRef.HandlerId].ContainerId, r.FlowRef.ContainerId);
        });
        Assert.Equal(CustomerprojectHandlerRoots, c.HandlerLinks);
        Assert.Equal(LayerTree.Registration.Handlers.Select(h => h.Id).Order(), c.HandlerLinks.Keys.Order());

        // INGRESS handler root: 4 nodes, 3 conceptual edges, root crumb only, no children.
        var root = c.Roots.Single(r => r.LayerId == LayerTree.RootLayer);
        Assert.Equal(new ArchitectureFlowRef(LayerTree.IngressContainer, LayerTree.RootHandler), root.FlowRef);
        Assert.Equal("INGRESS — importing M3 change events", root.Title);
        var layer = c.Layers[LayerTree.RootLayer];
        Assert.Equal(4, layer.Document.GetProperty("nodes").GetArrayLength());
        Assert.Equal(3, layer.Document.GetProperty("edges").GetArrayLength());
        Assert.False(layer.Document.TryGetProperty("$schema", out _)); // relative file path is not served
        Assert.Equal(new[] { new ArchitectureCrumb(LayerTree.RootLayer, root.Title) }, layer.Breadcrumbs);
        Assert.Equal(root.FlowRef, layer.RootFlowRef);
        Assert.Empty(layer.Children);

        // DOMAIN opens domain-update (id kept, reparented) from its snapshot-apply component; the child inherits the DOMAIN handler anchor.
        var domain = c.Layers[DomainLayer];
        Assert.Equal(new[] { new ArchitectureChild(DomainUpdateLayer, "DOMAIN — applying ingress snapshots", "comp:customerproject.func.snapshot-apply") }, domain.Children);
        var nested = c.Layers[DomainUpdateLayer];
        Assert.Equal([DomainLayer, DomainUpdateLayer], nested.Breadcrumbs.Select(b => b.LayerId));
        Assert.Equal(domain.Breadcrumbs, nested.Breadcrumbs.Take(1));
        Assert.Equal(new ArchitectureFlowRef(FuncContainer, "customerproject.fn.domain"), nested.RootFlowRef);
        Assert.False(nested.Document.TryGetProperty("flowRef", out _)); // inherited, not authored
        Assert.Empty(nested.Children);
        Assert.Equal(new ArchitectureFlowRef("container:customerproject.api", "customerproject.api.http"), c.Layers[ApiLayer].RootFlowRef);

        // CP deepen: API, COMMANDS and SNAPSHOT each open depth-2 children from existing components (children ordinal by layerId).
        AssertChildren(ApiLayer,
            new ArchitectureChild("layer:customerproject.api.project-write", "API — project create/update decisions", "comp:customerproject.api.project-service"));
        AssertChildren("layer:customerproject.fn.commands",
            new ArchitectureChild("layer:customerproject.fn.commands.address-upsert", "COMMANDS — customer address upsert decisions", "comp:customerproject.func.address-upsert"),
            new ArchitectureChild("layer:customerproject.fn.commands.project-lifecycle", "COMMANDS — project lifecycle decisions", "comp:customerproject.func.project-lifecycle"));
        AssertChildren("layer:customerproject.fn.snapshot",
            new ArchitectureChild("layer:customerproject.fn.snapshot.address", "SNAPSHOT — customer-address snapshot composition", "comp:customerproject.ingress.address-snapshots"),
            new ArchitectureChild("layer:customerproject.fn.snapshot.project-price", "SNAPSHOT — project-price snapshot composition", "comp:customerproject.ingress.price-snapshots"));

        void AssertChildren(string parentId, params ArchitectureChild[] expected)
        {
            var parent = c.Layers[parentId];
            Assert.Equal(expected, parent.Children);
            Assert.All(expected, e =>
            {
                var child = c.Layers[e.LayerId];
                Assert.Equal([parentId, e.LayerId], child.Breadcrumbs.Select(b => b.LayerId));
                Assert.Equal(parent.Breadcrumbs, child.Breadcrumbs.Take(1));
                Assert.Equal(parent.RootFlowRef, child.RootFlowRef);
                Assert.Empty(child.Children);
            });
        }

        Assert.All(c.Layers.Values.Select(l => l.Document), d =>
            Assert.All(d.GetProperty("edges").EnumerateArray(), e => Assert.Equal("conceptual", e.GetProperty("kind").GetString())));
    }

    [Fact]
    public void Real_price_catalog_has_eight_handler_roots_and_deepen_children_down_to_depth_five_calculator_guide()
    {
        var state = LoadPrice();
        Assert.True(state.Status == ArchitectureStatus.Valid, string.Join("\n", state.Issues));
        Assert.Empty(state.Warnings);
        var c = state.Catalog!;
        Assert.Equal("price.dev", c.RegistrationId);
        Assert.Equal(PriceHandlerRoots.Values.Order(StringComparer.Ordinal), c.Roots.Select(r => r.LayerId));
        Assert.Equal(17, c.Layers.Count);
        var handlers = LayerTree.PriceRegistration.Handlers.ToDictionary(h => h.Id);
        Assert.All(c.Roots, r => Assert.Equal(handlers[r.FlowRef.HandlerId].ContainerId, r.FlowRef.ContainerId));
        Assert.Equal(PriceHandlerRoots, c.HandlerLinks);
        // Deferred (subscriptions, sub-*) and no-layer (timers) handlers deliberately have no root; nothing else is unaccounted for.
        Assert.Equal(handlers.Keys.Order(), PriceHandlerRoots.Keys.Concat(PriceHandlersWithoutRoot).Order());

        var api = c.Layers["layer:price.api"];
        Assert.Equal(new[] { new ArchitectureChild("layer:price.api.get-prices", "Price reading — strategy, DSP calculation and M3 fallback", "comp:price.api.price-reader") }, api.Children);
        var nested = c.Layers["layer:price.api.get-prices"];
        Assert.Equal(["layer:price.api", "layer:price.api.get-prices"], nested.Breadcrumbs.Select(b => b.LayerId));
        Assert.Equal(new ArchitectureFlowRef("container:price.api", "price.api.http"), nested.RootFlowRef);
        Assert.False(nested.Document.TryGetProperty("flowRef", out _));
        Assert.Equal(new[] { new ArchitectureChild("layer:price.api.get-prices.calculator", "DSP price calculator — composition stages", "comp:price.get-prices.calculator") }, nested.Children);

        // Depth-3 calculator inherits the API handler root through get-prices.
        var calculator = c.Layers["layer:price.api.get-prices.calculator"];
        Assert.Equal(["layer:price.api", "layer:price.api.get-prices", "layer:price.api.get-prices.calculator"], calculator.Breadcrumbs.Select(b => b.LayerId));
        Assert.Equal(nested.Breadcrumbs, calculator.Breadcrumbs.Take(2));
        Assert.Equal(nested.RootFlowRef, calculator.RootFlowRef);
        Assert.False(calculator.Document.TryGetProperty("flowRef", out _));
        Assert.Equal(new[]
        {
            new ArchitectureChild("layer:price.api.get-prices.calculator.context", "Calculator context & strategy gate (guide fig. 1)", "comp:price.calc.context"),
            new ArchitectureChild("layer:price.api.get-prices.calculator.discount", "Discount positions loop (guide fig. 2, DIPO loop)", "comp:price.calc.discount"),
            new ArchitectureChild("layer:price.api.get-prices.calculator.matrix", "Matrix lookup, item price and convert/round (guide fig. 3)", "comp:price.calc.matrix"),
            new ArchitectureChild("layer:price.api.get-prices.calculator.select", "Strategy '2': per-list pricing and best price (guide fig. 2)", "comp:price.calc.select"),
        }, calculator.Children);

        // Depth-4 guide layers inherit the API handler root; only discount opens a depth-5 child.
        string[] calcCrumbs = ["layer:price.api", "layer:price.api.get-prices", "layer:price.api.get-prices.calculator"];
        foreach (var d4 in new[] { "context", "matrix", "discount", "select" })
        {
            var layer = c.Layers["layer:price.api.get-prices.calculator." + d4];
            Assert.Equal([.. calcCrumbs, "layer:price.api.get-prices.calculator." + d4], layer.Breadcrumbs.Select(b => b.LayerId));
            Assert.Equal(nested.RootFlowRef, layer.RootFlowRef);
            Assert.False(layer.Document.TryGetProperty("flowRef", out _));
            if (d4 != "discount") Assert.Empty(layer.Children);
        }
        Assert.Equal(new[] { new ArchitectureChild("layer:price.api.get-prices.calculator.discount.relations", "Discount relation, base and type rules (DIRE/DIBE/DITP)", "comp:price.calc-disc.relation") },
            c.Layers["layer:price.api.get-prices.calculator.discount"].Children);
        var relations = c.Layers["layer:price.api.get-prices.calculator.discount.relations"];
        Assert.Equal([.. calcCrumbs, "layer:price.api.get-prices.calculator.discount", "layer:price.api.get-prices.calculator.discount.relations"], relations.Breadcrumbs.Select(b => b.LayerId));
        Assert.Equal(nested.RootFlowRef, relations.RootFlowRef);
        Assert.Empty(relations.Children);

        // Price deepen depth-2 siblings under snapshot and commands.
        Assert.Equal(new[] { new ArchitectureChild("layer:price.fn.snapshot.full-reload", "SNAPSHOT — Optimera full reload", "comp:price.ingress.full-reload") },
            c.Layers["layer:price.fn.snapshot"].Children);
        Assert.Equal(["layer:price.fn.snapshot", "layer:price.fn.snapshot.full-reload"],
            c.Layers["layer:price.fn.snapshot.full-reload"].Breadcrumbs.Select(b => b.LayerId));
        Assert.Equal(new[] { new ArchitectureChild("layer:price.fn.commands.work", "COMMANDS — import, campaign-change and price-history work", "comp:price.func.commands") },
            c.Layers["layer:price.fn.commands"].Children);
        Assert.Equal(["layer:price.fn.commands", "layer:price.fn.commands.work"],
            c.Layers["layer:price.fn.commands.work"].Breadcrumbs.Select(b => b.LayerId));

        // Deliberately thin, high-uncertainty layer still loads and keeps its caveats visible.
        var batch = c.Layers["layer:price.api-batch"].Document;
        Assert.Equal((5, 4), (batch.GetProperty("nodes").GetArrayLength(), batch.GetProperty("edges").GetArrayLength()));
        Assert.NotEqual(0, batch.GetProperty("uncertainties").GetArrayLength());
    }

    [Fact]
    public void Project_without_a_catalog_folder_is_absent_without_warnings()
    {
        using var tree = LayerTree.Real();
        tree.Write(); // the temp layers root holds customerproject only, no price folder
        var state = tree.Load(LayerTree.PriceRegistration);
        Assert.Equal(ArchitectureStatus.Absent, state.Status);
        Assert.Empty(state.Warnings);
        Assert.Equal(ArchitectureStatus.Valid, tree.Load().Status);
    }

    [Fact]
    public void Invalid_catalog_in_one_project_does_not_affect_the_other()
    {
        using var cp = LayerTree.Real();
        using var price = LayerTree.PriceReal(cp.LayersRoot);
        cp.Layer(1)["parentLayerId"] = "layer:customerproject.missing";
        cp.Write();
        price.Write();
        Assert.Equal(ArchitectureStatus.Invalid, cp.Load().Status);
        var p = price.Load();
        Assert.True(p.Status == ArchitectureStatus.Valid, string.Join("\n", p.Issues));
        Assert.Equal(8, p.Catalog!.Roots.Count);
    }

    [Fact]
    public void Real_catalogs_have_no_bidirectional_boundary_node()
    {
        foreach (var state in new[] { LoadReal(), LoadPrice() })
            Assert.DoesNotContain(state.Issues, i => i.Contains("both receives from and sends to"));
    }

    [Fact]
    public void Repeated_flow_entity_refs_on_distinct_nodes_stay_valid()
    {
        // flowEntityId only has to name a registration entity (V-006); several nodes may point at the same queue/topic.
        var refs = LoadReal().Catalog!.Layers.Values
            .SelectMany(l => l.Document.GetProperty("nodes").EnumerateArray())
            .Where(n => n.TryGetProperty("flowEntityId", out _))
            .GroupBy(n => n.GetProperty("flowEntityId").GetString());
        Assert.Contains(refs, g => g.Count() > 1);

        using var tree = LayerTree.Real();
        var layer = tree.Layers.FindIndex(l => l["id"]!.GetValue<string>() == LayerTree.RootLayer);
        var nodes = tree.Layer(layer)["nodes"]!.AsArray();
        var entity = nodes.Select(n => n!["flowEntityId"]?.GetValue<string>()).First(e => e is not null);
        foreach (var n in nodes) n!["flowEntityId"] = entity; // every node of one layer on the same entity
        var state = tree.WriteAndLoad();
        Assert.True(state.Status == ArchitectureStatus.Valid, string.Join("\n", state.Issues));
    }

    // ---- catalogHash (DESIGN-flow-drilldown §8.5) ----

    /// <summary>Byte-for-byte copy of a checked-in catalog (LayerTree.Write re-serializes, which changes the bytes).</summary>
    private static string CopyCatalog(string project, string layersRoot)
    {
        var dir = Directory.CreateDirectory(Path.Combine(layersRoot, project)).FullName;
        foreach (var f in Directory.GetFiles(Path.Combine(RepoPaths.ArchitectureLayers, project), "*.json"))
            File.Copy(f, Path.Combine(dir, Path.GetFileName(f)));
        return dir;
    }

    private static string ExpectedHash(string dir)
    {
        var catalog = JsonNode.Parse(File.ReadAllBytes(Path.Combine(dir, "index.json")))!;
        var files = new[] { "index.json" }.Concat(catalog["layers"]!.AsArray().Select(e => e!["path"]!.GetValue<string>()));
        var input = files.SelectMany(f => System.Text.Encoding.UTF8.GetBytes(f + "\0").Concat(File.ReadAllBytes(Path.Combine(dir, f)))).ToArray();
        return "sha256:" + Convert.ToHexStringLower(SHA256.HashData(input));
    }

    [Fact]
    public void Catalog_hash_covers_basenames_and_bytes_in_catalog_order()
    {
        foreach (var (project, state) in new[] { ("customerproject", LoadReal()), ("price", LoadPrice()) })
        {
            Assert.Matches("^sha256:[0-9a-f]{64}$", state.Catalog!.CatalogHash);
            Assert.Equal(ExpectedHash(Path.Combine(RepoPaths.ArchitectureLayers, project)), state.Catalog.CatalogHash);
        }
        Assert.NotEqual(LoadReal().Catalog!.CatalogHash, LoadPrice().Catalog!.CatalogHash);
    }

    [Fact]
    public void Catalog_hash_is_stable_across_loads_and_locations_and_tracks_layer_bytes()
    {
        var root = Path.Combine(Path.GetTempPath(), "dsp-arch-hash-" + Guid.NewGuid().ToString("N"));
        try
        {
            var dir = CopyCatalog("customerproject", root);
            string Hash() => ArchitectureLoader.Load(root, LayerTree.Registration, () => LayerTree.Schema).Catalog!.CatalogHash;
            var first = Hash();
            Assert.Equal(first, Hash());
            Assert.Equal(LoadReal().Catalog!.CatalogHash, first); // no machine path in the input

            var layer = Path.Combine(dir, "fn.ingress.json");
            File.AppendAllText(layer, " "); // one byte, still the same valid document
            var changed = Hash();
            Assert.NotEqual(first, changed);
            Assert.Equal(ExpectedHash(dir), changed);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Missing_layers_root_or_project_dir_is_absent()
    {
        using var tree = LayerTree.Real(); // nothing written
        Assert.Equal(ArchitectureStatus.Absent, tree.Load().Status);
        Directory.CreateDirectory(tree.ProjectDir);
        Assert.Equal(ArchitectureStatus.Absent, tree.Load().Status);
    }

    [Fact]
    public void Underscore_prefixed_obsolete_dirs_are_never_a_project_or_layer_source()
    {
        using var tree = LayerTree.Real();
        tree.Write();
        // Retired container roots kept for reference under the project's _obsolete/: a container-only root that V-011 would reject,
        // with its own index.json. Only index-listed sibling basenames are ever read, so the catalog stays valid and unchanged.
        var obsolete = Directory.CreateDirectory(Path.Combine(tree.ProjectDir, "_obsolete")).FullName;
        var retired = tree.Layer(0).DeepClone().AsObject();
        retired["id"] = "layer:customerproject.ingress-app";
        retired["flowRef"] = new JsonObject { ["containerId"] = LayerTree.IngressContainer };
        File.WriteAllText(Path.Combine(obsolete, "ingress-app.json"), retired.ToJsonString());
        var retiredCatalog = tree.Catalog.DeepClone().AsObject();
        retiredCatalog["layers"] = new JsonArray(new JsonObject { ["id"] = "layer:customerproject.ingress-app", ["path"] = "ingress-app.json" });
        File.WriteAllText(Path.Combine(obsolete, "index.json"), retiredCatalog.ToJsonString());
        // A layers-root level _obsolete/ project folder is not a project either.
        Directory.CreateDirectory(Path.Combine(tree.LayersRoot, "_obsolete"));
        File.WriteAllText(Path.Combine(tree.LayersRoot, "_obsolete", "index.json"), retiredCatalog.ToJsonString());

        var state = tree.Load();
        Assert.True(state.Status == ArchitectureStatus.Valid, string.Join("\n", state.Issues));
        Assert.Equal(tree.Layers.Select(l => l["id"]!.GetValue<string>()).Order(StringComparer.Ordinal), state.Catalog!.Layers.Keys.Order(StringComparer.Ordinal));
        Assert.DoesNotContain("layer:customerproject.ingress-app", state.Catalog.Layers.Keys);
        Assert.All(state.Catalog.Roots, r => Assert.NotNull(r.FlowRef.HandlerId));
        Assert.Null(ArchitecturePaths.ProjectDir(tree.LayersRoot, "_obsolete"));

        // Listing an _obsolete file in the catalog does not reach it: not a sibling basename (schema path pattern; V-003 backs it up).
        tree.Entry(0)["path"] = "_obsolete/ingress-app.json";
        File.WriteAllText(Path.Combine(tree.ProjectDir, "index.json"), tree.Catalog.ToJsonString());
        var listed = tree.Load();
        Assert.Equal(ArchitectureStatus.Invalid, listed.Status);
        Assert.Contains(listed.Issues, i => i.StartsWith("V-001: index.json /layers/0/path"));
    }

    [Fact]
    public void Packaged_layers_contain_no_underscore_prefixed_dirs()
    {
        Assert.Empty(Directory.GetDirectories(RepoPaths.ArchitectureLayers, "_*", SearchOption.AllDirectories));
        foreach (var project in Directory.GetDirectories(RepoPaths.ArchitectureLayers))
        {
            var listed = JsonNode.Parse(File.ReadAllText(Path.Combine(project, "index.json")))!["layers"]!.AsArray().Select(e => e!["path"]!.GetValue<string>());
            Assert.Equal(listed.Append("index.json").Order(), Directory.GetFiles(project).Select(Path.GetFileName).Order());
        }
    }

    [Theory]
    [InlineData(3)]
    [InlineData(6)]
    public void Synthetic_tree_has_canonical_breadcrumbs_inherited_flow_ref_and_no_depth_ceiling(int depth)
    {
        using var tree = LayerTree.Deep(depth);
        var state = tree.WriteAndLoad();
        Assert.True(state.Status == ArchitectureStatus.Valid, string.Join("\n", state.Issues));
        var c = state.Catalog!;
        Assert.Single(c.Roots);
        Assert.Equal(depth, c.Layers.Count);

        var deepest = c.Layers[$"{LayerTree.RootLayer}.test{depth}"];
        Assert.Equal([LayerTree.RootLayer, .. Enumerable.Range(2, depth - 1).Select(d => $"{LayerTree.RootLayer}.test{d}")],
            deepest.Breadcrumbs.Select(b => b.LayerId));
        Assert.Equal($"Depth {depth}", deepest.Breadcrumbs[^1].Title);
        Assert.Equal(new ArchitectureFlowRef(LayerTree.IngressContainer, LayerTree.RootHandler), deepest.RootFlowRef);
        Assert.Empty(deepest.Children);

        var child = Assert.Single(c.Layers[LayerTree.RootLayer].Children);
        Assert.Equal(new ArchitectureChild($"{LayerTree.RootLayer}.test2", "Depth 2", "comp:customerproject.ingress.import"), child);
        Assert.Equal(c.Layers[LayerTree.RootLayer].Breadcrumbs, c.Layers[$"{LayerTree.RootLayer}.test2"].Breadcrumbs.Take(1));
    }

    [Fact]
    public void Children_are_sorted_by_layer_id()
    {
        using var tree = LayerTree.Deep(2);
        var sibling = tree.Layer(1).DeepClone().AsObject();
        sibling["id"] = $"{LayerTree.RootLayer}.a-sibling";
        foreach (var n in sibling["nodes"]!.AsArray()) n!["id"] = n["id"]!.GetValue<string>() + ".sib";
        foreach (var e in sibling["edges"]!.AsArray())
            foreach (var key in new[] { "id", "from", "to" }) e![key] = e[key]!.GetValue<string>() + ".sib";
        tree.AddOpener(0, ".sib", sibling["id"]!.GetValue<string>()); // second inside component in the root
        tree.Catalog["layers"]!.AsArray().Add(new JsonObject { ["id"] = sibling["id"]!.GetValue<string>(), ["path"] = "sibling.json" });
        tree.Layers.Add(sibling);

        var state = tree.WriteAndLoad();
        Assert.True(state.Status == ArchitectureStatus.Valid, string.Join("\n", state.Issues));
        Assert.Equal([$"{LayerTree.RootLayer}.a-sibling", $"{LayerTree.RootLayer}.test2"],
            state.Catalog!.Layers[LayerTree.RootLayer].Children.Select(c => c.LayerId));
    }

    [Fact]
    public void Conceptual_dependency_cycles_are_permitted()
    {
        using var tree = LayerTree.Real();
        // SNAPSHOT: address-snapshots ⇄ price-snapshots is an explanatory dependency cycle between inside components, not a navigation
        // cycle. (A two-edge cycle through one boundary node is the bidirectional V-007 rejection instead.)
        var snapshot = tree.LayerIndex("layer:customerproject.fn.snapshot");
        foreach (var (id, from, to) in new[] { ("there", "address", "price"), ("back", "price", "address") })
        {
            var edge = tree.Edge(snapshot, 0).DeepClone().AsObject();
            (edge["id"], edge["from"], edge["to"], edge["relationship"]) = ($"aedge:customerproject.snapshot.cycle-{id}",
                $"comp:customerproject.ingress.{from}-snapshots", $"comp:customerproject.ingress.{to}-snapshots", "dependency");
            tree.Layer(snapshot)["edges"]!.AsArray().Add(edge);
        }
        var state = tree.WriteAndLoad();
        Assert.True(state.Status == ArchitectureStatus.Valid, string.Join("\n", state.Issues));
    }

    // ---- ADDENDA-011.1 V-011: roots are Flow handler items ----

    private static JsonObject HandlerRef(string container, string handler) => new() { ["containerId"] = container, ["handlerId"] = handler };

    [Fact]
    public void Container_only_root_is_rejected_as_V011_without_a_legacy_mode()
    {
        using var tree = LayerTree.Real();
        tree.Layer(tree.LayerIndex(LayerTree.RootLayer))["flowRef"]!.AsObject().Remove("handlerId");
        var state = tree.WriteAndLoad();
        Assert.Equal(ArchitectureStatus.Invalid, state.Status);
        Assert.Null(state.Catalog);
        Assert.Contains("V-011: fn.ingress.json root has no flowRef.handlerId (container-only roots are not Explore targets)", state.Issues);
        Assert.Contains(state.Issues, i => i.StartsWith("V-001: fn.ingress.json") && i.Contains("handlerId")); // schema says the same

        // The cross-document validator rejects it on its own too (no schema in front, e.g. a blank handlerId).
        var docs = tree.Layers.Select(l => l.Deserialize<ArchitectureLayerDoc>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!).ToList();
        var catalog = tree.Catalog.Deserialize<ArchitectureCatalogDoc>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Contains($"V-011: root {LayerTree.RootLayer} has no flowRef.handlerId (container-only roots are not Explore targets)",
            ArchitectureValidator.Check(catalog, docs, LayerTree.Registration));
    }

    public static TheoryData<string, string> HandlerRootRejections => new()
    {
        { "dangling handler", $"V-011: root {LayerTree.RootLayer} handler customerproject.fn.missing is not in the registration" },
        { "handler in another container", $"V-011: root {LayerTree.RootLayer} handler customerproject.fn.domain belongs to container:customerproject.func-app, not {LayerTree.IngressContainer}" },
        { "handler of a sibling container's root", $"V-011: root {LayerTree.RootLayer} handler customerproject.api.http belongs to container:customerproject.api, not {LayerTree.IngressContainer}" },
        { "two roots claim one handler", $"V-011: more than one root for handler customerproject.fn.snapshot (layer:customerproject.fn.ingress, layer:customerproject.fn.snapshot)" },
        { "two roots claim one handler, each in its right container", $"V-011: more than one root for handler customerproject.fn.domain (layer:customerproject.fn.commands, layer:customerproject.fn.domain)" },
    };

    [Theory]
    [MemberData(nameof(HandlerRootRejections))]
    public void Handler_root_binding_violations_are_V011(string name, string expected)
    {
        using var tree = LayerTree.Real();
        var root = tree.Layer(tree.LayerIndex(LayerTree.RootLayer));
        switch (name)
        {
            case "dangling handler": root["flowRef"] = HandlerRef(LayerTree.IngressContainer, "customerproject.fn.missing"); break;
            case "handler in another container": root["flowRef"] = HandlerRef(LayerTree.IngressContainer, "customerproject.fn.domain"); break;
            case "handler of a sibling container's root": root["flowRef"] = HandlerRef(LayerTree.IngressContainer, "customerproject.api.http"); break;
            case "two roots claim one handler": root["flowRef"] = HandlerRef(LayerTree.IngressContainer, "customerproject.fn.snapshot"); break;
            case "two roots claim one handler, each in its right container":
                tree.Layer(tree.LayerIndex("layer:customerproject.fn.commands"))["flowRef"] = HandlerRef(FuncContainer, "customerproject.fn.domain"); break;
        }
        var state = tree.WriteAndLoad();
        Assert.Equal(ArchitectureStatus.Invalid, state.Status);
        Assert.Null(state.Catalog);
        Assert.Contains(expected, state.Issues);
        Assert.StartsWith("ARCHITECTURE_INVALID: ", Assert.Single(state.Warnings));
        var flow = ArchitectureService.Link(Graph(LayerTree.Registration), state);
        Assert.All(flow.Nodes, n => Assert.Null(n.OpensLayerId)); // rejected catalog: no Explore anywhere, Flow keeps working
    }

    public static TheoryData<string> Rejections => new(Mutations.Keys);

    private static readonly Dictionary<string, Action<LayerTree>> Mutations = new()
    {
        ["duplicate layer id"] = t => t.Entry(2)["id"] = t.Entry(1)["id"]!.GetValue<string>(),
        ["duplicate catalog path"] = t => t.Entry(2)["path"] = t.EntryPath(1),
        ["entry id mismatches document"] = t => t.Layer(2)["id"] = "layer:customerproject.ingress-app.other",
        ["duplicate node id"] = t => t.Layer(0)["nodes"]!.AsArray().Add(t.Node(0, 0).DeepClone()),
        ["duplicate node id across layers"] = t => t.Node(1, 0)["id"] = t.Node(0, 0)["id"]!.GetValue<string>(),
        ["duplicate edge id across layers"] = t => t.Edge(1, 0)["id"] = t.Edge(0, 0)["id"]!.GetValue<string>(),
        ["dangling parent"] = t => t.Layer(1)["parentLayerId"] = "layer:customerproject.missing",
        ["self parent"] = t => t.Layer(1)["parentLayerId"] = t.Layer(1)["id"]!.GetValue<string>(),
        ["navigation cycle"] = t => t.Layer(1)["parentLayerId"] = t.Layer(2)["id"]!.GetValue<string>(),
        ["dangling opener"] = t => t.Node(0, 1)["opensLayerId"] = "layer:customerproject.missing",
        ["orphan child"] = t => t.Node(0, 1).Remove("opensLayerId"),
        ["duplicate opener"] = t => t.AddOpener(0, ".twin", t.Layer(1)["id"]!.GetValue<string>()),
        ["non-child opener (grandchild)"] = t => t.Node(0, 1)["opensLayerId"] = t.Layer(2)["id"]!.GetValue<string>(),
        ["opener targets root"] = t => t.Node(1, 1)["opensLayerId"] = LayerTree.RootLayer,
        ["outside opener"] = t => t.Node(0, 1)["boundary"] = "outside",
        ["store opener"] = t => t.Node(0, 1)["kind"] = "store",
        ["cross-project layer"] = t => t.Layer(0)["project"] = "price",
        ["cross-project layer id"] = t => { t.Layer(1)["id"] = "layer:price.ingress"; t.Entry(1)["id"] = "layer:price.ingress"; t.Node(0, 1)["opensLayerId"] = "layer:price.ingress"; },
        ["cross-project node id"] = t => t.Node(0, 0)["id"] = "comp:price.ingress.input",
        ["cross-project catalog"] = t => t.Catalog["project"] = "price",
        ["flow container missing"] = t => t.Layer(0)["flowRef"]!["containerId"] = "container:customerproject.missing",
        ["flow handler outside container"] = t => t.Layer(0)["flowRef"]!["handlerId"] = "customerproject.fn.domain",
        ["flow handler missing"] = t => t.Layer(0)["flowRef"]!["handlerId"] = "customerproject.fn.missing",
        ["container-only root"] = t => t.Layer(0)["flowRef"]!.AsObject().Remove("handlerId"),
        ["blank handlerId"] = t => t.Layer(0)["flowRef"]!["handlerId"] = "",
        ["duplicate root for one handler"] = t => { t.Layer(1)["parentLayerId"] = null; t.Layer(1)["flowRef"] = HandlerRef(LayerTree.IngressContainer, LayerTree.RootHandler); t.Node(0, 1).Remove("opensLayerId"); },
        ["descendant with flowRef"] = t => t.Layer(1)["flowRef"] = HandlerRef(LayerTree.IngressContainer, LayerTree.RootHandler),
        ["flow entity missing"] = t => t.Node(0, 0)["flowEntityId"] = "q:missing",
        ["dangling edge endpoint"] = t => t.Edge(0, 0)["to"] = "comp:customerproject.missing",
        ["edge endpoint in another layer"] = t => t.Edge(0, 0)["to"] = t.Node(1, 1)["id"]!.GetValue<string>(),
        ["self edge"] = t => t.Edge(0, 0)["to"] = t.Edge(0, 0)["from"]!.GetValue<string>(),
        ["bidirectional queue"] = t => t.Layer(0)["edges"]!.AsArray().Add(Reversed(t.Edge(0, 0), "aedge:customerproject.ingress.back")), // import → input queue
        ["bidirectional store"] = t => t.Layer(0)["edges"]!.AsArray().Add(Reversed(t.Edge(0, 1), "aedge:customerproject.ingress.reread")), // storage → import
        ["live edge kind"] = t => t.Edge(0, 0)["kind"] = "observed",
        ["live field on node"] = t => t.Node(0, 0)["activeCount"] = 3,
        ["scope widened by descendant"] = t => { t.Layer(0)["scope"] = "local"; t.Layer(1)["scope"] = "both"; },
        ["empty evidence"] = t => t.Node(0, 0)["sources"] = new JsonArray(),
        ["inference without rationale"] = t => t.Layer(0)["sources"]!.AsArray().Add(new JsonObject { ["kind"] = "inferred", ["ref"] = "dsp-customerproject/README.md", ["confidence"] = "inferred" }),
        ["uncertain without rationale"] = t => { var src = t.Node(0, 1)["sources"]![0]!.AsObject(); src["confidence"] = "uncertain"; src.Remove("note"); },
        ["file url as repo ref"] = t => t.Node(0, 1)["sources"]![0]!["ref"] = "file:///etc/passwd",
        ["absolute repo ref"] = t => t.Node(0, 1)["sources"]![0]!["ref"] = "/workspace/dsp/dsp-customerproject/README.md",
        ["traversal repo ref"] = t => t.Node(0, 1)["sources"]![0]!["ref"] = "dsp-customerproject/../../etc/passwd",
        ["non-confluence url"] = t => t.Layer(0)["sources"]![3]!["ref"] = "https://evil.example/wiki/pages/1",
        ["bad reviewedAt"] = t => t.Layer(0)["reviewedAt"] = "yesterday",
        ["unsupported schemaVersion"] = t => t.Layer(0)["schemaVersion"] = 2,
        ["unknown property"] = t => t.Layer(0)["childLayerIds"] = new JsonArray(),
        ["catalog as layer"] = t => t.Layer(1)["documentType"] = "architecture-catalog",
        ["path traversal"] = t => t.Entry(1)["path"] = "../secret.json",
        ["absolute path"] = t => t.Entry(1)["path"] = "/etc/passwd",
        ["subdirectory path"] = t => t.Entry(1)["path"] = "sub/test2.json",
        ["url path"] = t => t.Entry(1)["path"] = "https://example.com/x.json",
    };

    private static JsonObject Reversed(JsonObject edge, string id)
    {
        var back = edge.DeepClone().AsObject();
        (back["id"], back["from"], back["to"]) = (id, edge["to"]!.GetValue<string>(), edge["from"]!.GetValue<string>());
        return back;
    }

    [Fact]
    public void Bidirectional_boundary_node_names_the_rule_node_and_component()
    {
        using var tree = LayerTree.Deep(2);
        tree.Layer(0)["edges"]!.AsArray().Add(Reversed(tree.Edge(0, 0), "aedge:customerproject.ingress.back"));
        var state = tree.WriteAndLoad();
        Assert.Equal(ArchitectureStatus.Invalid, state.Status);
        Assert.Equal(["V-007: boundary node comp:customerproject.ingress.input both receives from and sends to the same component comp:customerproject.ingress.import"],
            state.Issues);

        // A self edge is reported once as a self edge, not again as bidirectional.
        using var self = LayerTree.Deep(2);
        self.Edge(0, 0)["to"] = self.Edge(0, 0)["from"]!.GetValue<string>();
        Assert.Equal(["V-007: edge aedge:customerproject.ingress.changes is a self edge"], self.WriteAndLoad().Issues);
    }

    [Theory]
    [MemberData(nameof(Rejections))]
    public void Invalid_catalogs_are_rejected_atomically(string name)
    {
        using var tree = LayerTree.Deep(3);
        Mutations[name](tree);
        var state = tree.WriteAndLoad();
        Assert.Equal(ArchitectureStatus.Invalid, state.Status);
        Assert.Null(state.Catalog); // nothing partial is served
        Assert.NotEmpty(state.Issues);
        foreach (var issue in state.Issues) output.WriteLine(issue);
        var warning = Assert.Single(state.Warnings);
        Assert.StartsWith("ARCHITECTURE_INVALID: ", warning);
        Assert.DoesNotContain(tree.LayersRoot, warning);
        Assert.DoesNotContain(RepoPaths.Root, warning);
    }

    [Fact]
    public void Malformed_json_and_missing_layer_file_are_rejected_without_paths()
    {
        using var tree = LayerTree.Deep(2);
        tree.Write();
        File.WriteAllText(Path.Combine(tree.ProjectDir, tree.EntryPath(1)), "{ not json");
        var state = tree.Load();
        Assert.Equal(ArchitectureStatus.Invalid, state.Status);
        Assert.Contains(state.Issues, i => i.Contains("not valid JSON"));

        File.Delete(Path.Combine(tree.ProjectDir, tree.EntryPath(1)));
        state = tree.Load();
        Assert.Equal(ArchitectureStatus.Invalid, state.Status);
        Assert.All(state.Issues, i => Assert.DoesNotContain(tree.LayersRoot, i));
    }

    [Fact]
    public void Catalog_for_another_env_of_the_project_is_absent_not_borrowed()
    {
        using var tree = LayerTree.Real();
        tree.Catalog["registrationId"] = "customerproject.test";
        Assert.Equal(ArchitectureStatus.Absent, tree.WriteAndLoad().Status);
    }

    [Fact]
    public void Registration_reload_revalidates_flow_bindings()
    {
        using var tree = LayerTree.Real();
        tree.Write();
        var r = new RegistrationLoader(RepoPaths.Schema).Load(RepoPaths.Registration);
        r.Containers.RemoveAll(c => c.Id == LayerTree.IngressContainer);
        var state = tree.Load(r);
        Assert.Equal(ArchitectureStatus.Invalid, state.Status);
        Assert.Contains(state.Issues, i => i.StartsWith("V-006"));
    }

    [Fact]
    public void Symlink_escaping_the_project_directory_is_rejected()
    {
        using var tree = LayerTree.Deep(2);
        tree.Write();
        var outside = tree.LayersRoot + "-outside";
        Directory.CreateDirectory(outside);
        try
        {
            // A perfectly valid layer document, but reached through a link that leaves the project directory.
            var target = Path.Combine(outside, "test2.json");
            File.Move(Path.Combine(tree.ProjectDir, tree.EntryPath(1)), target);
            File.CreateSymbolicLink(Path.Combine(tree.ProjectDir, tree.EntryPath(1)), target);
            var state = tree.Load();
            Assert.Equal(ArchitectureStatus.Invalid, state.Status);
            Assert.Contains(state.Issues, i => i.StartsWith("V-003") && i.Contains("outside the project"));
            Assert.All(state.Issues, i => Assert.DoesNotContain(outside, i));

            // Relative link climbing out of the directory.
            File.Delete(Path.Combine(tree.ProjectDir, tree.EntryPath(1)));
            File.CreateSymbolicLink(Path.Combine(tree.ProjectDir, tree.EntryPath(1)), Path.Combine("..", "..", Path.GetFileName(outside), "test2.json"));
            Assert.Equal(ArchitectureStatus.Invalid, tree.Load().Status);

            // A link to a sibling inside the project directory is fine.
            File.Delete(Path.Combine(tree.ProjectDir, tree.EntryPath(1)));
            File.Move(target, Path.Combine(tree.ProjectDir, "real-test2.json"));
            File.CreateSymbolicLink(Path.Combine(tree.ProjectDir, tree.EntryPath(1)), "real-test2.json");
            Assert.Equal(ArchitectureStatus.Valid, tree.Load().Status);
        }
        finally { Directory.Delete(outside, recursive: true); }
    }

    [Fact]
    public void Symlinked_project_directory_or_index_outside_the_layers_root_is_rejected()
    {
        using var tree = LayerTree.Real();
        tree.Write();
        var elsewhere = tree.LayersRoot + "-elsewhere";
        try
        {
            Directory.Move(tree.ProjectDir, elsewhere);
            Directory.CreateSymbolicLink(tree.ProjectDir, elsewhere);
            var state = tree.Load();
            Assert.Equal(ArchitectureStatus.Invalid, state.Status);
            Assert.Contains(state.Issues, i => i.StartsWith("V-003") && i.Contains("layers root"));

            Directory.Delete(tree.ProjectDir);
            Directory.CreateDirectory(tree.ProjectDir);
            File.Copy(Path.Combine(elsewhere, "fn.ingress.json"), Path.Combine(tree.ProjectDir, "fn.ingress.json"));
            File.CreateSymbolicLink(Path.Combine(tree.ProjectDir, "index.json"), Path.Combine(elsewhere, "index.json"));
            Assert.Equal(ArchitectureStatus.Invalid, tree.Load().Status);

            File.Delete(Path.Combine(tree.ProjectDir, "index.json"));
            File.CreateSymbolicLink(Path.Combine(tree.ProjectDir, "index.json"), Path.Combine(elsewhere, "gone.json")); // dangling
            Assert.Equal(ArchitectureStatus.Invalid, tree.Load().Status);
        }
        finally { if (Directory.Exists(elsewhere)) Directory.Delete(elsewhere, recursive: true); }
    }

    [Fact]
    public void Real_path_resolves_nested_and_relative_links()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dsp-realpath-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "a", "b"));
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(dir, "link"), Path.Combine("a", "b"));
            File.WriteAllText(Path.Combine(dir, "a", "b", "f.json"), "{}");
            File.CreateSymbolicLink(Path.Combine(dir, "a", "b", "g.json"), "f.json");
            var real = ArchitecturePaths.RealPath(dir);
            Assert.Equal(Path.Combine(real, "a", "b", "f.json"), ArchitecturePaths.RealPath(Path.Combine(dir, "link", "g.json")));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Theory]
    [InlineData("ingress-app.json", true)]
    [InlineData("a.b-c.json", true)]
    [InlineData("../x.json", false)]
    [InlineData("/etc/passwd", false)]
    [InlineData(".hidden.json", false)]
    [InlineData("Upper.json", false)]
    [InlineData("x.json/../../y.json", false)]
    [InlineData("x\\y.json", false)]
    [InlineData("x.txt", false)]
    [InlineData("_obsolete.json", false)]
    [InlineData("_obsolete/ingress-app.json", false)]
    public void Catalog_paths_must_be_sibling_basenames(string path, bool ok) => Assert.Equal(ok, ArchitecturePaths.IsSafeBasename(path));

    // ---- conceptual vs live (ADDENDA-011 §7: never injected into FlowGraph nodes, AllNodeIds or activity joins) ----

    private static FlowGraph Graph(ProjectRegistration r, IReadOnlyDictionary<string, EntityCounts>? depths = null) =>
        FlowGraphBuilder.Build(r, FlowScope.All, "LR", SbScope.Local, depths ?? new Dictionary<string, EntityCounts>(), new Dictionary<string, FlowPosition>(), ["SB_UNREACHABLE: x"]);

    private static Dictionary<string, string> HandlerOpeners(FlowGraph g) =>
        g.Nodes.Where(n => n.OpensLayerId is not null).ToDictionary(n => n.Id, n => n.OpensLayerId!);

    [Fact]
    public void Linking_adds_only_handler_opens_layer_id_and_never_architecture_ids()
    {
        var state = LoadReal();
        var depths = new Dictionary<string, EntityCounts> { ["q:customerproject-ingress"] = new(5, 2, "peek"), ["q:customerproject-snapshot"] = new(1, 0, "peek") };
        var before = Graph(LayerTree.Registration, depths);
        var after = ArchitectureService.Link(before, state);

        // ADDENDA-011.1: the five handler items open their roots; containers never do (the DTO has no such field).
        Assert.Equal(CustomerprojectHandlerRoots, HandlerOpeners(after));
        Assert.All(after.Nodes.Where(n => n.OpensLayerId is not null), n => Assert.Equal("handler", n.Kind));
        Assert.Equal(before.Containers, after.Containers);
        // Nested layers are never Flow targets: only roots are linked.
        Assert.DoesNotContain(after.Nodes, n => n.OpensLayerId == DomainUpdateLayer);

        // Byte-identical once the additive field is removed: schemaVersion, appIds, positions, edges, sb counts, warnings untouched.
        AssertOnlyOpensLayerIdAdded(before, after);

        var flowIds = after.Nodes.Select(n => n.Id).Concat(after.Containers.Select(c => c.Id)).Concat(after.Edges.Select(e => e.Id)).ToList();
        Assert.DoesNotContain(flowIds, id => id.StartsWith("comp:") || id.StartsWith("aedge:") || id.StartsWith("layer:"));
        Assert.DoesNotContain(FlowGraphBuilder.AllNodeIds(LayerTree.Registration), id => id.StartsWith("comp:") || id.StartsWith("layer:"));
    }

    [Fact]
    public void Price_flow_links_the_eight_root_handlers_only()
    {
        var state = LoadPrice();
        var before = Graph(LayerTree.PriceRegistration);
        var after = ArchitectureService.Link(before, state);
        Assert.Equal(PriceHandlerRoots, HandlerOpeners(after));
        // Intentionally no affordance: subscriptions, the five sub-* handlers and the four timers (deferred / no-layer).
        Assert.All(PriceHandlersWithoutRoot, id => Assert.Null(after.Nodes.Single(n => n.Id == id).OpensLayerId));
        Assert.Equal(before.Containers, after.Containers);
        Assert.DoesNotContain(after.Nodes, n => n.OpensLayerId == "layer:price.api.get-prices");
        AssertOnlyOpensLayerIdAdded(before, after);
        Assert.DoesNotContain(FlowGraphBuilder.AllNodeIds(LayerTree.PriceRegistration), id => id.StartsWith("comp:") || id.StartsWith("layer:"));
    }

    [Fact]
    public void Flow_container_dto_never_carries_opens_layer_id()
    {
        Assert.Null(typeof(FlowContainer).GetProperty("OpensLayerId"));
        foreach (var (registration, state) in new[] { (LayerTree.Registration, LoadReal()), (LayerTree.PriceRegistration, LoadPrice()) })
        {
            var json = JsonNode.Parse(JsonSerializer.Serialize(ArchitectureService.Link(Graph(registration), state), RegistrationJson.Options))!;
            Assert.All(json["containers"]!.AsArray(), c => Assert.False(c!.AsObject().ContainsKey("opensLayerId")));
            Assert.Contains(json["nodes"]!.AsArray(), n => n!.AsObject().ContainsKey("opensLayerId"));
        }
    }

    private static void AssertOnlyOpensLayerIdAdded(FlowGraph before, FlowGraph after)
    {
        var a = JsonNode.Parse(JsonSerializer.Serialize(after, RegistrationJson.Options))!;
        foreach (var n in a["nodes"]!.AsArray()) n!.AsObject().Remove("opensLayerId");
        Assert.Equal(JsonSerializer.Serialize(before, RegistrationJson.Options), a.ToJsonString());
    }

    [Fact]
    public void Architecture_nodes_carry_no_live_fields_even_with_flow_entity_refs()
    {
        string[] live = ["sb", "activeCount", "deadLetterCount", "dlqHot", "activity", "position", "positionSource", "state"];
        foreach (var doc in LoadReal().Catalog!.Layers.Values.Concat(LoadPrice().Catalog!.Layers.Values).Select(l => l.Document))
            foreach (var item in doc.GetProperty("nodes").EnumerateArray().Concat(doc.GetProperty("edges").EnumerateArray()))
                Assert.DoesNotContain(item.EnumerateObject(), p => live.Contains(p.Name));
        var layer = LoadReal().Catalog!.Layers[LayerTree.RootLayer].Document;
        // flowEntityId is a reference for "View in Flow", not a join: the node keeps exactly its authored fields.
        var input = layer.GetProperty("nodes").EnumerateArray().Single(n => n.GetProperty("id").GetString() == "comp:customerproject.ingress.input");
        Assert.Equal("q:customerproject-ingress", input.GetProperty("flowEntityId").GetString());
        Assert.Equal(["boundary", "flowEntityId", "id", "kind", "label", "owner", "responsibility", "sources", "uncertainties"],
            input.EnumerateObject().Select(p => p.Name).Order());
    }

    [Fact]
    public void Invalid_or_absent_architecture_leaves_the_graph_alone_except_a_diagnostic()
    {
        var before = Graph(LayerTree.Registration);
        Assert.Same(before, ArchitectureService.Link(before, ArchitectureState.Absent));
        var invalid = ArchitectureService.Link(before, ArchitectureState.Invalid(["V-004: x"]));
        Assert.Equal(before.Containers, invalid.Containers);
        Assert.Equal(before.Nodes, invalid.Nodes);
        Assert.Equal([.. before.Warnings, "ARCHITECTURE_INVALID: Architecture catalog rejected; drill-down disabled, Flow unaffected. V-004: x"], invalid.Warnings);
    }

    // ---- V-009 compatibility ----

    [Fact]
    public void Registration_bytes_are_unchanged_and_carry_no_architecture_fields()
    {
        Assert.Equal("5ccc19c9751934139e195b0c50121ce1b95af6fea5a3d934fc5de6308571f39a", Sha256(RepoPaths.Registration));
        Assert.Equal("ec39d09048cdbbc9236c6ffeaa7ec4e3f1fd6536bf9ca9bac8785c3d2daa8046", Sha256(RepoPaths.PriceRegistration));
        Assert.DoesNotContain("opensLayerId", File.ReadAllText(RepoPaths.Registration));
        Assert.DoesNotContain("opensLayerId", File.ReadAllText(RepoPaths.Schema));
        var layerHashes = Directory.GetFiles(RepoPaths.ArchitectureLayers, "*.json", SearchOption.AllDirectories).ToDictionary(f => f, Sha256);
        LoadReal();
        LoadPrice();
        Assert.All(layerHashes, kv => Assert.Equal(kv.Value, Sha256(kv.Key))); // GET-side loading never writes or repairs
    }

    [Fact]
    public void Registration_container_or_handler_carrying_opens_layer_id_is_rejected()
    {
        // ADDENDA-011.1 V-011: Explore links come from the sidecar only; the registration schema has no opensLayerId anywhere.
        var dir = Directory.CreateTempSubdirectory("dsp-arch-reg-").FullName;
        try
        {
            foreach (var kind in new[] { "containers", "handlers" })
            {
                var reg = JsonNode.Parse(File.ReadAllText(RepoPaths.Registration))!;
                reg[kind]![0]!["opensLayerId"] = LayerTree.RootLayer;
                var path = Path.Combine(dir, $"{kind}.json");
                File.WriteAllText(path, reg.ToJsonString());
                Assert.Throws<RegistrationValidationException>(() => new RegistrationLoader(RepoPaths.Schema).Load(path));
            }
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    private static string Sha256(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
}
