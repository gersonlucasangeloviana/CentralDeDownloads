using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using CentralDeDownloads.Core;
using MongoDB.Bson;

namespace CentralDeDownloads.Tests;

public class ExportTests
{
    [Fact]
    public void BatchAcceptsOneThousandAndRejectsMore()
    {
        static byte[] Body(int count) => Encoding.UTF8.GetBytes("{\"id\":\"a\",\"dados\":[" +
            string.Join(',', Enumerable.Repeat("{\"x\":1}", count)) + "]}");
        Assert.Equal(1000, BatchValidator.Parse("a", Body(1000)).Count);
        Assert.Throws<ArgumentException>(() => BatchValidator.Parse("a", Body(1001)));
    }

    [Theory]
    [InlineData("xlsx", 1_000_000, "xlsx")]
    [InlineData("xlsx", 1_000_001, "csv")]
    [InlineData("csv", 1_000_001, "csv")]
    [InlineData("json", 1_000_001, "json")]
    public void LargeXlsxBecomesCsv(string requested, long items, string expected) =>
        Assert.Equal(expected, ExportFormatPolicy.AfterClose(requested, items));

    [Fact]
    public void BatchRejectsNestedValuesAndPreservesColumnOrder()
    {
        Assert.Throws<ArgumentException>(() => BatchValidator.Parse("a",
            Encoding.UTF8.GetBytes("{\"id\":\"a\",\"dados\":[{\"x\":[1]}]}")));
        var batch = BatchValidator.Parse("a", Encoding.UTF8.GetBytes(
            "{\"id\":\"a\",\"idLote\":\"um\",\"dados\":[{\"b\":2,\"a\":1}]}"));
        Assert.Equal(["b", "a"], batch.Columns);
        Assert.Equal("um", batch.BatchId);
    }

    [Fact]
    public void MongoJobSerializesDates()
    {
        var job = new ExportJob { PeriodStart = new DateOnly(2026, 9, 1), PeriodEnd = new DateOnly(2026, 9, 30) };
        Assert.NotEmpty(job.ToBson());
    }

    [Theory]
    [InlineData("csv")]
    [InlineData("json")]
    [InlineData("xlsx")]
    public async Task WritesUsableFiles(string format)
    {
        var path = Path.Combine(Path.GetTempPath(), $"gerador-test-{Guid.NewGuid():N}.{format}");
        try
        {
            var job = new ExportJob { Format = format, Columns = ["Pedido", "Texto"] };
            await ExportFileWriter.WriteAsync(job, Batches(), path, CancellationToken.None);
            Assert.True(new FileInfo(path).Length > 0);
            if (format == "csv")
            {
                var content = await File.ReadAllTextAsync(path);
                Assert.Contains("Pedido;Texto", content);
                Assert.Contains("'=1+1", content);
            }
            else if (format == "json")
            {
                using var json = JsonDocument.Parse(await File.ReadAllTextAsync(path));
                Assert.Equal(2, json.RootElement.GetArrayLength());
            }
            else
            {
                using var xlsx = SpreadsheetDocument.Open(path, false);
                var workbookPart = Assert.IsType<WorkbookPart>(xlsx.WorkbookPart);
                var workbook = Assert.IsType<Workbook>(workbookPart.Workbook);
                var sheets = Assert.IsType<Sheets>(workbook.Sheets);
                Assert.Single(sheets.Elements<Sheet>());
                var sheet = Assert.IsType<Worksheet>(workbookPart.WorksheetParts.Single().Worksheet);
                Assert.Equal(3, sheet.Descendants<Row>().Count());
                Assert.Equal("1", sheet.Descendants<Row>().First().Descendants<Cell>().First().StyleIndex?.ToString());
            }
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("csv")]
    [InlineData("json")]
    [InlineData("xlsx")]
    public async Task WritesToNonSeekableStream(string format)
    {
        using var data = new MemoryStream();
        using var output = new NonSeekableOutput(data);
        await ExportFileWriter.WriteAsync(new ExportJob { Format = format, Columns = ["Pedido", "Texto"] },
            Batches(), output, CancellationToken.None);
        Assert.True(data.Length > 0);
        if (format == "xlsx")
        {
            using var xlsx = SpreadsheetDocument.Open(new MemoryStream(data.ToArray()), false);
            Assert.Single(xlsx.WorkbookPart!.Workbook!.Sheets!.Elements<Sheet>());
        }
        else if (format == "json") Assert.Equal(2, JsonDocument.Parse(data.ToArray()).RootElement.GetArrayLength());
        else Assert.Contains("Pedido;Texto", Encoding.UTF8.GetString(data.ToArray()));
    }

    private sealed class NonSeekableOutput(MemoryStream inner) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken ct) => inner.FlushAsync(ct);
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        public override void Write(ReadOnlySpan<byte> buffer) => inner.Write(buffer);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) =>
            inner.WriteAsync(buffer, ct);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    [Fact]
    public void SignedDownloadLinkRejectsTamperingAndExpiry()
    {
        var links = new DownloadLinks(new string('x', 32));
        var token = links.Create("job123", DateTime.UtcNow.AddMinutes(5));
        Assert.Equal("job123", links.Validate(token));
        Assert.Null(links.Validate(token + "tampered"));
        Assert.Null(links.Validate(links.Create("job123", DateTime.UtcNow.AddSeconds(-1))));
    }

    [Theory]
    [InlineData("http://example.com/webhook")]
    [InlineData("https://127.0.0.1/webhook")]
    [InlineData("https://169.254.169.254/webhook")]
    [InlineData("https://192.168.1.1/webhook")]
    public async Task WebhookRejectsUnsafeDestinations(string url)
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            NotificationSender.ValidatePublicWebhookAsync(url, CancellationToken.None));
    }

    [Fact]
    public void FileKeyDoesNotDependOnUserSuppliedName()
    {
        Assert.Equal("arquivos/job123/arquivo", S3ExportStorage.ObjectKey("job123"));
    }

    private static async IAsyncEnumerable<ExportBatch> Batches(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        yield return new ExportBatch { DataJson = "[{\"Pedido\":\"1\",\"Texto\":\"=1+1\"},{\"Pedido\":\"2\",\"Texto\":\"Olá; mundo\"}]", ItemCount = 2 };
        await Task.CompletedTask;
    }
}
