using System.Globalization;
using System.IO.Compression;
using System.Security;
using System.Text;
using BrewForge.Application.Audit;

namespace BrewForge.Infrastructure.Files;

/// <summary>
/// Writes a table as CSV or as an XLSX workbook with one sheet. PDF is out
/// of scope for release 1.0.
/// </summary>
public sealed class ReportExporter : IReportExporter
{
    /// <summary>
    /// UTF-8 with a byte order mark, so that a spreadsheet opens Vietnamese
    /// names correctly. A text cell that begins like a formula is written
    /// with a leading apostrophe: a name typed into a form must never be run
    /// by the program that opens the export.
    /// </summary>
    public byte[] ToCsv(TabularReport report)
    {
        var text = new StringBuilder();
        void Line(IEnumerable<object?> cells) => text.AppendJoin(',', cells.Select(Csv)).Append("\r\n");

        Line(report.Columns);
        foreach (var row in report.Rows) Line(row);
        return [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(text.ToString())];
    }

    /// <summary>
    /// An XLSX file is a ZIP archive of XML parts. Text is written as inline
    /// strings, which a spreadsheet never evaluates, and numbers as numbers.
    /// </summary>
    public byte[] ToXlsx(TabularReport report)
    {
        var sheet = new StringBuilder(
            """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData>""");
        var rowNumber = 0;
        void Row(IReadOnlyList<object?> cells)
        {
            rowNumber++;
            sheet.Append(CultureInfo.InvariantCulture, $"""<row r="{rowNumber}">""");
            for (var column = 0; column < cells.Count; column++)
            {
                var reference = $"{ColumnName(column)}{rowNumber}";
                switch (cells[column])
                {
                    case null:
                        break;
                    case sbyte or byte or short or ushort or int or uint or long or ulong or float or double or decimal:
                        sheet.Append(CultureInfo.InvariantCulture,
                            $"""<c r="{reference}"><v>{Convert.ToString(cells[column], CultureInfo.InvariantCulture)}</v></c>""");
                        break;
                    case var value:
                        sheet.Append(CultureInfo.InvariantCulture,
                            $"""<c r="{reference}" t="inlineStr"><is><t xml:space="preserve">{Xml(Text(value))}</t></is></c>""");
                        break;
                }
            }
            sheet.Append("</row>");
        }

        Row([.. report.Columns]);
        foreach (var row in report.Rows) Row(row);
        sheet.Append("</sheetData></worksheet>");

        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Part(string path, string xml)
            {
                using var writer = new StreamWriter(archive.CreateEntry(path, CompressionLevel.Optimal).Open(), new UTF8Encoding(false));
                writer.Write(xml);
            }

            Part("[Content_Types].xml",
                """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/><Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/></Types>""");
            Part("_rels/.rels",
                """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/></Relationships>""");
            Part("xl/workbook.xml",
                $"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets><sheet name="{Xml(SheetName(report.Name))}" sheetId="1" r:id="rId1"/></sheets></workbook>""");
            Part("xl/_rels/workbook.xml.rels",
                """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/></Relationships>""");
            Part("xl/worksheets/sheet1.xml", sheet.ToString());
        }
        return buffer.ToArray();
    }

    private static string Csv(object? cell)
    {
        if (cell is null) return "";
        var text = Text(cell);
        if (cell is string && text.Length > 0 && text[0] is '=' or '+' or '-' or '@' or '\t' or '\r') text = "'" + text;
        return text.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? $"\"{text.Replace("\"", "\"\"")}\"" : text;
    }

    private static string Text(object value) => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";

    /// <summary>Escaped for XML, without the control characters XML 1.0 cannot carry at all.</summary>
    private static string Xml(string text) =>
        SecurityElement.Escape(new string([.. text.Where(c => c is '\t' or '\n' or '\r' || c >= ' ')])) ?? "";

    /// <summary>A sheet name may have at most 31 characters and none of <c>: \ / ? * [ ]</c>.</summary>
    private static string SheetName(string name)
    {
        var clean = new string([.. name.Where(c => c is not (':' or '\\' or '/' or '?' or '*' or '[' or ']'))]).Trim();
        return clean.Length == 0 ? "Sheet1" : clean[..Math.Min(31, clean.Length)];
    }

    /// <summary>0 is "A", 25 is "Z", 26 is "AA".</summary>
    private static string ColumnName(int index)
    {
        var name = "";
        for (index++; index > 0; index = (index - 1) / 26) name = (char)('A' + (index - 1) % 26) + name;
        return name;
    }
}
