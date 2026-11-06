using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Dsp.DevOverview.Api.Tests;

/// <summary>HTTP: architecture index/layer endpoints and the additive Flow opensLayerId, for valid, absent and invalid catalogs. No network.</summary>
public sealed class ArchitectureApiTests : IDisposable
{
    private const string P = "/api/projects/customerproject.dev";
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "dsp-arch-api-" + Guid.NewGuid().ToString("N"));
    private readonly List<IDisposable> _owned = [];

    /// <summary><paramref name="layersDir"/> null = the checked-in architecture/layers (default config).</summary>
    private HttpClient Client(string? layersDir = null)
    {
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Production"); // appsettings.json: no Service Bus
            b.UseSetting("Overview:DataDir", _dataDir);
            b.UseSetting("Overview:AppInsights:ProbeEnabled", "false");
            if (layersDir is not null) b.UseSetting("Overview:ArchitectureLayersDir", layersDir);
        });
        _owned.Add(factory);
        return factory.CreateClient();
    }

    private LayerTree Tree(LayerTree tree)
    {
        _owned.Add(tree);
        return tree;
    }

    public void Dispose()
    {
        foreach (var d in _owned) d.Dispose();
        if (Directory.Exists(_dataDir)) Directory.Delete(_dataDir, recursive: true);
    }

    private static async Task<(HttpStatusCode Status, JsonElement Body, string Raw)> Get(HttpClient http, string url)
    {
        var r = await http.GetAsync(url);
        var raw = await r.Content.ReadAsStringAsync();
        return (r.StatusCode, JsonDocument.Parse(raw).RootElement, raw);
    }

    private const string Price = "/api/projects/price.dev";
    private const string DomainLayer = "layer:customerproject.fn.domain";
    private const string DomainUpdateLayer = "layer:customerproject.func-app.domain-update";
    private const string ApiLayer = "layer:customerproject.api";

    private static string Layer(string layerId, string project = P) => $"{project}/architecture/layer?layerId={Uri.EscapeDataString(layerId)}";

    private static string[] Ids(JsonElement array, string property = "layerId") =>
        array.EnumerateArray().Select(x => x.GetProperty(property).GetString()!).ToArray();

    /// <summary>Flow handler node id → opensLayerId, for every node that carries one.</summary>
    private static Dictionary<string, string> Openers(JsonElement flow) =>
        flow.GetProperty("nodes").EnumerateArray().Where(n => n.TryGetProperty("opensLayerId", out _))
            .ToDictionary(n => n.GetProperty("id").GetString()!, n => n.GetProperty("opensLayerId").GetString()!);

    /// <summary>ADDENDA-011.1: Explore is on handler items only; containers never carry opensLayerId and only handler nodes do.</summary>
    private static void AssertHandlerOnlyOpeners(JsonElement flow, IReadOnlyDictionary<string, string> expected)
    {
        Assert.Equal(expected.OrderBy(kv => kv.Key), Openers(flow).OrderBy(kv => kv.Key));
        Assert.All(flow.GetProperty("nodes").EnumerateArray().Where(n => n.TryGetProperty("opensLayerId", out _)),
            n => Assert.Equal("handler", n.GetProperty("kind").GetString()));
        Assert.All(flow.GetProperty("containers").EnumerateArray(), c => Assert.False(c.TryGetProperty("opensLayerId", out _)));
    }

    private static void AssertNoMachinePaths(string raw, params string[] extra)
    {
        Assert.DoesNotContain(RepoPaths.Root, raw);
        Assert.DoesNotContain(Path.GetTempPath().TrimEnd('/'), raw);
        Assert.DoesNotContain("/workspace/", raw);
        Assert.DoesNotContain("   at ", raw); // stack frames
        foreach (var e in extra) Assert.DoesNotContain(e, raw);
    }

    /// <summary>Base Flow shape (FlowApiTests) regardless of architecture state.</summary>
    private static async Task AssertBaseFlow(HttpClient http)
    {
        var (status, flow, _) = await Get(http, $"{P}/flow");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(2, flow.GetProperty("schemaVersion").GetInt32());
        Assert.Equal((14, 3, 15), (flow.GetProperty("nodes").GetArrayLength(), flow.GetProperty("containers").GetArrayLength(), flow.GetProperty("edges").GetArrayLength()));
        Assert.Equal(["app:api", "app:functions", "app:ingress"],
            flow.GetProperty("containers").EnumerateArray().Select(c => c.GetProperty("appId").GetString()).Order());
        Assert.Contains(flow.GetProperty("warnings").EnumerateArray(), w => w.GetString()!.StartsWith("SB_UNREACHABLE"));
        Assert.Equal("stored", flow.GetProperty("layout").GetString());
    }

    [Fact]
    public async Task Real_catalog_serves_index_layer_and_flow_link()
    {
        var http = Client();

        var (status, index, raw) = await Get(http, $"{P}/architecture");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(("customerproject.dev", 1), (index.GetProperty("projectId").GetString(), index.GetProperty("schemaVersion").GetInt32()));
        Assert.Equal(ArchitectureLoaderTests.CustomerprojectHandlerRoots.Values.Order(StringComparer.Ordinal), Ids(index.GetProperty("roots")));
        // Every root's flowRef names its handler item (always serialized) and that handler's container.
        Assert.All(index.GetProperty("roots").EnumerateArray(), r => Assert.Equal(
            ArchitectureLoaderTests.CustomerprojectHandlerRoots[r.GetProperty("flowRef").GetProperty("handlerId").GetString()!], r.GetProperty("layerId").GetString()));
        var root = index.GetProperty("roots").EnumerateArray().Single(r => r.GetProperty("layerId").GetString() == LayerTree.RootLayer);
        Assert.Equal("INGRESS — importing M3 change events", root.GetProperty("title").GetString());
        Assert.Equal(LayerTree.IngressContainer, root.GetProperty("flowRef").GetProperty("containerId").GetString());
        Assert.Equal(LayerTree.RootHandler, root.GetProperty("flowRef").GetProperty("handlerId").GetString());
        Assert.Equal(["containerId", "handlerId"], root.GetProperty("flowRef").EnumerateObject().Select(p => p.Name));
        Assert.Equal(0, index.GetProperty("warnings").GetArrayLength());
        Assert.Equal(["catalogHash", "projectId", "roots", "schemaVersion", "warnings"], index.EnumerateObject().Select(p => p.Name).Order());
        Assert.Matches("^sha256:[0-9a-f]{64}$", index.GetProperty("catalogHash").GetString());
        AssertNoMachinePaths(raw);

        (status, var layer, raw) = await Get(http, Layer(LayerTree.RootLayer));
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(["breadcrumbs", "children", "layer", "projectId", "rootFlowRef", "schemaVersion", "warnings"],
            layer.EnumerateObject().Select(p => p.Name).Order());
        var doc = layer.GetProperty("layer");
        Assert.Equal((LayerTree.RootLayer, 4, 3), (doc.GetProperty("id").GetString(), doc.GetProperty("nodes").GetArrayLength(), doc.GetProperty("edges").GetArrayLength()));
        Assert.False(doc.TryGetProperty("$schema", out _));
        Assert.Equal(LayerTree.IngressContainer, layer.GetProperty("rootFlowRef").GetProperty("containerId").GetString());
        Assert.Equal(LayerTree.RootHandler, layer.GetProperty("rootFlowRef").GetProperty("handlerId").GetString());
        var crumb = Assert.Single(layer.GetProperty("breadcrumbs").EnumerateArray());
        Assert.Equal((LayerTree.RootLayer, root.GetProperty("title").GetString()), (crumb.GetProperty("layerId").GetString(), crumb.GetProperty("title").GetString()));
        Assert.Equal(0, layer.GetProperty("children").GetArrayLength());
        AssertNoMachinePaths(raw, "../../architecture-layer.schema.json");

        // Raw (unencoded) id works too: ':' is legal in a query value.
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync($"{P}/architecture/layer?layerId={LayerTree.RootLayer}")).StatusCode);

        (status, var flow, _) = await Get(http, $"{P}/flow");
        Assert.Equal(HttpStatusCode.OK, status);
        AssertHandlerOnlyOpeners(flow, ArchitectureLoaderTests.CustomerprojectHandlerRoots);
        await AssertBaseFlow(http);
    }

    [Fact]
    public async Task Real_customerproject_domain_opens_nested_domain_update_with_inherited_root_flow_ref()
    {
        var http = Client();
        var (status, func, _) = await Get(http, Layer(DomainLayer));
        Assert.Equal(HttpStatusCode.OK, status);
        var child = Assert.Single(func.GetProperty("children").EnumerateArray());
        Assert.Equal((DomainUpdateLayer, "comp:customerproject.func.snapshot-apply"),
            (child.GetProperty("layerId").GetString(), child.GetProperty("openedByNodeId").GetString()));
        Assert.Contains(func.GetProperty("layer").GetProperty("nodes").EnumerateArray(),
            n => n.GetProperty("id").GetString() == "comp:customerproject.func.snapshot-apply" && n.GetProperty("opensLayerId").GetString() == DomainUpdateLayer);

        (status, var nested, var raw) = await Get(http, Layer(DomainUpdateLayer));
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal([DomainLayer, DomainUpdateLayer], Ids(nested.GetProperty("breadcrumbs")));
        Assert.Equal("container:customerproject.func-app", nested.GetProperty("rootFlowRef").GetProperty("containerId").GetString());
        Assert.Equal("customerproject.fn.domain", nested.GetProperty("rootFlowRef").GetProperty("handlerId").GetString());
        Assert.False(nested.GetProperty("layer").TryGetProperty("flowRef", out _));
        Assert.Equal(0, nested.GetProperty("children").GetArrayLength());
        AssertNoMachinePaths(raw);

        // The nested layer is not a root and nothing in Flow opens it directly.
        var (_, index, _) = await Get(http, $"{P}/architecture");
        Assert.DoesNotContain(DomainUpdateLayer, Ids(index.GetProperty("roots")));
        Assert.DoesNotContain(DomainUpdateLayer, await http.GetStringAsync($"{P}/flow?scope=all"));
    }

    [Fact]
    public async Task Real_price_catalog_serves_eight_handler_roots_nested_get_prices_and_handler_flow_links()
    {
        var http = Client();
        var (status, index, raw) = await Get(http, $"{Price}/architecture");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("price.dev", index.GetProperty("projectId").GetString());
        Assert.Equal(ArchitectureLoaderTests.PriceHandlerRoots.Values.Order(StringComparer.Ordinal), Ids(index.GetProperty("roots")));
        Assert.All(index.GetProperty("roots").EnumerateArray(), r => Assert.Equal(
            ArchitectureLoaderTests.PriceHandlerRoots[r.GetProperty("flowRef").GetProperty("handlerId").GetString()!], r.GetProperty("layerId").GetString()));
        Assert.Equal(0, index.GetProperty("warnings").GetArrayLength());
        Assert.Matches("^sha256:[0-9a-f]{64}$", index.GetProperty("catalogHash").GetString());
        var (_, customerproject, _) = await Get(http, $"{P}/architecture");
        Assert.NotEqual(customerproject.GetProperty("catalogHash").GetString(), index.GetProperty("catalogHash").GetString());
        AssertNoMachinePaths(raw);

        (status, var nested, raw) = await Get(http, Layer("layer:price.api.get-prices", Price));
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(["layer:price.api", "layer:price.api.get-prices"], Ids(nested.GetProperty("breadcrumbs")));
        Assert.Equal("container:price.api", nested.GetProperty("rootFlowRef").GetProperty("containerId").GetString());
        Assert.Equal("price.api.http", nested.GetProperty("rootFlowRef").GetProperty("handlerId").GetString());
        AssertNoMachinePaths(raw);
        var (_, api, _) = await Get(http, Layer("layer:price.api", Price));
        var child = Assert.Single(api.GetProperty("children").EnumerateArray());
        Assert.Equal(("layer:price.api.get-prices", "comp:price.api.price-reader"),
            (child.GetProperty("layerId").GetString(), child.GetProperty("openedByNodeId").GetString()));
        var calcChild = Assert.Single(nested.GetProperty("children").EnumerateArray());
        Assert.Equal(("layer:price.api.get-prices.calculator", "comp:price.get-prices.calculator"),
            (calcChild.GetProperty("layerId").GetString(), calcChild.GetProperty("openedByNodeId").GetString()));
        (status, var calculator, var rawCalc) = await Get(http, Layer("layer:price.api.get-prices.calculator", Price));
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(["layer:price.api", "layer:price.api.get-prices", "layer:price.api.get-prices.calculator"], Ids(calculator.GetProperty("breadcrumbs")));
        Assert.Equal("price.api.http", calculator.GetProperty("rootFlowRef").GetProperty("handlerId").GetString());
        Assert.Equal(0, calculator.GetProperty("children").GetArrayLength());
        AssertNoMachinePaths(rawCalc);

        (status, var batch, _) = await Get(http, Layer("layer:price.api-batch", Price));
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.NotEqual(0, batch.GetProperty("layer").GetProperty("uncertainties").GetArrayLength());

        (status, var flow, _) = await Get(http, $"{Price}/flow?scope=all");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(4, flow.GetProperty("containers").GetArrayLength());
        AssertHandlerOnlyOpeners(flow, ArchitectureLoaderTests.PriceHandlerRoots);
        // Deferred (subscriptions, sub-*) and no-layer (timers) handlers are in the Flow but show no Explore affordance.
        var nodeIds = flow.GetProperty("nodes").EnumerateArray().Select(n => n.GetProperty("id").GetString()).ToHashSet();
        Assert.All(ArchitectureLoaderTests.PriceHandlersWithoutRoot, id => Assert.Contains(id, nodeIds));
        Assert.DoesNotContain(Openers(flow).Keys, ArchitectureLoaderTests.PriceHandlersWithoutRoot.Contains);
        Assert.DoesNotContain(flow.GetProperty("warnings").EnumerateArray(), w => w.GetString()!.StartsWith("ARCHITECTURE"));

        // Cross-project ids never resolve: a real price id on the customerproject route is 404, and vice versa.
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync(Layer("layer:price.api"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync(Layer(DomainLayer, Price))).StatusCode);
    }

    [Fact]
    public async Task Project_without_catalog_has_empty_roots_and_no_flow_links()
    {
        var tree = Tree(LayerTree.Real()); // layers root with a customerproject catalog only: no price folder
        tree.Write();
        var http = Client(tree.LayersRoot);
        var (status, index, _) = await Get(http, "/api/projects/price.dev/architecture");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal((0, 0), (index.GetProperty("roots").GetArrayLength(), index.GetProperty("warnings").GetArrayLength()));
        Assert.False(index.TryGetProperty("catalogHash", out _));

        (status, var err, _) = await Get(http, $"/api/projects/price.dev/architecture/layer?layerId={Uri.EscapeDataString("layer:price.api")}");
        Assert.Equal(HttpStatusCode.NotFound, status);
        Assert.Equal("ARCHITECTURE_LAYER_NOT_FOUND", err.GetProperty("error").GetProperty("code").GetString());

        (status, var flow, var raw) = await Get(http, "/api/projects/price.dev/flow?scope=all");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.DoesNotContain("opensLayerId", raw);
        Assert.DoesNotContain(flow.GetProperty("warnings").EnumerateArray(), w => w.GetString()!.StartsWith("ARCHITECTURE"));
    }

    [Fact]
    public async Task Unknown_project_layer_or_path_like_ids_are_404_shaped_like_the_api()
    {
        var http = Client();
        foreach (var url in new[] { "/api/projects/nope/architecture", "/api/projects/nope/architecture/layer?layerId=x" })
        {
            var (status, err, raw) = await Get(http, url);
            Assert.Equal(HttpStatusCode.NotFound, status);
            Assert.Equal("PROJECT_NOT_FOUND", err.GetProperty("error").GetProperty("code").GetString());
            Assert.False(err.GetProperty("error").GetProperty("retryable").GetBoolean());
            AssertNoMachinePaths(raw);
        }

        // Ids are looked up in the validated catalog only: file names, paths and traversal never reach the file system.
        string[] probes = ["layer:customerproject.missing", "ingress-app.json", "index.json", "../registrations/customerproject.dev.json",
            "../../registrations/customerproject.dev.json", "/etc/passwd", RepoPaths.Registration, "layer:customerproject.ingress-app/../index",
            "layer:price.ingress-app", "LAYER:CUSTOMERPROJECT.FN.INGRESS",
            // ADDENDA-011.1 retired container roots: ignored, never remapped onto the new handler roots.
            "layer:customerproject.ingress-app", "layer:customerproject.func-app", "_obsolete/ingress-app.json", "fn.ingress.json"];
        foreach (var id in probes)
        {
            var (status, err, raw) = await Get(http, Layer(id));
            Assert.Equal(HttpStatusCode.NotFound, status);
            Assert.Equal("ARCHITECTURE_LAYER_NOT_FOUND", err.GetProperty("error").GetProperty("code").GetString());
            Assert.DoesNotContain("\"nodes\"", raw);
            AssertNoMachinePaths(raw, id);
        }

        var (missing, body, _) = await Get(http, $"{P}/architecture/layer");
        Assert.Equal(HttpStatusCode.BadRequest, missing);
        Assert.Equal("LAYER_ID_REQUIRED", body.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Absent_catalog_keeps_flow_and_answers_empty_roots_and_layer_404()
    {
        var tree = Tree(LayerTree.Real()); // nothing written: the layers root does not even exist
        var http = Client(tree.LayersRoot);

        var (status, index, raw) = await Get(http, $"{P}/architecture");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal((0, 0), (index.GetProperty("roots").GetArrayLength(), index.GetProperty("warnings").GetArrayLength()));
        Assert.DoesNotContain("catalogHash", raw);
        (status, var err, _) = await Get(http, Layer(LayerTree.RootLayer));
        Assert.Equal(HttpStatusCode.NotFound, status);
        Assert.Equal("ARCHITECTURE_LAYER_NOT_FOUND", err.GetProperty("error").GetProperty("code").GetString());

        await AssertBaseFlow(http);
        (_, var flow, raw) = await Get(http, $"{P}/flow");
        Assert.DoesNotContain("opensLayerId", raw);
        Assert.DoesNotContain(flow.GetProperty("warnings").EnumerateArray(), w => w.GetString()!.StartsWith("ARCHITECTURE"));
    }

    [Fact]
    public async Task Invalid_catalog_keeps_flow_warns_on_index_and_503s_the_layer()
    {
        var tree = Tree(LayerTree.Deep(3));
        tree.Layer(1)["parentLayerId"] = tree.Layer(2)["id"]!.GetValue<string>(); // navigation cycle
        tree.Write();
        Tree(LayerTree.PriceReal(tree.LayersRoot)).Write();
        var http = Client(tree.LayersRoot);

        var (status, index, raw) = await Get(http, $"{P}/architecture");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(0, index.GetProperty("roots").GetArrayLength());
        var warning = Assert.Single(index.GetProperty("warnings").EnumerateArray()).GetString()!;
        Assert.StartsWith("ARCHITECTURE_INVALID: ", warning);
        Assert.Contains("V-004", warning);
        Assert.DoesNotContain("catalogHash", raw);
        AssertNoMachinePaths(raw, tree.LayersRoot);

        // Atomic: even the untouched root layer is not served from a rejected catalog.
        foreach (var id in new[] { LayerTree.RootLayer, tree.Layer(1)["id"]!.GetValue<string>(), "layer:customerproject.unknown" })
        {
            (status, var err, raw) = await Get(http, Layer(id));
            Assert.Equal(HttpStatusCode.ServiceUnavailable, status);
            Assert.Equal("ARCHITECTURE_INVALID", err.GetProperty("error").GetProperty("code").GetString());
            AssertNoMachinePaths(raw, tree.LayersRoot);
        }

        await AssertBaseFlow(http);
        (_, var flow, raw) = await Get(http, $"{P}/flow");
        Assert.DoesNotContain("opensLayerId", raw);
        Assert.Contains(flow.GetProperty("warnings").EnumerateArray(), w => w.GetString()!.StartsWith("ARCHITECTURE_INVALID: "));
        AssertNoMachinePaths(raw, tree.LayersRoot);

        // Other projects are unaffected: price has a valid catalog in the same layers root and keeps serving it.
        (status, var price, _) = await Get(http, $"{Price}/architecture");
        Assert.Equal((HttpStatusCode.OK, 0, 8), (status, price.GetProperty("warnings").GetArrayLength(), price.GetProperty("roots").GetArrayLength()));
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync(Layer("layer:price.api.get-prices", Price))).StatusCode);
        (_, var priceFlow, _) = await Get(http, $"{Price}/flow?scope=all");
        AssertHandlerOnlyOpeners(priceFlow, ArchitectureLoaderTests.PriceHandlerRoots);
    }

    [Fact]
    public async Task Container_only_root_rejects_the_catalog_and_flow_has_no_explore()
    {
        var tree = Tree(LayerTree.Real());
        tree.Layer(tree.LayerIndex(LayerTree.RootLayer))["flowRef"]!.AsObject().Remove("handlerId");
        tree.Write();
        var http = Client(tree.LayersRoot);

        var (status, index, raw) = await Get(http, $"{P}/architecture");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(0, index.GetProperty("roots").GetArrayLength());
        Assert.Contains("V-011: fn.ingress.json root has no flowRef.handlerId", Assert.Single(index.GetProperty("warnings").EnumerateArray()).GetString());
        AssertNoMachinePaths(raw, tree.LayersRoot);

        await AssertBaseFlow(http);
        (_, var flow, raw) = await Get(http, $"{P}/flow");
        Assert.DoesNotContain("opensLayerId", raw);
        Assert.Contains(flow.GetProperty("warnings").EnumerateArray(), w => w.GetString()!.StartsWith("ARCHITECTURE_INVALID: "));
    }

    [Fact]
    public async Task Unreadable_json_catalog_is_invalid_not_a_500()
    {
        var tree = Tree(LayerTree.Real());
        Directory.CreateDirectory(tree.ProjectDir);
        File.WriteAllText(Path.Combine(tree.ProjectDir, "index.json"), "{");
        var http = Client(tree.LayersRoot);
        var (status, index, raw) = await Get(http, $"{P}/architecture");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.StartsWith("ARCHITECTURE_INVALID", Assert.Single(index.GetProperty("warnings").EnumerateArray()).GetString());
        Assert.DoesNotContain("catalogHash", raw);
        AssertNoMachinePaths(raw, tree.LayersRoot);
        await AssertBaseFlow(http);
    }

    [Fact]
    public async Task Deep_tree_navigates_by_id_with_breadcrumbs_children_and_inherited_root_flow_ref()
    {
        var tree = Tree(LayerTree.Deep(4));
        tree.Write();
        var http = Client(tree.LayersRoot);

        var (_, root, _) = await Get(http, Layer(LayerTree.RootLayer));
        var child = Assert.Single(root.GetProperty("children").EnumerateArray());
        Assert.Equal(($"{LayerTree.RootLayer}.test2", "Depth 2", "comp:customerproject.ingress.import"),
            (child.GetProperty("layerId").GetString(), child.GetProperty("title").GetString(), child.GetProperty("openedByNodeId").GetString()));

        var (status, deepest, _) = await Get(http, Layer($"{LayerTree.RootLayer}.test4"));
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal([LayerTree.RootLayer, $"{LayerTree.RootLayer}.test2", $"{LayerTree.RootLayer}.test3", $"{LayerTree.RootLayer}.test4"],
            deepest.GetProperty("breadcrumbs").EnumerateArray().Select(b => b.GetProperty("layerId").GetString()));
        Assert.Equal(LayerTree.IngressContainer, deepest.GetProperty("rootFlowRef").GetProperty("containerId").GetString());
        Assert.Equal(0, deepest.GetProperty("children").GetArrayLength());
        Assert.False(deepest.GetProperty("layer").TryGetProperty("flowRef", out _)); // inherited, not authored

        // Only roots are listed by the index; descendants are reached via children.
        var (_, index, _) = await Get(http, $"{P}/architecture");
        Assert.Equal([LayerTree.RootLayer], index.GetProperty("roots").EnumerateArray().Select(r => r.GetProperty("layerId").GetString()));
    }

    [Fact]
    public async Task Architecture_ids_never_join_flow_positions_or_activity()
    {
        var http = Client();
        await http.PutAsJsonAsync($"{P}/flow/positions", new { });
        var before = await http.GetStringAsync($"{P}/flow/positions/export");

        // comp:/layer:/aedge: ids are not Flow ids: PUT ignores them (no reset, no write) and export is unchanged.
        var put = await http.PutAsJsonAsync($"{P}/flow/positions", new Dictionary<string, object>
        {
            ["comp:customerproject.ingress.import"] = new { x = 1, y = 2 }, [LayerTree.RootLayer] = new { x = 3, y = 4 },
            ["aedge:customerproject.ingress.changes"] = new { x = 5, y = 6 },
        });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.DoesNotContain("comp:", await put.Content.ReadAsStringAsync());
        Assert.Equal(before, await http.GetStringAsync($"{P}/flow/positions/export"));
        var store = Path.Combine(_dataDir, "flow-positions", "customerproject.dev.json");
        if (File.Exists(store)) Assert.DoesNotContain("comp:", File.ReadAllText(store));

        var flow = JsonNode.Parse(await http.GetStringAsync($"{P}/flow?scope=all"))!;
        var ids = flow["nodes"]!.AsArray().Concat(flow["containers"]!.AsArray()).Concat(flow["edges"]!.AsArray()).Select(n => n!["id"]!.GetValue<string>());
        Assert.DoesNotContain(ids, id => id.StartsWith("comp:") || id.StartsWith("aedge:") || id.StartsWith("layer:"));

        // The layer payload is static: no sb/count/activity/position fields joined onto conceptual nodes or edges.
        var layer = JsonNode.Parse(await http.GetStringAsync(Layer(LayerTree.RootLayer)))!["layer"]!;
        string[] live = ["sb", "activeCount", "deadLetterCount", "dlqHot", "activity", "position", "positionSource"];
        foreach (var item in layer["nodes"]!.AsArray().Concat(layer["edges"]!.AsArray()))
            Assert.DoesNotContain(item!.AsObject(), p => live.Contains(p.Key));
        var authored = JsonNode.Parse(File.ReadAllText(Path.Combine(RepoPaths.ArchitectureLayers, "customerproject", "fn.ingress.json")))!.AsObject();
        authored.Remove("$schema");
        Assert.True(JsonNode.DeepEquals(authored, layer), "served layer must be the authored document minus $schema");
    }
}
