using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Xml;

namespace GeradorExcel.Core;

public static class ExportFileWriter
{
    public static async Task<long> WriteAsync(ExportJob job, IAsyncEnumerable<ExportBatch> batches,
        string path, CancellationToken ct)
    {
        switch (job.Format)
        {
            case "csv": await WriteCsvAsync(job, batches, path, ct); break;
            case "json": await WriteJsonAsync(batches, path, ct); break;
            case "xlsx": await WriteXlsxAsync(job, batches, path, ct); break;
            default: throw new InvalidOperationException("Formato não suportado.");
        }
        return new FileInfo(path).Length;
    }

    private static async Task WriteCsvAsync(ExportJob job, IAsyncEnumerable<ExportBatch> batches,
        string path, CancellationToken ct)
    {
        await using var stream = File.Create(path);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(true), 65536);
        await writer.WriteLineAsync(string.Join(';', job.Columns.Select(EscapeCsv)));
        await foreach (var batch in batches.WithCancellation(ct))
        {
            using var document = JsonDocument.Parse(batch.DataJson);
            foreach (var row in document.RootElement.EnumerateArray())
            {
                ct.ThrowIfCancellationRequested();
                var values = job.Columns.Select(column =>
                    row.TryGetProperty(column, out var value) ? EscapeCsv(AsText(value)) : "");
                await writer.WriteLineAsync(string.Join(';', values));
            }
        }
    }

    private static async Task WriteJsonAsync(IAsyncEnumerable<ExportBatch> batches,
        string path, CancellationToken ct)
    {
        await using var stream = File.Create(path);
        using var writer = new Utf8JsonWriter(stream);
        writer.WriteStartArray();
        await foreach (var batch in batches.WithCancellation(ct))
        {
            using var document = JsonDocument.Parse(batch.DataJson);
            foreach (var row in document.RootElement.EnumerateArray())
            {
                ct.ThrowIfCancellationRequested();
                row.WriteTo(writer);
            }
            await writer.FlushAsync(ct);
        }
        writer.WriteEndArray();
        await writer.FlushAsync(ct);
    }

    private static async Task WriteXlsxAsync(ExportJob job, IAsyncEnumerable<ExportBatch> batches,
        string path, CancellationToken ct)
    {
        await using var stream = File.Create(path);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);
        using var xlsx = new XlsxStreamWriter(archive, job.Columns);
        await foreach (var batch in batches.WithCancellation(ct))
        {
            using var document = JsonDocument.Parse(batch.DataJson);
            foreach (var row in document.RootElement.EnumerateArray())
            {
                ct.ThrowIfCancellationRequested();
                xlsx.WriteRow(job.Columns.Select(column =>
                    row.TryGetProperty(column, out var value) ? AsText(value) : ""));
            }
        }
    }

    private static string AsText(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? "",
        JsonValueKind.Null => "",
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => value.GetRawText()
    };

    private static string EscapeCsv(string value)
    {
        if (value.Length > 0 && "=+-@\t\r\n".Contains(value[0])) value = "'" + value;
        if (value.Contains(';') || value.Contains('"') || value.Contains('\r') || value.Contains('\n'))
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        return value;
    }

    private sealed class XlsxStreamWriter : IDisposable
    {
        private const string MainNs = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        private const string RelNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        private readonly ZipArchive _archive;
        private readonly IReadOnlyList<string> _columns;
        private XmlWriter? _sheet;
        private int _sheetNumber;
        private int _rowNumber;

        public XlsxStreamWriter(ZipArchive archive, IReadOnlyList<string> columns)
        {
            _archive = archive;
            _columns = columns;
            StartSheet();
        }

        public void WriteRow(IEnumerable<string> values)
        {
            if (_rowNumber >= 1_048_576)
            {
                EndSheet();
                StartSheet();
            }
            WriteCells(values, bold: false);
        }

        private void StartSheet()
        {
            _sheetNumber++;
            _rowNumber = 0;
            var entry = _archive.CreateEntry($"xl/worksheets/sheet{_sheetNumber}.xml", CompressionLevel.Fastest);
            _sheet = XmlWriter.Create(entry.Open(), new XmlWriterSettings { Encoding = new UTF8Encoding(false), CloseOutput = true });
            _sheet.WriteStartDocument();
            _sheet.WriteStartElement("worksheet", MainNs);
            _sheet.WriteStartElement("sheetData", MainNs);
            WriteCells(_columns, bold: true);
        }

        private void WriteCells(IEnumerable<string> values, bool bold)
        {
            var xml = _sheet!;
            xml.WriteStartElement("row", MainNs);
            xml.WriteAttributeString("r", (++_rowNumber).ToString());
            foreach (var value in values)
            {
                xml.WriteStartElement("c", MainNs);
                xml.WriteAttributeString("t", "inlineStr");
                if (bold) xml.WriteAttributeString("s", "1");
                xml.WriteStartElement("is", MainNs);
                xml.WriteStartElement("t", MainNs);
                xml.WriteAttributeString("xml", "space", null, "preserve");
                xml.WriteString(value);
                xml.WriteEndElement();
                xml.WriteEndElement();
                xml.WriteEndElement();
            }
            xml.WriteEndElement();
        }

        private void EndSheet()
        {
            _sheet!.WriteEndElement(); // sheetData
            _sheet.WriteEndElement(); // worksheet
            _sheet.WriteEndDocument();
            _sheet.Dispose();
            _sheet = null;
        }

        public void Dispose()
        {
            EndSheet();
            WritePackageParts();
        }

        private void WritePackageParts()
        {
            WriteEntry("_rels/.rels", xml =>
            {
                xml.WriteStartElement("Relationships", "http://schemas.openxmlformats.org/package/2006/relationships");
                xml.WriteStartElement("Relationship");
                xml.WriteAttributeString("Id", "rId1");
                xml.WriteAttributeString("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument");
                xml.WriteAttributeString("Target", "xl/workbook.xml");
                xml.WriteEndElement(); xml.WriteEndElement();
            });
            WriteEntry("xl/workbook.xml", xml =>
            {
                xml.WriteStartElement("workbook", MainNs);
                xml.WriteAttributeString("xmlns", "r", null, RelNs);
                xml.WriteStartElement("sheets", MainNs);
                for (var i = 1; i <= _sheetNumber; i++)
                {
                    xml.WriteStartElement("sheet", MainNs);
                    xml.WriteAttributeString("name", $"Dados {i}");
                    xml.WriteAttributeString("sheetId", i.ToString());
                    xml.WriteAttributeString("r", "id", RelNs, $"rId{i}");
                    xml.WriteEndElement();
                }
                xml.WriteEndElement(); xml.WriteEndElement();
            });
            WriteEntry("xl/_rels/workbook.xml.rels", xml =>
            {
                xml.WriteStartElement("Relationships", "http://schemas.openxmlformats.org/package/2006/relationships");
                for (var i = 1; i <= _sheetNumber; i++)
                {
                    xml.WriteStartElement("Relationship");
                    xml.WriteAttributeString("Id", $"rId{i}");
                    xml.WriteAttributeString("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet");
                    xml.WriteAttributeString("Target", $"worksheets/sheet{i}.xml");
                    xml.WriteEndElement();
                }
                xml.WriteStartElement("Relationship");
                xml.WriteAttributeString("Id", $"rId{_sheetNumber + 1}");
                xml.WriteAttributeString("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles");
                xml.WriteAttributeString("Target", "styles.xml");
                xml.WriteEndElement(); xml.WriteEndElement();
            });
            WriteEntry("xl/styles.xml", xml =>
            {
                xml.WriteStartElement("styleSheet", MainNs);
                xml.WriteRaw("<fonts count=\"2\"><font><sz val=\"11\"/><name val=\"Calibri\"/></font><font><b/><sz val=\"11\"/><name val=\"Calibri\"/></font></fonts>");
                xml.WriteRaw("<fills count=\"2\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill></fills>");
                xml.WriteRaw("<borders count=\"1\"><border><left/><right/><top/><bottom/><diagonal/></border></borders>");
                xml.WriteRaw("<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>");
                xml.WriteRaw("<cellXfs count=\"2\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/><xf numFmtId=\"0\" fontId=\"1\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyFont=\"1\"/></cellXfs>");
                xml.WriteEndElement();
            });
            WriteEntry("[Content_Types].xml", xml =>
            {
                xml.WriteStartElement("Types", "http://schemas.openxmlformats.org/package/2006/content-types");
                xml.WriteStartElement("Default"); xml.WriteAttributeString("Extension", "rels");
                xml.WriteAttributeString("ContentType", "application/vnd.openxmlformats-package.relationships+xml"); xml.WriteEndElement();
                xml.WriteStartElement("Default"); xml.WriteAttributeString("Extension", "xml");
                xml.WriteAttributeString("ContentType", "application/xml"); xml.WriteEndElement();
                WriteOverride(xml, "/xl/workbook.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml");
                WriteOverride(xml, "/xl/styles.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml");
                for (var i = 1; i <= _sheetNumber; i++)
                    WriteOverride(xml, $"/xl/worksheets/sheet{i}.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml");
                xml.WriteEndElement();
            });
        }

        private static void WriteOverride(XmlWriter xml, string name, string contentType)
        {
            xml.WriteStartElement("Override");
            xml.WriteAttributeString("PartName", name);
            xml.WriteAttributeString("ContentType", contentType);
            xml.WriteEndElement();
        }

        private void WriteEntry(string name, Action<XmlWriter> action)
        {
            var entry = _archive.CreateEntry(name, CompressionLevel.Fastest);
            using var xml = XmlWriter.Create(entry.Open(), new XmlWriterSettings
            { Encoding = new UTF8Encoding(false), CloseOutput = true });
            xml.WriteStartDocument();
            action(xml);
            xml.WriteEndDocument();
        }
    }
}
