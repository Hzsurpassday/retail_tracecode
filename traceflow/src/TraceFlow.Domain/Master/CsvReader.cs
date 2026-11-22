using System.Text;

namespace TraceFlow.Domain.Master;

public sealed class CsvFormatException : Exception
{
    public CsvFormatException(string message) : base(message) { }
}

/// <summary>
/// MD-03 导入 CSV：UTF-8（可带 BOM）、RFC4180 子集（引号/双引号转义/逗号/换行）。
/// 表头必须恰好覆盖 requiredColumns（顺序不限，不允许多余列）。值去首尾空白。
/// </summary>
public static class CsvReader
{
    public static IReadOnlyList<IReadOnlyDictionary<string, string>> Parse(string text, IReadOnlyList<string> requiredColumns)
    {
        if (text.Length > 0 && text[0] == '\uFEFF') text = text[1..];
        var records = SplitRecords(text);
        if (records.Count == 0) throw new CsvFormatException("CSV 为空：缺少表头行");
        var header = records[0];
        foreach (var col in requiredColumns)
            if (!header.Contains(col))
                throw new CsvFormatException($"CSV 表头缺少必需列 {col}");
        if (header.Count != requiredColumns.Count)
            throw new CsvFormatException($"CSV 表头含未定义列（期望 {requiredColumns.Count} 列，实际 {header.Count} 列）");
        var rows = new List<IReadOnlyDictionary<string, string>>();
        for (var i = 1; i < records.Count; i++)
        {
            var fields = records[i];
            if (fields.Count != header.Count)
                throw new CsvFormatException($"CSV 第 {i + 1} 行字段数 {fields.Count} 与表头 {header.Count} 不一致");
            var dict = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var j = 0; j < header.Count; j++) dict[header[j]] = fields[j].Trim();
            rows.Add(dict);
        }
        return rows;
    }

    private static List<List<string>> SplitRecords(string text)
    {
        var records = new List<List<string>>();
        var fields = new List<string>();
        var sb = new StringBuilder();
        var inQuotes = false;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (inQuotes)
            {
                if (ch == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { sb.Append('"'); i++; }
                    else inQuotes = false;
                }
                else sb.Append(ch);
            }
            else if (ch == '"') inQuotes = true;
            else if (ch == ',') { fields.Add(sb.ToString()); sb.Clear(); }
            else if (ch == '\r' || ch == '\n')
            {
                if (ch == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                fields.Add(sb.ToString()); sb.Clear();
                if (fields.Count > 1 || fields[0].Trim().Length > 0) records.Add(fields);
                fields = new List<string>();
            }
            else sb.Append(ch);
        }
        if (inQuotes) throw new CsvFormatException("CSV 引号未闭合");
        if (sb.Length > 0 || fields.Count > 0)
        {
            fields.Add(sb.ToString());
            if (fields.Count > 1 || fields[0].Trim().Length > 0) records.Add(fields);
        }
        return records;
    }
}
