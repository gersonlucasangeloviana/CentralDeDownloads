using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using GeradorExcel.Core;
using MongoDB.Bson;

namespace GeradorExcel.Tests;

public class ExportTests
{
    [Fact]
    public void BatchRejectsMoreThanOneHundredItems()
    {
        var body = Encoding.UTF8.GetBytes("{\"id\":\"a\",\"dados\":[" +
            string.Join(',', Enumerable.Repeat("{\"x\":1}", 101)) + "]}");
        Assert.Throws<ArgumentException>(() => BatchValidator.Parse("a", body));
    }

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
