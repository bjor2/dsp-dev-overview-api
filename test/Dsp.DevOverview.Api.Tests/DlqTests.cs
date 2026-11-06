using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Dsp.DevOverview.Api.Registration;
using Dsp.DevOverview.Api.ServiceBus;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Abstractions;

namespace Dsp.DevOverview.Api.Tests;

/// <summary>Slice 2 pieces that need no bus: the retry clone, DLQ mapping, request parsing, routing, retry target.</summary>
public class DlqUnitTests
{
    private static ServiceBusReceivedMessage DeadLettered(long seq = 42) => ServiceBusModelFactory.ServiceBusReceivedMessage(
        body: BinaryData.FromString("""{"CustomerId":"10045871"}"""),
        messageId: "original-id",
        correlationId: "corr-1",
        subject: "CustomerChanged",
        contentType: "application/json",
        properties: new Dictionary<string, object>
        {
            ["DataSource"] = "M3Cloud",
            ["x-overview-injected"] = true,
            ["Dsp_ShardingBucketNumber"] = 7,
            ["DeadLetterReason"] = "MaxDeliveryCountExceeded",
            ["DeadLetterErrorDescription"] = "Message could not be consumed after 10 delivery attempts.",
        },
        deliveryCount: 10,
        sequenceNumber: seq,
        deadLetterSource: "customerproject-topic/Subscriptions/local-inspect",
        enqueuedTime: new DateTimeOffset(2026, 11, 2, 11, 40, 12, TimeSpan.Zero));

    [Fact]
    public void Retry_clone_copies_body_subject_content_type_correlation_and_properties_and_marks_retry_of()
    {
        var original = DeadLettered(seq: 42);
        var clone = SbGateway.CloneForRetry(original);

        Assert.Equal(original.Body.ToArray(), clone.Body.ToArray());
        Assert.Equal("CustomerChanged", clone.Subject);
        Assert.Equal("application/json", clone.ContentType);
        Assert.Equal("corr-1", clone.CorrelationId);
        Assert.NotEqual("original-id", clone.MessageId);
        Assert.Equal(
            new Dictionary<string, object>
            {
                ["DataSource"] = "M3Cloud", ["x-overview-injected"] = true, ["Dsp_ShardingBucketNumber"] = 7, ["x-overview-retry-of"] = 42L,
            },
            clone.ApplicationProperties);
    }

    [Fact]
    public void Retry_clone_drops_broker_dead_letter_properties_and_overwrites_an_earlier_retry_of()
    {
        var original = ServiceBusModelFactory.ServiceBusReceivedMessage(
            body: BinaryData.FromString("x"), sequenceNumber: 9,
            properties: new Dictionary<string, object> { ["x-overview-retry-of"] = 3L, ["DeadLetterReason"] = "r" });
        var clone = SbGateway.CloneForRetry(original);

        Assert.False(clone.ApplicationProperties.ContainsKey("DeadLetterReason"));
        Assert.Equal(9L, clone.ApplicationProperties["x-overview-retry-of"]);
        Assert.Null(clone.ContentType);
        Assert.Null(clone.Subject);
    }

    [Fact]
    public void Dlq_message_carries_reason_description_and_source()
    {
        var d = SbGateway.ToDlq(DeadLettered());

        Assert.Equal(42, d.SequenceNumber);
        Assert.Equal(10, d.DeliveryCount);
        Assert.Equal("MaxDeliveryCountExceeded", d.DeadLetterReason);
        Assert.Equal("Message could not be consumed after 10 delivery attempts.", d.DeadLetterErrorDescription);
        Assert.Equal("customerproject-topic/Subscriptions/local-inspect", d.DeadLetterSource);
        Assert.Equal("10045871", ((JsonElement)d.Body!).GetProperty("CustomerId").GetString());
    }

    [Fact]
    public void Dlq_message_json_omits_dead_letter_fields_the_broker_did_not_set()
    {
        var bare = SbGateway.ToDlq(ServiceBusModelFactory.ServiceBusReceivedMessage(body: BinaryData.FromString("{}"), messageId: "m", sequenceNumber: 1));
        var json = JsonSerializer.SerializeToElement(bare, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.False(json.TryGetProperty("deadLetterReason", out _));
        Assert.False(json.TryGetProperty("deadLetterSource", out _));
        Assert.Equal(1, json.GetProperty("sequenceNumber").GetInt64());

        var full = JsonSerializer.SerializeToElement(SbGateway.ToDlq(DeadLettered()), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal("MaxDeliveryCountExceeded", full.GetProperty("deadLetterReason").GetString());
        Assert.Equal("CustomerChanged", full.GetProperty("subject").GetString());
    }

    private static JsonElement Json(string s) => JsonDocument.Parse(s).RootElement;

    [Fact]
    public void Sequence_number_selection_parses_lists_and_all()
    {
        Assert.Equal([3L, 5L], DlqSelection.Parse(Json("[3,5,3]"), allowAll: false).SequenceNumbers.Order());
        Assert.True(DlqSelection.Parse(Json("\"all\""), allowAll: true).All);
    }

    [Theory]
    [InlineData("\"all\"", false)]
    [InlineData("[]", true)]
    [InlineData("[\"7\"]", true)]
    [InlineData("[-1]", true)]
    [InlineData("\"some\"", true)]
    [InlineData("null", true)]
    public void Invalid_sequence_number_selection_is_400(string json, bool allowAll)
    {
        var ex = Assert.Throws<ApiException>(() => DlqSelection.Parse(Json(json), allowAll));
        Assert.Equal(400, ex.Status);
        Assert.Equal("INVALID_SEQUENCE_NUMBERS", ex.Code);
    }

    [Theory]
    [InlineData("q:customerproject/dlq", "dlq", "q:customerproject")]
    [InlineData("q:customerproject/dlq/retry", "dlq/retry", "q:customerproject")]
    [InlineData("s:customerproject-topic/local-inspect/dlq/discard", "dlq/discard", "s:customerproject-topic/local-inspect")]
    [InlineData("s%3Acustomerproject-topic%2Flocal-inspect/peek", "peek", "s%3Acustomerproject-topic%2Flocal-inspect")]
    [InlineData("q:customerproject/nope", "", "q:customerproject/nope")]
    public void Sb_catch_all_splits_entity_and_action(string path, string action, string entityId)
    {
        Assert.Equal((action, entityId), SbRoute.Action(path));
    }

    [Fact]
    public void Retry_target_is_the_queue_itself_or_the_subscriptions_topic()
    {
        var registration = new RegistrationLoader(RepoPaths.Schema).Load(RepoPaths.Registration);
        var project = new OverviewProject(registration, null, null, null);
        var options = new OverviewOptions();
        var service = new OverviewService(new ProjectStore([project]), new SbGateway(options.ServiceBus, NullLogger<SbGateway>.Instance), options);

        Assert.Equal("q:customerproject-ingress", service.RetryTarget(project, registration.FindEntity("q:customerproject-ingress")!).Id);
        Assert.Equal("t:customerproject-topic", service.RetryTarget(project, registration.FindEntity("s:customerproject-topic/local-inspect")!).Id);
        var ex = Assert.Throws<ApiException>(() => service.RetryTarget(project, registration.FindEntity("t:customerproject-topic")!));
        Assert.Equal("ENTITY_HAS_NO_DLQ", ex.Code);
    }
}

/// <summary>DLQ endpoints against a cloud-configured API: writes are refused and unknown entities 404 before any network call.</summary>
public class DlqCloudConfiguredApiTests(CloudConfiguredApiTests.Factory factory) : IClassFixture<CloudConfiguredApiTests.Factory>
{
    private const string P = "/api/projects/customerproject.dev";
    private readonly HttpClient _http = factory.CreateClient();

    private static async Task<string> ErrorCode(HttpResponseMessage r) =>
        JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.GetProperty("error").GetProperty("code").GetString()!;

    [Theory]
    [InlineData("q:customerproject/dlq/retry", "[1]")]
    [InlineData("q:customerproject/dlq/retry", "\"all\"")]
    [InlineData("q%3Acustomerproject-snapshot/dlq/retry", "[1]")]
    [InlineData("q:customerproject-ingress/dlq/discard", "[1]")]
    public async Task Retry_and_discard_are_403_CLOUD_WRITE_FORBIDDEN(string route, string sequenceNumbers)
    {
        var r = await _http.PostAsync($"{P}/sb/{route}", JsonContent.Create(JsonDocument.Parse($$"""{"sequenceNumbers":{{sequenceNumbers}}}""").RootElement));
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        Assert.Equal("CLOUD_WRITE_FORBIDDEN", await ErrorCode(r));
    }

    [Theory]
    [InlineData("GET", "q:nope/dlq")]
    [InlineData("POST", "q:nope/dlq/retry")]
    [InlineData("POST", "q:nope/dlq/discard")]
    public async Task Unknown_entity_is_404_UNKNOWN_ENTITY(string method, string route)
    {
        var r = method == "GET"
            ? await _http.GetAsync($"{P}/sb/{route}")
            : await _http.PostAsJsonAsync($"{P}/sb/{route}", new { sequenceNumbers = new[] { 1 } });
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
        Assert.Equal("UNKNOWN_ENTITY", await ErrorCode(r));
    }

    [Fact]
    public async Task Local_only_subscription_is_404_ENTITY_NOT_IN_SCOPE_under_cloud_scope()
    {
        var r = await _http.PostAsJsonAsync($"{P}/sb/s%3Acustomerproject-topic%2Flocal-inspect/dlq/retry", new { sequenceNumbers = "all" });
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
        Assert.Equal("ENTITY_NOT_IN_SCOPE", await ErrorCode(r));
    }

    [Fact]
    public async Task Unknown_project_and_unknown_sb_action_are_404()
    {
        var r = await _http.PostAsJsonAsync("/api/projects/customerproject/sb/q:customerproject/dlq/retry", new { sequenceNumbers = "all" });
        Assert.Equal("PROJECT_NOT_FOUND", await ErrorCode(r));
        r = await _http.PostAsJsonAsync($"{P}/sb/q:customerproject/dlq/requeue", new { sequenceNumbers = "all" });
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
        Assert.Equal("NOT_FOUND", await ErrorCode(r));
    }
}

/// <summary>Runs only with DSP_EMULATOR_TESTS=1 (it writes to the shared emulator). Take the box lock:
/// <c>flock /workspace/dsp/test.lock env DSP_EMULATOR_TESTS=1 dotnet test --filter Category=Emulator</c>.</summary>
public sealed class EmulatorFactAttribute : FactAttribute
{
    public EmulatorFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("DSP_EMULATOR_TESTS") != "1")
            Skip = "writes to the shared Service Bus emulator; run with DSP_EMULATOR_TESTS=1 under flock /workspace/dsp/test.lock";
    }
}

/// <summary>
/// End to end on the shared-stack emulator, on q:customerproject-topic-inspect (local-only, no consumer): send marked
/// messages, force them into the DLQ (PeekLock + DeadLetterMessageAsync), then list / retry / discard through the API.
/// Only touches messages carrying this run's marker and removes them afterwards.
/// </summary>
[Trait("Category", "Emulator")]
public class DlqEmulatorTests(DlqEmulatorTests.Factory factory, ITestOutputHelper output) : IClassFixture<DlqEmulatorTests.Factory>
{
    public sealed class Factory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("Overview:ServiceBus:ConnectionString", Emulator);
            builder.UseSetting("Overview:ServiceBus:Scope", "Local");
        }
    }

    private const string Emulator = "Endpoint=sb://localhost;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true";
    private const string Queue = "customerproject-topic-inspect";
    private const string Route = "/api/projects/customerproject.dev/sb/q:customerproject-topic-inspect";
    private const string MarkerKey = "x-overview-test-run";
    private readonly HttpClient _http = factory.CreateClient();
    private readonly string _marker = Guid.NewGuid().ToString("N");

    private bool Mine(JsonElement m) =>
        m.GetProperty("applicationProperties").TryGetProperty(MarkerKey, out var v) && v.GetString() == _marker;

    private async Task<List<JsonElement>> MyDeadLetters() =>
        JsonDocument.Parse(await _http.GetStringAsync($"{Route}/dlq?max=250")).RootElement.EnumerateArray().Where(Mine).ToList();

    private async Task<List<JsonElement>> MyActive() =>
        JsonDocument.Parse(await _http.GetStringAsync($"{Route}/peek?max=250")).RootElement.GetProperty("messages").EnumerateArray().Where(Mine).ToList();

    private async Task<JsonElement> Post(string action, object sequenceNumbers)
    {
        var r = await _http.PostAsJsonAsync($"{Route}/dlq/{action}", new { sequenceNumbers });
        var body = await r.Content.ReadAsStringAsync();
        Assert.True(r.IsSuccessStatusCode, $"{action}: {(int)r.StatusCode} {body}");
        return JsonDocument.Parse(body).RootElement;
    }

    [EmulatorFact]
    public async Task Forced_dead_letters_can_be_listed_retried_and_discarded()
    {
        await using var client = new ServiceBusClient(Emulator);
        try
        {
            await using (var sender = client.CreateSender(Queue))
                for (var i = 1; i <= 3; i++)
                    await sender.SendMessageAsync(new ServiceBusMessage(BinaryData.FromString($$"""{"test":"dlq","n":{{i}}}"""))
                    {
                        Subject = "CustomerChanged", ContentType = "application/json", CorrelationId = $"corr-{i}",
                        ApplicationProperties = { [MarkerKey] = _marker, ["DataSource"] = "M3Cloud", ["n"] = i },
                    });
            Assert.Equal(3, await ForEachMine(client, SubQueue.None, (r, m) => r.DeadLetterMessageAsync(m, "OverviewTest", "forced by DlqEmulatorTests")));

            // list
            var dlq = await MyDeadLetters();
            Assert.Equal(3, dlq.Count);
            Assert.All(dlq, m =>
            {
                Assert.Equal("OverviewTest", m.GetProperty("deadLetterReason").GetString());
                Assert.Equal("forced by DlqEmulatorTests", m.GetProperty("deadLetterErrorDescription").GetString());
            });
            var bySeq = dlq.ToDictionary(m => m.GetProperty("sequenceNumber").GetInt64(), m => m.GetProperty("body").GetProperty("n").GetInt32());
            var (retrySeq, discardSeq, lastSeq) = (bySeq.Keys.ElementAt(0), bySeq.Keys.ElementAt(1), bySeq.Keys.ElementAt(2));
            output.WriteLine($"DLQ seqs {string.Join(",", bySeq.Keys)} (marker {_marker})");

            // retry one: clone lands on the active queue, the original leaves the DLQ
            var retried = await Post("retry", new[] { retrySeq, 999_999_999L });
            Assert.Equal(1, retried.GetProperty("retried").GetInt32());
            Assert.Equal("q:customerproject-topic-inspect", retried.GetProperty("targetEntityId").GetString());
            Assert.Equal([999_999_999L], retried.GetProperty("notFound").EnumerateArray().Select(x => x.GetInt64()));
            var clone = Assert.Single(await MyActive());
            var props = clone.GetProperty("applicationProperties");
            Assert.Equal(retrySeq, props.GetProperty("x-overview-retry-of").GetInt64());
            Assert.Equal("M3Cloud", props.GetProperty("DataSource").GetString());
            Assert.Equal(bySeq[retrySeq], props.GetProperty("n").GetInt32());
            Assert.False(props.TryGetProperty("DeadLetterReason", out _));
            Assert.Equal("CustomerChanged", clone.GetProperty("subject").GetString());
            Assert.Equal("application/json", clone.GetProperty("contentType").GetString());
            Assert.Equal($"corr-{bySeq[retrySeq]}", clone.GetProperty("correlationId").GetString());
            Assert.Equal(bySeq[retrySeq], clone.GetProperty("body").GetProperty("n").GetInt32());
            Assert.Equal(0, clone.GetProperty("deliveryCount").GetInt32());
            Assert.DoesNotContain(retrySeq, (await MyDeadLetters()).Select(m => m.GetProperty("sequenceNumber").GetInt64()));

            // discard one: gone from the DLQ, nothing new on the active queue
            var discarded = await Post("discard", new[] { discardSeq });
            Assert.Equal(1, discarded.GetProperty("discarded").GetInt32());
            Assert.Equal([lastSeq], (await MyDeadLetters()).Select(m => m.GetProperty("sequenceNumber").GetInt64()));
            Assert.Single(await MyActive());

            // retry "all" only when the DLQ holds nothing but ours (never resend other people's dead letters)
            var everything = JsonDocument.Parse(await _http.GetStringAsync($"{Route}/dlq?max=250")).RootElement.GetArrayLength();
            if (everything == 1)
            {
                var all = await Post("retry", "all");
                Assert.Equal(1, all.GetProperty("retried").GetInt32());
                Assert.Empty(await MyDeadLetters());
                Assert.Contains(lastSeq, (await MyActive()).Select(m => m.GetProperty("applicationProperties").GetProperty("x-overview-retry-of").GetInt64()));
            }
            else output.WriteLine($"retry \"all\" skipped: the {Queue} DLQ holds {everything - 1} message(s) that aren't this test's");
        }
        finally
        {
            await ForEachMine(client, SubQueue.None, (r, m) => r.CompleteMessageAsync(m));
            await ForEachMine(client, SubQueue.DeadLetter, (r, m) => r.CompleteMessageAsync(m));
        }
    }

    /// <summary>PeekLock-receives <see cref="Queue"/> (or its DLQ), applies <paramref name="act"/> to this run's messages, abandons the rest.</summary>
    private async Task<int> ForEachMine(ServiceBusClient client, SubQueue subQueue, Func<ServiceBusReceiver, ServiceBusReceivedMessage, Task> act)
    {
        await using var receiver = client.CreateReceiver(Queue, new ServiceBusReceiverOptions { SubQueue = subQueue });
        var others = new List<ServiceBusReceivedMessage>();
        var done = 0;
        try
        {
            while (true)
            {
                var batch = await receiver.ReceiveMessagesAsync(100, TimeSpan.FromSeconds(2));
                if (batch.Count == 0) break;
                foreach (var m in batch)
                    if (m.ApplicationProperties.TryGetValue(MarkerKey, out var v) && (string)v == _marker) { await act(receiver, m); done++; }
                    else others.Add(m);
            }
        }
        finally { foreach (var m in others) await receiver.AbandonMessageAsync(m); }
        return done;
    }
}
