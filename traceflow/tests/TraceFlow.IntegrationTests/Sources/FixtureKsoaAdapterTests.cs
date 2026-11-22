using System.Text.Json;
using TraceFlow.Application.Abstractions;
using TraceFlow.Infrastructure.Sources;

namespace TraceFlow.IntegrationTests.Sources;

public class FixtureKsoaAdapterTests
{
    private static string FixtureDir => Path.Combine(AppContext.BaseDirectory, "fixtures", "ksoa");

    [Fact]
    public async Task Sale_normal_doc_key_lines_and_codes_associated_per_line()
    {
        var adapter = new FixtureKsoaAdapter(FixtureDir);
        Assert.Equal("ksoa-fixture-1", adapter.ContractVersion);
        var docs = await adapter.FetchAsync("sale", DateTime.MinValue, DateTime.MaxValue, CancellationToken.None);
        var doc = Assert.Single(docs, d => d.SourceDocumentKey == "000123");
        Assert.Equal("SALE", doc.DocumentType);
        // 2026-11-20 14:32:05 Asia/Shanghai = 06:32:05 UTC（AT-002 业务时间）
        Assert.Equal(new DateTime(2026, 11, 20, 6, 32, 5, DateTimeKind.Utc), doc.BusinessDateTimeUtc);
        Assert.Equal(3, doc.Lines.Count);
        var l1 = doc.Lines.Single(l => l.SourceLineKey == "1");
        Assert.Equal("SP0001", l1.ProductSourceId);
        Assert.Equal("81234567890123456789", l1.TraceCodeRaw); // 码随行走，不挂单头
        Assert.Equal("SALE", l1.EventKind);
        var l3 = doc.Lines.Single(l => l.SourceLineKey == "3");
        Assert.Null(l3.TraceCodeRaw); // 未扫码行保留 null，不造码
        Assert.Empty(doc.KeyIssues);
    }

    [Fact]
    public async Task Purchase_in_lines_carry_batch_and_purchase_event()
    {
        var adapter = new FixtureKsoaAdapter(FixtureDir);
        var docs = await adapter.FetchAsync("purchase-in", DateTime.MinValue, DateTime.MaxValue, CancellationToken.None);
        var doc = Assert.Single(docs);
        Assert.Equal("RK20261120001", doc.SourceDocumentKey);
        Assert.Equal("PURCHASE_IN", doc.DocumentType);
        Assert.Equal(2, doc.Lines.Count);
        Assert.All(doc.Lines, l => Assert.Equal("PURCHASE_IN", l.EventKind));
        Assert.Equal("P20260101", doc.Lines.Single(l => l.SourceLineKey == "1").BatchRaw);
        Assert.Equal(10m, doc.Lines.Single(l => l.SourceLineKey == "1").QuantityOriginal);
    }

    [Fact]
    public async Task Missing_plh_becomes_issue_with_raw_preserved_not_fabricated_line()
    {
        var adapter = new FixtureKsoaAdapter(FixtureDir);
        var docs = await adapter.FetchAsync("sale", DateTime.MinValue, DateTime.MaxValue, CancellationToken.None);
        var doc = docs.Single(d => d.SourceDocumentKey == "000124");
        Assert.Empty(doc.Lines); // 不造行号（SRC-02）
        var issue = Assert.Single(doc.KeyIssues);
        Assert.Equal("MISSING_LINE_KEY", issue.IssueKind);
        Assert.Null(issue.SourceLineKey);
        Assert.Contains("81234567890123456791", issue.RawLineJson); // raw 完整保留
    }

    [Fact]
    public async Task Mixed_sign_receipt_splits_events_without_netting()
    {
        var adapter = new FixtureKsoaAdapter(FixtureDir);
        var docs = await adapter.FetchAsync("sale", DateTime.MinValue, DateTime.MaxValue, CancellationToken.None);
        var doc = docs.Single(d => d.SourceDocumentKey == "000125");
        Assert.Equal("SALE", doc.Lines.Single(l => l.SourceLineKey == "1").EventKind);
        Assert.Equal("CONSUMER_RETURN", doc.Lines.Single(l => l.SourceLineKey == "2").EventKind);
        Assert.Equal("NO_EVENT", doc.Lines.Single(l => l.SourceLineKey == "3").EventKind);
        Assert.Equal(-1m, doc.Lines.Single(l => l.SourceLineKey == "2").QuantityOriginal);
    }

    [Fact]
    public async Task Duplicate_line_key_and_orphan_line_become_issues()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tf-fixture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(dir, "dup.json"), """
            { "salemast": [{ "lshh": "D1", "rq": "2026-11-20", "ontime": "08:00:00" }],
              "salemxls": [
                { "lshh": "D1", "plh": "1", "spid": "SP0001", "shl": 1 },
                { "lshh": "D1", "plh": "1", "spid": "SP0001", "shl": 2 },
                { "lshh": "GHOST", "plh": "9", "spid": "SP0001", "shl": 1 } ] }
            """);
            var docs = await new FixtureKsoaAdapter(dir).FetchAsync("sale", DateTime.MinValue, DateTime.MaxValue, CancellationToken.None);
            var d1 = docs.Single(d => d.SourceDocumentKey == "D1");
            Assert.Single(d1.Lines); // 首条保留
            Assert.Contains(d1.KeyIssues, i => i.IssueKind == "DUPLICATE_LINE_KEY" && i.SourceLineKey == "1");
            var ghost = docs.Single(d => d.SourceDocumentKey == "GHOST");
            Assert.Contains(ghost.KeyIssues, i => i.IssueKind == "ORPHAN_LINE");
            Assert.Empty(ghost.Lines);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task Same_doc_key_across_business_days_flags_cross_day_reuse()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tf-fixture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(dir, "a.json"), """
            { "salemast": [{ "lshh": "R1", "rq": "2026-11-20", "ontime": "08:00:00" }],
              "salemxls": [{ "lshh": "R1", "plh": "1", "spid": "SP0001", "shl": 1 }] }
            """);
            await File.WriteAllTextAsync(Path.Combine(dir, "b.json"), """
            { "salemast": [{ "lshh": "R1", "rq": "2026-11-21", "ontime": "08:00:00" }],
              "salemxls": [{ "lshh": "R1", "plh": "1", "spid": "SP0001", "shl": 1 }] }
            """);
            var docs = await new FixtureKsoaAdapter(dir).FetchAsync("sale", DateTime.MinValue, DateTime.MaxValue, CancellationToken.None);
            Assert.Equal(2, docs.Count);
            Assert.DoesNotContain(docs[0].KeyIssues, i => i.IssueKind == "CROSS_DAY_REUSE");
            Assert.Contains(docs[1].KeyIssues, i => i.IssueKind == "CROSS_DAY_REUSE");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task Real_adapter_throws_not_certified_and_factory_routes_modes()
    {
        var real = SourceAdapterFactory.Create("real", FixtureDir);
        Assert.Equal("ksoa-real-0", real.ContractVersion);
        await Assert.ThrowsAsync<AdapterNotCertifiedException>(
            () => real.FetchAsync("sale", DateTime.MinValue, DateTime.MaxValue, CancellationToken.None));
        Assert.IsType<FixtureKsoaAdapter>(SourceAdapterFactory.Create("fixture", FixtureDir));
        Assert.Throws<ArgumentException>(() => SourceAdapterFactory.Create("anything-else", FixtureDir));
    }

    [Fact]
    public async Task Unknown_entity_throws_argument_exception()
    {
        var adapter = new FixtureKsoaAdapter(FixtureDir);
        await Assert.ThrowsAsync<ArgumentException>(
            () => adapter.FetchAsync("inventory", DateTime.MinValue, DateTime.MaxValue, CancellationToken.None));
    }
}
