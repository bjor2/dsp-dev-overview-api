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
    public const string RootLayer = "layer:customerproject.ingress-app";
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

    /// <summary>The full checked-in customerproject catalog (3 roots, one nested child).</summary>
    public static LayerTree Real(string? layersRoot = null) => FromRepo("customerproject", Registration, layersRoot);

    /// <summary>The full checked-in price catalog (4 roots, one nested child), validated against price.dev.</summary>
    public static LayerTree PriceReal(string? layersRoot = null) => FromRepo("price", PriceRegistration, layersRoot);

    /// <summary>
    /// Synthetic forest built from the ingress worked example only: the ingress root is the sole catalog entry, followed by a
    /// chain of children (depth 2..<paramref name="depth"/>), each opened by its parent's inside import component (node 1).
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

    private const string FuncLayer = "layer:customerproject.func-app";
    private const string DomainUpdateLayer = "layer:customerproject.func-app.domain-update";
    private const string ApiLayer = "layer:customerproject.api";

    [Fact]
    public void Real_customerproject_catalog_has_three_roots_and_a_nested_domain_update_child()
    {
        var state = LoadReal();
        Assert.True(state.Status == ArchitectureStatus.Valid, string.Join("\n", state.Issues));
        Assert.Empty(state.Issues);
        var c = state.Catalog!;
        Assert.Equal([ApiLayer, FuncLayer, LayerTree.RootLayer], c.Roots.Select(r => r.LayerId)); // ordinal by layerId
        Assert.All(c.Roots, r => Assert.Null(r.FlowRef.HandlerId));
        Assert.Equal(4, c.Layers.Count);

        // Ingress worked example is unchanged: 7 nodes, 9 conceptual edges, root crumb only, no children.
        var root = c.Roots.Single(r => r.LayerId == LayerTree.RootLayer);
        Assert.Equal((LayerTree.IngressContainer, "Ingress — import and snapshot responsibilities"), (root.FlowRef.ContainerId, root.Title));
        var layer = c.Layers[LayerTree.RootLayer];
        Assert.Equal(7, layer.Document.GetProperty("nodes").GetArrayLength());
        Assert.Equal(9, layer.Document.GetProperty("edges").GetArrayLength());
        Assert.False(layer.Document.TryGetProperty("$schema", out _)); // relative file path is not served
        Assert.Equal(new[] { new ArchitectureCrumb(LayerTree.RootLayer, root.Title) }, layer.Breadcrumbs);
        Assert.Equal(LayerTree.IngressContainer, layer.RootFlowRef.ContainerId);
        Assert.Empty(layer.Children);

        // func-app opens domain-update from its snapshot-apply component; the child inherits the func-app Flow anchor.
        var func = c.Layers[FuncLayer];
        Assert.Equal(new[] { new ArchitectureChild(DomainUpdateLayer, "DOMAIN — applying ingress snapshots", "comp:customerproject.func.snapshot-apply") }, func.Children);
        var nested = c.Layers[DomainUpdateLayer];
        Assert.Equal([FuncLayer, DomainUpdateLayer], nested.Breadcrumbs.Select(b => b.LayerId));
        Assert.Equal(func.Breadcrumbs, nested.Breadcrumbs.Take(1));
        Assert.Equal(new ArchitectureFlowRef("container:customerproject.func-app"), nested.RootFlowRef);
        Assert.False(nested.Document.TryGetProperty("flowRef", out _)); // inherited, not authored
        Assert.Empty(nested.Children);
        Assert.Empty(c.Layers[ApiLayer].Children);

        Assert.All(c.Layers.Values.Select(l => l.Document), d =>
            Assert.All(d.GetProperty("edges").EnumerateArray(), e => Assert.Equal("conceptual", e.GetProperty("kind").GetString())));
        Assert.Equal(new Dictionary<string, string>
        {
            ["container:customerproject.api"] = ApiLayer,
            ["container:customerproject.func-app"] = FuncLayer,
            [LayerTree.IngressContainer] = LayerTree.RootLayer,
        }, c.ContainerLinks);
        Assert.Empty(c.HandlerLinks);
    }

    [Fact]
    public void Real_price_catalog_has_four_roots_and_a_nested_get_prices_child()
    {
        var state = LoadPrice();
        Assert.True(state.Status == ArchitectureStatus.Valid, string.Join("\n", state.Issues));
        Assert.Empty(state.Warnings);
        var c = state.Catalog!;
        Assert.Equal("price.dev", c.RegistrationId);
        Assert.Equal(["layer:price.api", "layer:price.api-batch", "layer:price.func-app", "layer:price.ingress-app"], c.Roots.Select(r => r.LayerId));
        Assert.Equal(5, c.Layers.Count);

        var api = c.Layers["layer:price.api"];
        Assert.Equal(new[] { new ArchitectureChild("layer:price.api.get-prices", "Price reading — strategy, DSP calculation and M3 fallback", "comp:price.api.price-reader") }, api.Children);
        var nested = c.Layers["layer:price.api.get-prices"];
        Assert.Equal(["layer:price.api", "layer:price.api.get-prices"], nested.Breadcrumbs.Select(b => b.LayerId));
        Assert.Equal(new ArchitectureFlowRef("container:price.api"), nested.RootFlowRef);
        Assert.False(nested.Document.TryGetProperty("flowRef", out _));

        // Deliberately thin, high-uncertainty layer still loads and keeps its caveats visible.
        var batch = c.Layers["layer:price.api-batch"].Document;
        Assert.Equal((5, 4), (batch.GetProperty("nodes").GetArrayLength(), batch.GetProperty("edges").GetArrayLength()));
        Assert.NotEqual(0, batch.GetProperty("uncertainties").GetArrayLength());

        Assert.Equal(LayerTree.PriceRegistration.Containers.Select(x => x.Id).Order(), c.ContainerLinks.Keys.Order());
        Assert.All(c.ContainerLinks, kv => Assert.Equal(kv.Key.Replace("container:", "layer:"), kv.Value));
        Assert.Empty(c.HandlerLinks);
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
        Assert.Equal(4, p.Catalog!.Roots.Count);
    }

    [Fact]
    public void Real_catalogs_have_no_bidirectional_boundary_node()
    {
        foreach (var state in new[] { LoadReal(), LoadPrice() })
            Assert.DoesNotContain(state.Issues, i => i.Contains("both receives from and sends to"));
    }

    [Fact]
    public void Missing_layers_root_or_project_dir_is_absent()
    {
        using var tree = LayerTree.Real(); // nothing written
        Assert.Equal(ArchitectureStatus.Absent, tree.Load().Status);
        Directory.CreateDirectory(tree.ProjectDir);
        Assert.Equal(ArchitectureStatus.Absent, tree.Load().Status);
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
        Assert.Equal(new ArchitectureFlowRef(LayerTree.IngressContainer), deepest.RootFlowRef);
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
        tree.Node(0, 4)["opensLayerId"] = sibling["id"]!.GetValue<string>(); // address-snapshots, inside component
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
        using var tree = LayerTree.Deep(3);
        // address-snapshots → import closes import → snapshot-requests → address-snapshots: an explanatory dependency cycle,
        // not a navigation cycle. (A two-edge cycle through one boundary node is the bidirectional V-007 rejection instead.)
        var back = tree.Edge(0, 1).DeepClone().AsObject();
        (back["id"], back["from"], back["to"], back["relationship"]) =
            ("aedge:customerproject.ingress.reread", "comp:customerproject.ingress.address-snapshots", "comp:customerproject.ingress.import", "dependency");
        tree.Layer(0)["edges"]!.AsArray().Add(back);
        var state = tree.WriteAndLoad();
        Assert.True(state.Status == ArchitectureStatus.Valid, string.Join("\n", state.Issues));
    }

    [Fact]
    public void Handler_bound_root_links_the_handler_not_the_container()
    {
        using var tree = LayerTree.Real();
        var handlerRoot = tree.Layer(0).DeepClone().AsObject();
        handlerRoot["id"] = "layer:customerproject.ingress-handler";
        handlerRoot["flowRef"] = new JsonObject { ["containerId"] = LayerTree.IngressContainer, ["handlerId"] = "customerproject.fn.ingress" };
        foreach (var n in handlerRoot["nodes"]!.AsArray()) n!["id"] = n["id"]!.GetValue<string>() + ".h";
        foreach (var e in handlerRoot["edges"]!.AsArray())
            foreach (var key in new[] { "id", "from", "to" }) e![key] = e[key]!.GetValue<string>() + ".h";
        tree.Catalog["layers"]!.AsArray().Add(new JsonObject { ["id"] = "layer:customerproject.ingress-handler", ["path"] = "ingress-handler.json" });
        tree.Layers.Add(handlerRoot);

        var state = tree.WriteAndLoad();
        Assert.True(state.Status == ArchitectureStatus.Valid, string.Join("\n", state.Issues));
        Assert.Equal(4, state.Catalog!.Roots.Count);
        Assert.Equal(3, state.Catalog.ContainerLinks.Count);
        Assert.Equal(new Dictionary<string, string> { ["customerproject.fn.ingress"] = "layer:customerproject.ingress-handler" }, state.Catalog.HandlerLinks);

        var g = ArchitectureService.Link(Graph(LayerTree.Registration), state);
        Assert.Equal("layer:customerproject.ingress-handler", g.Nodes.Single(n => n.Id == "customerproject.fn.ingress").OpensLayerId);
        Assert.Null(g.Nodes.Single(n => n.Id == "customerproject.fn.snapshot").OpensLayerId);
        Assert.Equal(LayerTree.RootLayer, g.Containers.Single(c => c.Id == LayerTree.IngressContainer).OpensLayerId);
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
        ["duplicate opener"] = t => t.Node(0, 4)["opensLayerId"] = t.Layer(1)["id"]!.GetValue<string>(),
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
        ["duplicate root for one flow source"] = t => { t.Layer(1)["parentLayerId"] = null; t.Layer(1)["flowRef"] = new JsonObject { ["containerId"] = LayerTree.IngressContainer }; t.Node(0, 1).Remove("opensLayerId"); },
        ["descendant with flowRef"] = t => t.Layer(1)["flowRef"] = new JsonObject { ["containerId"] = LayerTree.IngressContainer },
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
            File.Copy(Path.Combine(elsewhere, "ingress-app.json"), Path.Combine(tree.ProjectDir, "ingress-app.json"));
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
    public void Catalog_paths_must_be_sibling_basenames(string path, bool ok) => Assert.Equal(ok, ArchitecturePaths.IsSafeBasename(path));

    // ---- conceptual vs live (ADDENDA-011 §7: never injected into FlowGraph nodes, AllNodeIds or activity joins) ----

    private static FlowGraph Graph(ProjectRegistration r, IReadOnlyDictionary<string, EntityCounts>? depths = null) =>
        FlowGraphBuilder.Build(r, FlowScope.All, "LR", SbScope.Local, depths ?? new Dictionary<string, EntityCounts>(), new Dictionary<string, FlowPosition>(), ["SB_UNREACHABLE: x"]);

    [Fact]
    public void Linking_adds_only_opens_layer_id_and_never_architecture_ids()
    {
        var state = LoadReal();
        var depths = new Dictionary<string, EntityCounts> { ["q:customerproject-ingress"] = new(5, 2, "peek"), ["q:customerproject-snapshot"] = new(1, 0, "peek") };
        var before = Graph(LayerTree.Registration, depths);
        var after = ArchitectureService.Link(before, state);

        Assert.Equal(new Dictionary<string, string?>
        {
            ["container:customerproject.api"] = ApiLayer,
            ["container:customerproject.func-app"] = FuncLayer,
            [LayerTree.IngressContainer] = LayerTree.RootLayer,
        }, after.Containers.ToDictionary(c => c.Id, c => c.OpensLayerId));
        Assert.All(after.Nodes, n => Assert.Null(n.OpensLayerId));
        // Nested layers are never Flow targets: only roots are linked.
        Assert.DoesNotContain(after.Containers, c => c.OpensLayerId == DomainUpdateLayer);

        // Byte-identical once the additive field is removed: schemaVersion, appIds, positions, edges, sb counts, warnings untouched.
        AssertOnlyOpensLayerIdAdded(before, after);

        var flowIds = after.Nodes.Select(n => n.Id).Concat(after.Containers.Select(c => c.Id)).Concat(after.Edges.Select(e => e.Id)).ToList();
        Assert.DoesNotContain(flowIds, id => id.StartsWith("comp:") || id.StartsWith("aedge:") || id.StartsWith("layer:"));
        Assert.DoesNotContain(FlowGraphBuilder.AllNodeIds(LayerTree.Registration), id => id.StartsWith("comp:") || id.StartsWith("layer:"));
    }

    [Fact]
    public void Price_flow_links_every_container_with_a_root_and_nothing_else()
    {
        var state = LoadPrice();
        var before = Graph(LayerTree.PriceRegistration);
        var after = ArchitectureService.Link(before, state);
        Assert.Equal(state.Catalog!.ContainerLinks.OrderBy(kv => kv.Key),
            after.Containers.Where(c => c.OpensLayerId is not null).Select(c => KeyValuePair.Create(c.Id, c.OpensLayerId!)).OrderBy(kv => kv.Key));
        Assert.Equal(4, after.Containers.Count(c => c.OpensLayerId is not null));
        Assert.All(after.Nodes, n => Assert.Null(n.OpensLayerId));
        Assert.DoesNotContain(after.Containers, c => c.OpensLayerId == "layer:price.api.get-prices");
        AssertOnlyOpensLayerIdAdded(before, after);
        Assert.DoesNotContain(FlowGraphBuilder.AllNodeIds(LayerTree.PriceRegistration), id => id.StartsWith("comp:") || id.StartsWith("layer:"));
    }

    private static void AssertOnlyOpensLayerIdAdded(FlowGraph before, FlowGraph after)
    {
        var a = JsonNode.Parse(JsonSerializer.Serialize(after, RegistrationJson.Options))!;
        foreach (var c in a["containers"]!.AsArray()) c!.AsObject().Remove("opensLayerId");
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

    private static string Sha256(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
}
