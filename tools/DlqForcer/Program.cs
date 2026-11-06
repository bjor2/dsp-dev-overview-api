// Forces messages into the dead-letter queue of an emulator queue or subscription.
//
//   dotnet run --project tools/DlqForcer -- --queue customerproject-ingress --match x-overview-demo-run=abc123 [--count 2]
//   dotnet run --project tools/DlqForcer -- --topic customerproject-topic --subscription local-inspect --match k=v
//   options: --reason <text> (default OverviewForcedDeadLetter)  --description <text>  --count <n> (default: all matches)
//
// PeekLock-receives the entity, dead-letters messages whose application property matches --match (all of them when
// --match is omitted, which needs --count), and abandons the rest (their delivery count goes up by one). Prints one line
// per dead-lettered message: "<sequenceNumber> <subject>". Exit 1 if nothing matched.
// Connection: $SB_CONNECTION, default the shared-stack emulator. Refuses anything that is not the emulator.
using Azure.Messaging.ServiceBus;

const string EmulatorConnection = "Endpoint=sb://localhost;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true";

var opts = new Dictionary<string, string>();
for (var i = 0; i < args.Length; i++)
{
    if (!args[i].StartsWith("--") || i + 1 >= args.Length) return Usage($"bad argument '{args[i]}'");
    opts[args[i][2..]] = args[++i];
}

var connection = Environment.GetEnvironmentVariable("SB_CONNECTION") is { Length: > 0 } c ? c : EmulatorConnection;
if (!connection.Contains("UseDevelopmentEmulator=true", StringComparison.OrdinalIgnoreCase))
    return Usage("SB_CONNECTION is not the local emulator; DlqForcer only runs against UseDevelopmentEmulator=true");

(string Key, string Value)? match = opts.TryGetValue("match", out var m) && m.Split('=', 2) is [var k, var v] ? (k, v) : null;
var count = opts.TryGetValue("count", out var n) ? int.Parse(n) : int.MaxValue;
if (match is null && count == int.MaxValue) return Usage("give --match key=value and/or --count n");
var reason = opts.GetValueOrDefault("reason", "OverviewForcedDeadLetter");
var description = opts.GetValueOrDefault("description", "Dead-lettered on purpose by dev-overview tools/DlqForcer (slice 2 test).");

await using var client = new ServiceBusClient(connection);
var receiverOptions = new ServiceBusReceiverOptions { ReceiveMode = ServiceBusReceiveMode.PeekLock };
ServiceBusReceiver receiver;
if (opts.TryGetValue("queue", out var queue)) receiver = client.CreateReceiver(queue, receiverOptions);
else if (opts.TryGetValue("topic", out var topic) && opts.TryGetValue("subscription", out var sub)) receiver = client.CreateReceiver(topic, sub, receiverOptions);
else return Usage("give --queue <name> or --topic <name> --subscription <name>");

var held = new List<ServiceBusReceivedMessage>();
var deadLettered = 0;
try
{
    while (deadLettered < count)
    {
        var batch = await receiver.ReceiveMessagesAsync(100, TimeSpan.FromSeconds(2));
        if (batch.Count == 0) break;
        foreach (var msg in batch)
        {
            var isMatch = deadLettered < count && (match is not { } mm
                || msg.ApplicationProperties.TryGetValue(mm.Key, out var value) && string.Equals(value?.ToString(), mm.Value, StringComparison.Ordinal));
            if (!isMatch) { held.Add(msg); continue; }
            await receiver.DeadLetterMessageAsync(msg, reason, description);
            deadLettered++;
            Console.WriteLine($"{msg.SequenceNumber} {msg.Subject}");
        }
    }
}
finally
{
    foreach (var msg in held) await receiver.AbandonMessageAsync(msg);
    await receiver.DisposeAsync();
}
Console.Error.WriteLine($"dead-lettered {deadLettered} message(s) on {receiver.EntityPath} (reason '{reason}'); {held.Count} other(s) abandoned");
return deadLettered > 0 ? 0 : 1;

static int Usage(string error)
{
    Console.Error.WriteLine($"DlqForcer: {error}\n  --queue <name> | --topic <name> --subscription <name>  [--match key=value] [--count n] [--reason r] [--description d]");
    return 2;
}
