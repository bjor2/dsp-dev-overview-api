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

    private static string Layer(string layerId) => $"{P}/architecture/layer?layerId={Uri.EscapeDataString(layerId)}";

    private static JsonElement Container(JsonElement flow, string id) =>
        flow.GetProperty("containers").EnumerateArray().Single(c => c.GetProperty("id").GetString() == id);

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
        var root = Assert.Single(index.GetProperty("roots").EnumerateArray());
        Assert.Equal(LayerTree.RootLayer, root.GetProperty("layerId").GetString());
        Assert.Equal("Ingress — import and snapshot responsibilities", root.GetProperty("title").GetString());
        Assert.Equal(LayerTree.IngressContainer, root.GetProperty("flowRef").GetProperty("containerId").GetString());
        Assert.False(root.GetProperty("flowRef").TryGetProperty("handlerId", out _));
        Assert.Equal(0, index.GetProperty("warnings").GetArrayLength());
        AssertNoMachinePaths(raw);

        (status, var layer, raw) = await Get(http, Layer(LayerTree.RootLayer));
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(["breadcrumbs", "children", "layer", "projectId", "rootFlowRef", "schemaVersion", "warnings"],
            layer.EnumerateObject().Select(p => p.Name).Order());
        var doc = layer.GetProperty("layer");
        Assert.Equal((LayerTree.RootLayer, 7, 9), (doc.GetProperty("id").GetString(), doc.GetProperty("nodes").GetArrayLength(), doc.GetProperty("edges").GetArrayLength()));
        Assert.False(doc.TryGetProperty("$schema", out _));
        Assert.Equal(LayerTree.IngressContainer, layer.GetProperty("rootFlowRef").GetProperty("containerId").GetString());
        var crumb = Assert.Single(layer.GetProperty("breadcrumbs").EnumerateArray());
        Assert.Equal((LayerTree.RootLayer, root.GetProperty("title").GetString()), (crumb.GetProperty("layerId").GetString(), crumb.GetProperty("title").GetString()));
        Assert.Equal(0, layer.GetProperty("children").GetArrayLength());
        AssertNoMachinePaths(raw, "../../architecture-layer.schema.json");

        // Raw (unencoded) id works too: ':' is legal in a query value.
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync($"{P}/architecture/layer?layerId={LayerTree.RootLayer}")).StatusCode);

        (status, var flow, _) = await Get(http, $"{P}/flow");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(LayerTree.RootLayer, Container(flow, LayerTree.IngressContainer).GetProperty("opensLayerId").GetString());
        Assert.False(Container(flow, "container:customerproject.func-app").TryGetProperty("opensLayerId", out _));
        Assert.False(Container(flow, "container:customerproject.api").TryGetProperty("opensLayerId", out _));
        Assert.All(flow.GetProperty("nodes").EnumerateArray(), n => Assert.False(n.TryGetProperty("opensLayerId", out _)));
        await AssertBaseFlow(http);
    }

    [Fact]
    public async Task Project_without_catalog_has_empty_roots_and_no_flow_links()
    {
        var http = Client();
        var (status, index, _) = await Get(http, "/api/projects/price.dev/architecture");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal((0, 0), (index.GetProperty("roots").GetArrayLength(), index.GetProperty("warnings").GetArrayLength()));

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
            "layer:price.ingress-app", "LAYER:CUSTOMERPROJECT.INGRESS-APP"];
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

        var (status, index, _) = await Get(http, $"{P}/architecture");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal((0, 0), (index.GetProperty("roots").GetArrayLength(), index.GetProperty("warnings").GetArrayLength()));
        (status, var err, _) = await Get(http, Layer(LayerTree.RootLayer));
        Assert.Equal(HttpStatusCode.NotFound, status);
        Assert.Equal("ARCHITECTURE_LAYER_NOT_FOUND", err.GetProperty("error").GetProperty("code").GetString());

        await AssertBaseFlow(http);
        var (_, flow, raw) = await Get(http, $"{P}/flow");
        Assert.DoesNotContain("opensLayerId", raw);
        Assert.DoesNotContain(flow.GetProperty("warnings").EnumerateArray(), w => w.GetString()!.StartsWith("ARCHITECTURE"));
    }

    [Fact]
    public async Task Invalid_catalog_keeps_flow_warns_on_index_and_503s_the_layer()
    {
        var tree = Tree(LayerTree.Deep(3));
        tree.Layer(1)["parentLayerId"] = tree.Layer(2)["id"]!.GetValue<string>(); // navigation cycle
        tree.Write();
        var http = Client(tree.LayersRoot);

        var (status, index, raw) = await Get(http, $"{P}/architecture");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(0, index.GetProperty("roots").GetArrayLength());
        var warning = Assert.Single(index.GetProperty("warnings").EnumerateArray()).GetString()!;
        Assert.StartsWith("ARCHITECTURE_INVALID: ", warning);
        Assert.Contains("V-004", warning);
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

        // Other projects are unaffected (their own catalog state: none).
        (status, var price, _) = await Get(http, "/api/projects/price.dev/architecture");
        Assert.Equal((HttpStatusCode.OK, 0), (status, price.GetProperty("warnings").GetArrayLength()));
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
        var authored = JsonNode.Parse(File.ReadAllText(Path.Combine(RepoPaths.ArchitectureLayers, "customerproject", "ingress-app.json")))!.AsObject();
        authored.Remove("$schema");
        Assert.True(JsonNode.DeepEquals(authored, layer), "served layer must be the authored document minus $schema");
    }
}
