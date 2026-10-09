using System.IO.Compression;
using System.Text;
using BrewForge.Api.Tests.Infrastructure;
using BrewForge.Application.Sales;
using BrewForge.Domain.Common;
using BrewForge.Infrastructure.Files;

namespace BrewForge.Api.Tests.Slice7;

/// <summary>
/// The reader of POS files, on its own: CSV and XLSX come out as the same
/// lines of text, numbered as the user sees them in the file.
/// </summary>
public sealed class PosFileReaderTests
{
    private static IReadOnlyList<PosFileRow> Csv(string text) => PosFileReader.ReadCsv(Encoding.UTF8.GetBytes(text));

    private static void AssertRows(IReadOnlyList<PosFileRow> rows, params (int Row, string[] Cells)[] expected)
    {
        Assert.Equal(expected.Select(e => e.Row), rows.Select(r => r.RowNumber));
        Assert.All(expected.Zip(rows), pair => Assert.Equal(pair.First.Cells, pair.Second.Cells));
    }

    // ---------------------------------------------------------------- CSV

    [Fact]
    public void Csv_lines_are_numbered_as_in_the_file_and_blank_lines_are_skipped_without_renumbering()
    {
        var rows = Csv("branch_code,drink_code,trading_date,quantity\r\nB01,R07,2026-10-06,143\r\n\r\n  ,, ,\r\nB01,R08,2026-10-06,87");

        AssertRows(rows,
            (1, ["branch_code", "drink_code", "trading_date", "quantity"]),
            (2, ["B01", "R07", "2026-10-06", "143"]),
            (5, ["B01", "R08", "2026-10-06", "87"]));
    }

    [Fact]
    public void Csv_quoted_fields_may_hold_the_separator_a_quote_and_a_line_break()
    {
        var rows = Csv("a,b,c\n\"x, y\",\"say \"\"hi\"\"\",\"two\nlines\"\nlast,,row\n");

        AssertRows(rows,
            (1, ["a", "b", "c"]),
            (2, ["x, y", "say \"hi\"", "two\nlines"]),
            (4, ["last", "", "row"])); // the line break inside the quotes was a line of the file
    }

    [Fact]
    public void Csv_with_a_byte_order_mark_and_semicolons_is_read()
    {
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("branch_code;drink_code\nB01;Trà sữa, size L\n")).ToArray();

        AssertRows(PosFileReader.ReadCsv(bytes),
            (1, ["branch_code", "drink_code"]),
            (2, ["B01", "Trà sữa, size L"])); // a comma is text when the file separates with semicolons
    }

    // ---------------------------------------------------------------- XLSX

    [Fact]
    public void Workbook_cells_are_read_from_shared_strings_numbers_and_date_serials()
    {
        var rows = PosFileReader.ReadXlsx(SalesScenario.Xlsx(
        [
            ["branch_code", "drink_code", "trading_date", "quantity"],
            ["B01", "R07", new DateOnly(2026, 10, 6), 143],
            ["B01", "R&D <test>", "2026-10-07", 151.0m],
        ]));

        AssertRows(rows,
            (1, ["branch_code", "drink_code", "trading_date", "quantity"]),
            (2, ["B01", "R07", "46301", "143"]),          // a date cell holds its day serial
            (3, ["B01", "R&D <test>", "2026-10-07", "151"]));
    }

    [Fact]
    public void Workbook_with_inline_strings_and_gaps_keeps_every_cell_in_its_column_and_every_row_its_number()
    {
        var rows = PosFileReader.ReadXlsx(SalesScenario.Xlsx(
        [
            ["a", "b", "c", "d"],
            [null, null, null, null],   // an empty row of the sheet
            ["B01", null, null, 9],     // B and C left empty: a spreadsheet leaves such cells out
        ], inlineStrings: true));

        AssertRows(rows,
            (1, ["a", "b", "c", "d"]),
            (3, ["B01", "", "", "9"]));
    }

    [Fact]
    public void Workbook_text_in_rich_text_runs_is_joined_and_the_first_sheet_is_found_by_its_relationship()
    {
        // As a spreadsheet program may write it: the sheet part under another name,
        // an absolute target, rich text in the string table, a phonetic guide, scientific notation.
        var workbook = Zip(
            ("xl/workbook.xml", """<workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets><sheet name="Export" sheetId="4" r:id="rId3"/><sheet name="Notes" sheetId="5" r:id="rId4"/></sheets></workbook>"""),
            ("xl/_rels/workbook.xml.rels", """<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId4" Target="worksheets/notes.xml"/><Relationship Id="rId3" Target="/xl/worksheets/export.xml"/></Relationships>"""),
            ("xl/worksheets/notes.xml", """<worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData><row r="1"><c r="A1"><v>999</v></c></row></sheetData></worksheet>"""),
            ("xl/worksheets/export.xml", """<worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData><row r="1"><c r="A1" t="s"><v>0</v></c><c r="B1" t="s"><v>1</v></c><c r="C1" t="str"><v>formula text</v></c><c r="D1"><v>1.43E2</v></c></row></sheetData></worksheet>"""),
            ("xl/sharedStrings.xml", """<sst xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><si><r><t>B</t></r><r><rPr><b/></rPr><t>01</t></r></si><si><t>R07</t><rPh sb="0" eb="1"><t>ignored</t></rPh></si></sst>"""));

        AssertRows(PosFileReader.ReadXlsx(workbook), (1, ["B01", "R07", "formula text", "143"]));
    }

    [Fact]
    public async Task File_that_cannot_be_read_is_refused_with_the_reason()
    {
        var reader = new PosFileReader();
        async Task<string> RefusalOf(byte[] content, string fileName)
        {
            var refusal = await Assert.ThrowsAsync<DomainException>(() =>
                reader.ReadAsync(new MemoryStream(content), fileName, CancellationToken.None));
            Assert.Equal((ErrorKind.Validation, "IMPORT_LAYOUT"), (refusal.Kind, refusal.Code));
            return Assert.Single(refusal.Details, d => d.Field == "file").Issue;
        }

        Assert.Equal("is empty", await RefusalOf([], "pos.csv"));
        Assert.Contains("larger than 5 MB", await RefusalOf(new byte[PosFileReader.MaxBytes + 1], "pos.csv"));
        Assert.Equal("is not a CSV file or an XLSX workbook", await RefusalOf([1, 2, 0, 3], "pos.csv"));
        Assert.Equal("is not a CSV file or an XLSX workbook", await RefusalOf("a,b"u8.ToArray(), "legacy.xls"));
        Assert.Equal("is not a valid XLSX workbook", await RefusalOf([(byte)'P', (byte)'K', 3, 4, 9, 9, 9, 9], "pos.xlsx"));
        Assert.Equal("has no worksheet", await RefusalOf(Zip(("readme.txt", "not a workbook")), "pos.xlsx"));
        // A document type declaration is how an XML part reaches outside itself: not read.
        Assert.Equal("is not a valid XLSX workbook", await RefusalOf(Zip(("xl/worksheets/sheet1.xml",
            """<!DOCTYPE x [<!ENTITY e SYSTEM "file:///etc/passwd">]><worksheet><sheetData><row><c t="str"><v>&e;</v></c></row></sheetData></worksheet>""")), "pos.xlsx"));
    }

    [Fact]
    public async Task Csv_and_workbook_of_the_same_lines_read_the_same()
    {
        var reader = new PosFileReader();
        object?[][] lines = [["branch_code", "drink_code", "trading_date", "quantity"], ["B01", "R07", "2026-10-06", "143"]];

        var fromCsv = await reader.ReadAsync(new MemoryStream(Encoding.UTF8.GetBytes(
            string.Join("\n", lines.Select(line => string.Join(",", line))))), "pos.csv", CancellationToken.None);
        var fromWorkbook = await reader.ReadAsync(new MemoryStream(SalesScenario.Xlsx(lines)), "pos.xlsx", CancellationToken.None);

        Assert.Equal(fromCsv.Select(r => (r.RowNumber, string.Join("|", r.Cells))), fromWorkbook.Select(r => (r.RowNumber, string.Join("|", r.Cells))));
    }

    private static byte[] Zip(params (string Path, string Content)[] parts)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, content) in parts)
            {
                using var writer = new StreamWriter(archive.CreateEntry(path).Open(), new UTF8Encoding(false));
                writer.Write(content);
            }
        }
        return buffer.ToArray();
    }
}
