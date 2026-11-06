// Mirrors /workspace/dsp/local-dev/customerproject/seed-messages/Program.cs into catalog/customerproject.samples.json.
//
//   dotnet run --project tools/CatalogGenerator -- generate   write the catalog (bodies rendered at a fixed reference time)
//   dotnet run --project tools/CatalogGenerator -- verify     rebuild every mode with the real contract types and check the
//                                                            overview's SampleMessageBuilder produces the same bytes/props
//
// KitMirror below is a line-by-line copy of the kit's sample data and message construction, with DateTime.UtcNow replaced
// by an injected clock. The kit itself is never executed or edited. The commands are serialized from a local copy of
// SyncProjectDates/CreateMissingProjects ({ Tenant }) because referencing Dahl.CustomerProject.Messaging would build
// (and write obj/ into) the read-only service repo.
using System.Security.Cryptography;
using System.Text.Json;
using Dahl.Core.Tenant;
using Dahl.M3.Event.Common;
using Dahl.M3.Event.Common.Enum;
using Dahl.M3.Event.CustomerAddressChanged.Contract;
using Dahl.M3.Event.CustomerAgreementHeadChanged.Contract;
using Dahl.M3.Event.CustomerChanged.Contract;
using Dahl.M3.Event.CustomerEmailChanged.Contract;
using Dahl.M3.Event.HeaderFileChanged.Contract;
using Dahl.M3.Event.QuotationOrderHeadChanged.Contract;
using Dsp.DevOverview.Catalog;

var root = FindRoot();
var kitPath = "/workspace/dsp/local-dev/customerproject/seed-messages/Program.cs";
var catalogPath = Path.Combine(root, "catalog", "customerproject.samples.json");
var command = args.FirstOrDefault() ?? "generate";

switch (command)
{
    case "generate":
        var catalog = Generate(new DateTime(2026, 11, 2, 12, 0, 0, DateTimeKind.Utc));
        Directory.CreateDirectory(Path.GetDirectoryName(catalogPath)!);
        File.WriteAllText(catalogPath, catalog.ToJson() + "\n");
        Console.WriteLine($"wrote {catalogPath}: {catalog.IngressMessages.Count()} ingress + {catalog.CommandMessages.Count()} commands");
        return Verify(SampleCatalog.Load(catalogPath));
    case "verify":
        return Verify(SampleCatalog.Load(catalogPath));
    default:
        Console.Error.WriteLine("usage: generate | verify");
        return 2;
}

SampleCatalog Generate(DateTime referenceUtc)
{
    var kit = new KitMirror(() => referenceUtc);
    var projects = KitMirror.SampleProjects();
    var messages = new List<CatalogMessage>();
    var seq = 0;

    CatalogMessage Ingress(object e, int projectIndex, string variant, int offset, int? sameAs = null)
    {
        var bytes = KitMirror.IngressBody(e);
        return new CatalogMessage
        {
            Seq = ++seq, PresetId = PresetIds.Ingress, Subject = e.GetType().Name, TargetEntityId = "q:customerproject-ingress",
            ContentType = "application/json",
            ApplicationProperties = new() { ["DataSource"] = JsonSerializer.SerializeToElement("M3Cloud") },
            Body = JsonDocument.Parse(bytes).RootElement.Clone(), BodySha256AtReference = Sha(bytes),
            ProjectIndex = projectIndex, Variant = variant, DataVersionOffset = offset, SameBodyAsSeq = sameAs,
        };
    }

    // QueueIngressEventsCore: six events per project, then renamed (v+1), stale (v), duplicate (same instance as renamed).
    for (var i = 0; i < projects.Length; i++)
        foreach (var e in kit.ChangeEventsFor(projects[i], 1))
            messages.Add(Ingress(e, i + 1, "base", 0));
    var first = projects[0];
    var renamed = Ingress(kit.CustomerChanged(first with { Name = first.Name + " (ny adresse)" }, 2), 1, "renamed", 1);
    messages.Add(renamed);
    messages.Add(Ingress(kit.CustomerChanged(first, 1), 1, "stale", 0));
    messages.Add(Ingress(kit.CustomerChanged(first with { Name = first.Name + " (ny adresse)" }, 2), 1, "duplicate", 1, renamed.Seq));

    foreach (var c in KitMirror.Commands())
    {
        var bytes = KitMirror.CommandBody(c);
        messages.Add(new CatalogMessage
        {
            Seq = ++seq, PresetId = PresetIds.Commands, Subject = c.GetType().Name, TargetEntityId = "q:customerproject",
            ContentType = null, Body = JsonDocument.Parse(bytes).RootElement.Clone(), BodySha256AtReference = Sha(bytes),
            Variant = "command",
        });
    }

    var events = kit.ChangeEventsFor(projects[0], 1).Select(e => e.GetType().Name).ToList();
    var ingressCount = messages.Count(m => m.PresetId == PresetIds.Ingress);
    var commandCount = messages.Count(m => m.PresetId == PresetIds.Commands);

    return new SampleCatalog
    {
        ProjectId = "customerproject.dev",
        Source = "local-dev/customerproject/seed-messages/Program.cs",
        SourceSha256 = Sha(File.ReadAllBytes(kitPath)),
        MirroredAt = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssK"),
        Generator = "tools/CatalogGenerator (net8.0, Dahl.M3.Event.* 1.0.10, Dahl.M3.Event.Common 1.0.7, Dahl.Core.Tenant 2.0.15)",
        ReferenceUtc = referenceUtc,
        ContractPackages = new()
        {
            ["Dahl.M3.Event.Common"] = "1.0.7", ["Dahl.M3.Event.*.Contract"] = "1.0.10", ["Dahl.Core.Tenant"] = "2.0.15",
            ["Dahl.Core.Messaging (kit-resolved, commands)"] = "2.0.1",
        },
        SampleProjects = projects.Select((p, i) => new SampleProjectInfo
        {
            Index = i + 1, CustomerId = p.CustomerId, Name = p.Name, OrderNumber = p.OrderNumber, Active = p.Active, Email = p.Email,
            Phone = p.Phone, AddressNumber = p.AddressNumber, AddressLine1 = p.AddressLine1, AddressLine2 = p.AddressLine2,
            PostalCode = p.PostalCode, City = p.City, ProjectName = p.ProjectName,
        }).ToList(),
        Events = events,
        Ingress = new IngressSpec
        {
            TargetEntityId = "q:customerproject-ingress",
            ContentType = "application/json",
            ApplicationProperties = new() { ["DataSource"] = JsonSerializer.SerializeToElement("M3Cloud") },
            DefaultDataVersion = 1,
            LastModifiedRule = "Envelope.LastModifiedUtc = utcNow + DataVersion seconds; Envelope.DataVersion = base + dataVersionOffset",
            DateFieldsMonthsFromNow = new()
            {
                ["CustomerAddressChanged"] = new() { ["ValidFrom"] = -6, ["ValidTo"] = 18 },
                ["CustomerAgreementHeadChanged"] = new() { ["StartDate"] = -6, ["ValidTo"] = 18 },
                ["QuotationOrderHeadChanged"] = new() { ["ValidTo"] = 18 },
            },
            // QueueOneEvent: project with { ProjectName += " (vN)", Name += " (vN)" }; Name feeds CustomerChanged.Name,
            // ProjectName feeds CustomerAddressChanged.YourReference1. No other event carries either.
            OneSuffixFields = new()
            {
                ["CustomerChanged"] = ["Name"],
                ["CustomerAddressChanged"] = ["YourReference1"],
            },
            OneSuffixFormat = " (v{0})",
        },
        Commands = new CommandSpec
        {
            TargetEntityId = "q:customerproject",
            ContentType = null,
            DefaultPropertyProfile = "kit",
            PropertyProfiles = new()
            {
                ["kit"] = new CommandPropertyProfile
                {
                    Description = "Exactly what the kit puts on the wire: Dahl.Core.Messaging 2.0.1 ServiceBusClientBase.SetEnvelopeDataOnMessage. " +
                        "No Tenant* properties (SyncProjectDates/CreateMissingProjects implement ICommand {Tenant}, not ITenants) and " +
                        "no Dsp_OriginalEnqueuedTime (added in Dahl.Core.Messaging 2.0.2).",
                    Properties =
                    [
                        new() { Name = "Dsp_ShardingBucketNumber", Runtime = "shardingBucket" },
                        new() { Name = "Dsp_CorrelationContextId", Runtime = "correlationId" },
                        new() { Name = "Dsp_IsM3Cloud", Value = JsonSerializer.SerializeToElement(true) },
                        new() { Name = "DataSource", Value = JsonSerializer.SerializeToElement("M3Cloud") },
                    ],
                },
                ["addenda"] = new CommandPropertyProfile
                {
                    Description = "ADDENDA-001 §1 / HANDOFF property set: Tenant<Name> bool per Tenant enum value (Optimera=true), " +
                        "Dsp_ShardingBucketNumber, Dsp_CorrelationContextId, Dsp_IsM3Cloud, DataSource, Dsp_OriginalEnqueuedTime " +
                        "(Dahl.Core.Messaging >= 2.0.2 order). Superset of 'kit'.",
                    Properties =
                    [
                        .. Enum.GetValues<Tenant>().Where(t => t != Tenant.None).Select(t => new CommandProperty
                            { Name = $"Tenant{t}", Value = JsonSerializer.SerializeToElement(t == Tenant.Optimera) }),
                        new() { Name = "Dsp_ShardingBucketNumber", Runtime = "shardingBucket" },
                        new() { Name = "Dsp_CorrelationContextId", Runtime = "correlationId" },
                        new() { Name = "Dsp_IsM3Cloud", Value = JsonSerializer.SerializeToElement(true) },
                        new() { Name = "DataSource", Value = JsonSerializer.SerializeToElement("M3Cloud") },
                        new() { Name = "Dsp_OriginalEnqueuedTime", Runtime = "utcNowRoundtrip" },
                    ],
                },
            },
        },
        PurgeEntityIds = ["q:customerproject-ingress", "q:customerproject-snapshot", "q:customerproject-domain", "q:customerproject", "q:communication", "q:customerproject-topic-inspect"],
        Presets =
        [
            new() { Id = PresetIds.Ingress, Mode = "ingress", Label = "Ingress batch (6 events × 3 projects + rename/stale/duplicate)", TargetEntityId = "q:customerproject-ingress", MessageCount = ingressCount, Params = new() { DataVersion = 1 } },
            new() { Id = PresetIds.One, Mode = "one", Label = "Single ingress event", TargetEntityId = "q:customerproject-ingress", MessageCount = 1, Params = new() { Event = events, DataVersion = 1, ProjectIndex = [1, 2, 3] } },
            new() { Id = PresetIds.Commands, Mode = "commands", Label = "Commands (Optimera): SyncProjectDates, CreateMissingProjects", TargetEntityId = "q:customerproject", MessageCount = commandCount },
            new() { Id = PresetIds.All, Mode = "all", Label = "Everything (ingress + commands)", TargetEntityId = null, MessageCount = ingressCount + commandCount, Params = new() { DataVersion = 1 } },
            new() { Id = PresetIds.Purge, Mode = "purge", Label = "Purge all customerproject queues + DLQs (emulator only)", TargetEntityId = null, MessageCount = 0, Destructive = true },
        ],
        Messages = messages,
    };
}

int Verify(SampleCatalog catalog)
{
    // An awkward clock (sub-millisecond ticks, month-end) so DateTime formatting and AddMonths are exercised.
    var clock = new DateTime(2027, 1, 31, 23, 59, 58, DateTimeKind.Utc).AddTicks(1234567);
    var kit = new KitMirror(() => clock);
    var builder = new SampleMessageBuilder(catalog, () => clock, markInjected: false);
    var failures = 0;
    var checks = 0;

    void Same(string what, byte[] expected, byte[] actual)
    {
        checks++;
        if (expected.AsSpan().SequenceEqual(actual)) return;
        failures++;
        Console.WriteLine($"MISMATCH {what}\n  kit:     {System.Text.Encoding.UTF8.GetString(expected)}\n  builder: {System.Text.Encoding.UTF8.GetString(actual)}");
    }

    foreach (var dv in new[] { 1, 7 })
    {
        var expected = kit.IngressBatch(dv).Select(KitMirror.IngressBody).ToList();
        var actual = builder.Build(new SampleSendRequest("ingress", DataVersion: dv));
        if (expected.Count != actual.Count) { failures++; Console.WriteLine($"ingress {dv}: count {actual.Count} != {expected.Count}"); }
        for (var i = 0; i < Math.Min(expected.Count, actual.Count); i++)
        {
            Same($"ingress dv={dv} #{i + 1} {actual[i].Subject}", expected[i], actual[i].Body);
            checks++;
            if (actual[i].ContentType != "application/json" || actual[i].ApplicationProperties.Count != 1 || (string)actual[i].ApplicationProperties[0].Value != "M3Cloud")
            { failures++; Console.WriteLine($"ingress #{i + 1}: props/contentType differ"); }
        }
    }

    foreach (var p in new[] { 1, 2, 3 })
        foreach (var ev in catalog.Events)
            foreach (var dv in new[] { 1, 9 })
            {
                var expected = KitMirror.IngressBody(kit.OneEvent(ev, dv, p));
                var actual = builder.Build(new SampleSendRequest("one", ev, dv, p)).Single();
                Same($"one {ev} dv={dv} project={p}", expected, actual.Body);
            }

    var commands = builder.Build(new SampleSendRequest("commands"));
    var kitCommands = KitMirror.Commands().ToList();
    for (var i = 0; i < kitCommands.Count; i++)
    {
        Same($"command {kitCommands[i].GetType().Name}", KitMirror.CommandBody(kitCommands[i]), commands[i].Body);
        checks++;
        if (commands[i].Subject != kitCommands[i].GetType().Name || commands[i].ContentType is not null) { failures++; Console.WriteLine("command subject/contentType differ"); }
    }

    var all = builder.Build(new SampleSendRequest("all"));
    checks++;
    if (all.Count != 23) { failures++; Console.WriteLine($"all: {all.Count} != 23"); }

    Console.WriteLine(failures == 0 ? $"verify OK: {checks} checks, kit bytes == builder bytes" : $"verify FAILED: {failures}/{checks}");
    return failures == 0 ? 0 : 1;
}

static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

static string FindRoot()
{
    for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        if (File.Exists(Path.Combine(dir.FullName, "Dsp.DevOverview.sln"))) return dir.FullName;
    throw new DirectoryNotFoundException("Dsp.DevOverview.sln not found above " + AppContext.BaseDirectory);
}

/// <summary>Copy of the kit's data and builders (seed-messages/Program.cs), clock injected.</summary>
sealed class KitMirror(Func<DateTime> utcNow)
{
    const int Company = 100;

    public static SampleProject[] SampleProjects() => new[]
    {
        new SampleProject("10045871", "Bygg og Montasje Nordre Aker AS", "post@byggmontasje-na.example", "+4722334455",
            "PRO001", "Kjelsåsveien 161", "Byggeplass blokk B", "0491", "OSLO", "1000231871", "Kjelsås Hageby, byggetrinn 2", Active: true),
        new SampleProject("10051234", "Fjordbyen Rør og Varme AS", "ordre@fjordbyen-ror.example", "+4755667788",
            "PRO002", "Damsgårdsveien 70", "Port 3", "5058", "BERGEN", "1000231902", "Damsgård Brygge, VVS", Active: true),
        new SampleProject("10060077", "Tømrermester Hansen", "hansen@tomrer-hansen.example", "+4790012345",
            "PRO003", "Strandveien 12", "", "7042", "TRONDHEIM", "1000229455", "Lade Allé 3, rehab", Active: false),
    };

    /// <summary>QueueIngressEventsCore, in send order (the duplicate is the same instance as the renamed event).</summary>
    public List<object> IngressBatch(int baseVersion)
    {
        var projects = SampleProjects();
        var list = new List<object>();
        foreach (var project in projects)
            list.AddRange(ChangeEventsFor(project, baseVersion));
        var first = projects[0];
        var renamed = CustomerChanged(first with { Name = first.Name + " (ny adresse)" }, baseVersion + 1);
        list.Add(renamed);
        list.Add(CustomerChanged(first, baseVersion));
        list.Add(renamed);
        return list;
    }

    /// <summary>QueueOneEvent.</summary>
    public object OneEvent(string eventName, int dataVersion, int projectNumber)
    {
        var project = SampleProjects()[projectNumber - 1];
        project = project with { ProjectName = $"{project.ProjectName} (v{dataVersion})", Name = $"{project.Name} (v{dataVersion})" };
        return ChangeEventsFor(project, dataVersion).First(e => e.GetType().Name == eventName);
    }

    public static IEnumerable<object> Commands()
    {
        yield return new SyncProjectDates { Tenant = Tenant.Optimera };
        yield return new CreateMissingProjects { Tenant = Tenant.Optimera };
    }

    public IEnumerable<object> ChangeEventsFor(SampleProject p, int dataVersion)
    {
        yield return CustomerChanged(p, dataVersion);
        yield return new CustomerEmailChanged
        {
            Envelope = NewEnvelope($"{Company}|{p.CustomerId}|01", dataVersion),
            Company = Company,
            Deleted = false,
            EmailType = "01",
            EmailKeyValue = p.CustomerId,
            ElectronicMailAddress = p.Email,
        };
        yield return new CustomerAddressChanged
        {
            Envelope = NewEnvelope($"{Company}|{p.CustomerId}|1|{p.AddressNumber}", dataVersion),
            Company = Company,
            Deleted = false,
            Customer = p.CustomerId,
            AddressType = 1,
            AddressNumber = p.AddressNumber,
            AddressLine1 = p.AddressLine1,
            AddressLine2 = p.AddressLine2,
            PostalCode = p.PostalCode,
            City = p.City,
            Country = "NO",
            TelephoneNumber1 = p.Phone,
            YourReference1 = p.ProjectName,
            AddressCategory = "PRO",
            ValidFrom = YyyyMmDd(utcNow().AddMonths(-6)),
            ValidTo = YyyyMmDd(utcNow().AddMonths(18)),
        };
        yield return new CustomerAgreementHeadChanged
        {
            Envelope = NewEnvelope($"{Company}|{p.CustomerId}|{p.OrderNumber}", dataVersion),
            Deleted = false,
            Customer = p.CustomerId,
            AddressNumber = p.AddressNumber,
            Description = p.OrderNumber,
            Status = p.Active ? "20" : "90",
            StartDate = YyyyMmDd(utcNow().AddMonths(-6)),
            ValidTo = YyyyMmDd(utcNow().AddMonths(18)),
        };
        yield return new QuotationOrderHeadChanged
        {
            Envelope = NewEnvelope($"{Company}|Q{p.OrderNumber}", dataVersion),
            Company = Company,
            Deleted = false,
            CustomerOrderNumber = p.OrderNumber,
            QuotationReason = "300",
            ValidTo = YyyyMmDd(utcNow().AddMonths(18)),
        };
        yield return new HeaderFileChanged
        {
            Envelope = NewEnvelope($"{Company}|H{p.OrderNumber}", dataVersion),
            Company = Company,
            Deleted = false,
            Customer = p.CustomerId,
            AddressNumber = p.AddressNumber,
            CustomerOrderNumber = p.OrderNumber,
            CustomerOrderType = "010",
        };
    }

    public CustomerChanged CustomerChanged(SampleProject p, int dataVersion) => new()
    {
        Envelope = NewEnvelope($"{Company}|{p.CustomerId}", dataVersion),
        Company = Company,
        Deleted = false,
        Customer = p.CustomerId,
        Name = p.Name,
        TelephoneNumber2 = p.Phone,
    };

    Envelope NewEnvelope(string id, int dataVersion) => new()
    {
        Id = id,
        DataVersion = dataVersion,
        DataSource = DataSource.M3Cloud,
        PlatformIngressDataSource = PlatformIngressDataSource.M3Cloud,
        LastModifiedUtc = utcNow().AddSeconds(dataVersion),
        DataSourceTenants = ["Optimera"],
        PlatformIngressTenants = [PlatformIngressTenant.Optimera],
    };

    /// <summary>ToIngressMessage: new BinaryData(changeEvent, type: changeEvent.GetType()).</summary>
    public static byte[] IngressBody(object changeEvent) => new BinaryData(changeEvent, type: changeEvent.GetType()).ToArray();

    /// <summary>ServiceBusClientBase.MapToServiceBusMessage: new BinaryData(message, JsonSerializerOptions (null), null).</summary>
    public static byte[] CommandBody(object command) => new BinaryData(command, (JsonSerializerOptions?)null, null).ToArray();

    static int YyyyMmDd(DateTime date) => date.Year * 10000 + date.Month * 100 + date.Day;
}

record SampleProject(
    string CustomerId, string Name, string Email, string Phone,
    string AddressNumber, string AddressLine1, string AddressLine2, string PostalCode, string City,
    string OrderNumber, string ProjectName, bool Active);

// Local copies of Dahl.CustomerProject.Messaging.Messages.Commands (dsp-customerproject-localrun, read-only): same name,
// same single public property, so System.Text.Json output is identical.
class SyncProjectDates { public Tenant Tenant { get; set; } }
class CreateMissingProjects { public Tenant Tenant { get; set; } }
