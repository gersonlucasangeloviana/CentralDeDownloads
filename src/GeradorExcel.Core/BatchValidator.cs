using System.Text.Json;
using System.Xml;

namespace GeradorExcel.Core;

public static class BatchValidator
{
    public static BatchInput Parse(string routeId, ReadOnlyMemory<byte> body)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new ArgumentException("O corpo deve ser um objeto JSON.");
        if (!root.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String || id.GetString() != routeId)
            throw new ArgumentException("O campo id deve corresponder ao arquivo da rota.");
        string? batchId = null;
        if (root.TryGetProperty("idLote", out var batchIdElement) && batchIdElement.ValueKind != JsonValueKind.Null)
        {
            if (batchIdElement.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(batchIdElement.GetString()) ||
                batchIdElement.GetString()!.Length > 128)
                throw new ArgumentException("idLote deve ser uma string não vazia.");
            batchId = batchIdElement.GetString();
        }
        if (!root.TryGetProperty("dados", out var data) || data.ValueKind != JsonValueKind.Array)
            throw new ArgumentException("dados deve ser um array.");
        var count = data.GetArrayLength();
        if (count is < 1 or > 100) throw new ArgumentException("dados deve conter de 1 a 100 registros.");

        List<string> columns = [];
        var rowIndex = 0;
        foreach (var row in data.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object) throw new ArgumentException($"dados[{rowIndex}] deve ser um objeto.");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in row.EnumerateObject())
            {
                if (string.IsNullOrWhiteSpace(property.Name) || property.Name.Length > 32767 || !seen.Add(property.Name))
                    throw new ArgumentException($"Campo vazio ou duplicado em dados[{rowIndex}].");
                if (property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array or JsonValueKind.Undefined)
                    throw new ArgumentException($"dados[{rowIndex}].{property.Name} deve ser um valor simples.");
                if (property.Value.ValueKind == JsonValueKind.Null)
                    throw new ArgumentException($"dados[{rowIndex}].{property.Name} não pode ser null; envie texto vazio.");
                if (property.Value.ValueKind == JsonValueKind.String && property.Value.GetString()!.Length > 32767)
                    throw new ArgumentException($"dados[{rowIndex}].{property.Name} excede 32767 caracteres.");
                try
                {
                    XmlConvert.VerifyXmlChars(property.Name);
                    if (property.Value.ValueKind == JsonValueKind.String)
                        XmlConvert.VerifyXmlChars(property.Value.GetString()!);
                }
                catch (XmlException)
                { throw new ArgumentException($"dados[{rowIndex}].{property.Name} contém caracteres inválidos para XLSX."); }
                if (rowIndex == 0) columns.Add(property.Name);
            }
            if (seen.Count == 0) throw new ArgumentException($"dados[{rowIndex}] não pode ser vazio.");
            if (seen.Count > 256) throw new ArgumentException($"dados[{rowIndex}] excede 256 campos.");
            rowIndex++;
        }
        return new BatchInput(routeId, batchId, data.GetRawText(), count, columns);
    }
}
