using System.Security.Cryptography;
using System.Text.Json;
using Dsp.DevOverview.Catalog;

namespace Dsp.DevOverview.Api.Tests;

public class CatalogTests
{
    private static readonly SampleCatalog Catalog = SampleCatalog.Load(RepoPaths.Catalog);

    private static readonly string[] Events =
        ["CustomerChanged", "CustomerEmailChanged", "CustomerAddressChanged", "CustomerAgreementHeadChanged", "QuotationOrderHeadChanged", "HeaderFileChanged"];

    [Fact]
    public void Catalog_loads_with_kit_counts_21_2_23()
    {
        Assert.Equal("customerproject.dev", Catalog.ProjectId);
        Assert.Equal(21, Catalog.IngressMessages.Count());
        Assert.Equal(2, Catalog.CommandMessages.Count());
        Assert.Equal(23, Catalog.Messages.Count);

        var presets = Catalog.Presets.ToDictionary(p => p.Id);
        Assert.Equal(21, presets["ingress-all"].MessageCount);
        Assert.Equal(1, presets["one"].MessageCount);
        Assert.Equal(2, presets["commands"].MessageCount);
        Assert.Equal(23, presets["all"].MessageCount);
        Assert.True(presets["purge"].Destructive);
        Assert.Equal(Events, presets["one"].Params!.Event);
        Assert.Equal(Events, Catalog.Events);
    }

    [Fact]
    public void Ingress_messages_follow_the_kit_sequence()
    {
        var ingress = Catalog.IngressMessages.ToList();
        // 6 events × 3 projects, in kit order
        for (var p = 0; p < 3; p++)
            Assert.Equal(Events, ingress.Skip(p * 6).Take(6).Select(m => m.Subject));
        Assert.All(ingress.Take(18), m => Assert.Equal("base", m.Variant));
        // then project 1: renamed (v+1), stale (v), duplicate (same body as renamed)
        var tail = ingress.Skip(18).ToList();
        Assert.Equal(["renamed", "stale", "duplicate"], tail.Select(m => m.Variant));
        Assert.All(tail, m => { Assert.Equal("CustomerChanged", m.Subject); Assert.Equal(1, m.ProjectIndex); });
        Assert.Equal([1, 0, 1], tail.Select(m => m.DataVersionOffset));
        Assert.Equal(tail[0].Seq, tail[2].SameBodyAsSeq);
        Assert.EndsWith(" (ny adresse)", tail[0].Body.GetProperty("Name").GetString());
    }

    [Fact]
    public void Ingress_messages_carry_subject_contenttype_and_DataSource_only()
    {
        Assert.All(Catalog.IngressMessages, m =>
        {
            Assert.Equal("q:customerproject-ingress", m.TargetEntityId);
            Assert.Equal("application/json", m.ContentType);
            Assert.Equal(["DataSource"], m.ApplicationProperties.Keys);
            Assert.Equal("M3Cloud", m.ApplicationProperties["DataSource"].GetString());
        });
    }

    [Fact]
    public void Ingress_envelopes_match_the_kit()
    {
        var ids = Catalog.IngressMessages.Take(18).Select(m => m.Body.GetProperty("Envelope").GetProperty("Id").GetString()).ToList();
        Assert.Equal(["100|10045871", "100|10045871|01", "100|10045871|1|PRO001", "100|10045871|1000231871", "100|Q1000231871", "100|H1000231871"], ids.Take(6));

        Assert.All(Catalog.IngressMessages, m =>
        {
            var env = m.Body.GetProperty("Envelope");
            Assert.Equal("M3Cloud", env.GetProperty("DataSource").GetString());
            Assert.Equal("M3Cloud", env.GetProperty("PlatformIngressDataSource").GetString());
            Assert.Equal(["Optimera"], env.GetProperty("DataSourceTenants").EnumerateArray().Select(x => x.GetString()));
            Assert.Equal(["Optimera"], env.GetProperty("PlatformIngressTenants").EnumerateArray().Select(x => x.GetString()));
            Assert.Equal(1 + m.DataVersionOffset, env.GetProperty("DataVersion").GetInt64());
        });

        // project 3 agreement ended (Status 90), others active (20)
        var agreements = Catalog.IngressMessages.Where(m => m.Subject == "CustomerAgreementHeadChanged").ToList();
        Assert.Equal(["20", "20", "90"], agreements.Select(m => m.Body.GetProperty("Status").GetString()));
    }

    [Fact]
    public void Commands_are_SyncProjectDates_and_CreateMissingProjects_for_Optimera()
    {
        var commands = Catalog.CommandMessages.ToList();
        Assert.Equal(["SyncProjectDates", "CreateMissingProjects"], commands.Select(m => m.Subject));
        Assert.All(commands, m =>
        {
            Assert.Equal("q:customerproject", m.TargetEntityId);
            Assert.Null(m.ContentType);
            Assert.Equal("""{"Tenant":2}""", JsonSerializer.Serialize(m.Body)); // Tenant.Optimera = 2, enum as number
        });
    }

    [Fact]
    public void Sample_projects_match_the_kit_source()
    {
        Assert.Equal(["10045871", "10051234", "10060077"], Catalog.SampleProjects.Select(p => p.CustomerId));
        Assert.Equal([true, true, false], Catalog.SampleProjects.Select(p => p.Active));
        if (!File.Exists(RepoPaths.Kit)) return; // the kit is the oracle when present (read-only)
        var kit = File.ReadAllText(RepoPaths.Kit);
        Assert.All(Catalog.SampleProjects, p =>
        {
            foreach (var value in new[] { p.CustomerId, p.Name, p.Email, p.Phone, p.AddressNumber, p.AddressLine1, p.PostalCode, p.City, p.OrderNumber, p.ProjectName })
                Assert.Contains($"\"{value}\"", kit);
        });
    }

    [Fact]
    public void Catalog_is_in_sync_with_the_kit_source_hash()
    {
        if (!File.Exists(RepoPaths.Kit)) return;
        var sha = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(RepoPaths.Kit))).ToLowerInvariant();
        Assert.True(sha == Catalog.SourceSha256,
            "seed-messages/Program.cs changed since the catalog was mirrored. Re-run: dotnet run --project tools/CatalogGenerator -- generate");
    }

    [Fact]
    public void Builder_reproduces_the_kit_bytes_at_the_reference_time()
    {
        // The generator recorded SHA-256 of the exact bytes the kit's BinaryData(obj, type) produced (net8, real contracts).
        // Rebuilding at the same clock on net10 must give the same bytes.
        var builder = new SampleMessageBuilder(Catalog, () => Catalog.ReferenceUtc, markInjected: false);
        var built = builder.Build(new SampleSendRequest("all"));

        Assert.Equal(23, built.Count);
        foreach (var m in built)
        {
            var expected = Catalog.Messages.Single(x => x.Seq == m.CatalogSeq).BodySha256AtReference;
            Assert.Equal(expected, Convert.ToHexString(SHA256.HashData(m.Body)).ToLowerInvariant());
        }
    }

    [Fact]
    public void Kit_escaping_is_preserved_in_bodies()
    {
        // System.Text.Json's default encoder (what the kit uses) escapes non-ASCII and '+'.
        var builder = new SampleMessageBuilder(Catalog, () => Catalog.ReferenceUtc, markInjected: false);
        var json = System.Text.Encoding.UTF8.GetString(builder.Build(new SampleSendRequest("one", "CustomerAddressChanged")).Single().Body);
        Assert.Contains("Kjels\\u00E5sveien 161", json);
        Assert.Contains("\\u002B4722334455", json);
        Assert.Equal("Kjelsåsveien 161", JsonDocument.Parse(json).RootElement.GetProperty("AddressLine1").GetString());
    }
}
