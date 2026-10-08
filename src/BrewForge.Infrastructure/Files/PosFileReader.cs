using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using BrewForge.Application.Sales;
using BrewForge.Domain.Common;

namespace BrewForge.Infrastructure.Files;

/// <summary>
/// Reads the lines of a POS export: a CSV file, or the first sheet of an XLSX
/// workbook. Both come out as the same thing, lines of text cells with the
/// line number the user sees, so that nothing after this point knows which
/// format the file had.
/// </summary>
public sealed class PosFileReader : IPosFileReader
{
    /// <summary>The largest upload that is read.</summary>
    public const int MaxBytes = 5 * 1024 * 1024;

    /// <summary>The largest sheet that is unpacked: an archive of a few kilobytes can claim to hold gigabytes.</summary>
    private const int MaxUnpackedBytes = 64 * 1024 * 1024;

    public async Task<IReadOnlyList<PosFileRow>> ReadAsync(Stream content, string fileName,
        CancellationToken cancellationToken)
    {
        var bytes = await ReadBoundedAsync(content, MaxBytes, cancellationToken)
                    ?? throw Unreadable($"is larger than {MaxBytes / (1024 * 1024)} MB");
        if (bytes.Length == 0) throw Unreadable("is empty");

        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        var isArchive = bytes.Length > 4 && bytes[0] == 'P' && bytes[1] == 'K' && bytes[2] == 3 && bytes[3] == 4;
        if (isArchive) return ReadXlsx(bytes);
        if (extension is ".xlsx" or ".xls" || Array.IndexOf(bytes, (byte)0) >= 0)
        {
            throw Unreadable("is not a CSV file or an XLSX workbook");
        }
        return ReadCsv(bytes);
    }

    // ---------------------------------------------------------------- CSV

    /// <summary>
    /// RFC 4180: quoted fields, doubled quotes, line breaks inside quotes.
    /// The separator is the comma, or the semicolon when the header line uses
    /// it, which is what a spreadsheet writes under a Vietnamese locale.
    /// </summary>
    internal static IReadOnlyList<PosFileRow> ReadCsv(byte[] bytes)
    {
        var text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetString(bytes).TrimStart('﻿');
        var headerEnd = text.IndexOfAny(['\r', '\n']);
        var header = headerEnd < 0 ? text : text[..headerEnd];
        var separator = !header.Contains(',') && header.Contains(';') ? ';' : ',';

        var rows = new List<PosFileRow>();
        var cells = new List<string>();
        var cell = new StringBuilder();
        var (line, startedOn, quoted, anything) = (1, 1, false, false);

        void EndCell()
        {
            cells.Add(cell.ToString());
            cell.Clear();
        }

        void EndRow()
        {
            EndCell();
            if (anything && cells.Any(value => value.Trim().Length > 0)) rows.Add(new PosFileRow(startedOn, [.. cells]));
            cells.Clear();
            anything = false;
        }

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"')
                {
                    cell.Append('"');
                    i++;
                }
                else if (c == '"') quoted = false;
                else
                {
                    if (c == '\n') line++;
                    cell.Append(c);
                }
                continue;
            }

            if (c == '\r') continue;
            if (c == '\n')
            {
                EndRow();
                line++;
                startedOn = line;
                continue;
            }

            anything = true;
            if (c == '"' && cell.Length == 0) quoted = true;
            else if (c == separator) EndCell();
            else cell.Append(c);
        }
        EndRow();
        return rows;
    }

    // ---------------------------------------------------------------- XLSX

    /// <summary>
    /// The first sheet of the workbook. An XLSX file is a ZIP archive of XML
    /// parts: the sheet holds the cells, and text cells point into a table of
    /// shared strings. A date cell holds a day serial, which is passed on as
    /// the number it is.
    /// </summary>
    internal static IReadOnlyList<PosFileRow> ReadXlsx(byte[] bytes)
    {
        try
        {
            using var archive = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
            var shared = SharedStrings(archive);
            var sheet = Load(archive, FirstSheetPath(archive)) ?? throw Unreadable("has no worksheet");

            var rows = new List<PosFileRow>();
            var nextRow = 1;
            foreach (var row in sheet.Descendants().Where(element => element.Name.LocalName == "row"))
            {
                var number = int.TryParse(row.Attribute("r")?.Value, out var declared) ? declared : nextRow;
                nextRow = number + 1;

                var cells = new List<string>();
                foreach (var cell in row.Elements().Where(element => element.Name.LocalName == "c"))
                {
                    var column = ColumnOf(cell.Attribute("r")?.Value) ?? cells.Count;
                    while (cells.Count < column) cells.Add("");
                    if (column < cells.Count) cells[column] = TextOf(cell, shared);
                    else cells.Add(TextOf(cell, shared));
                }
                if (cells.Any(value => value.Trim().Length > 0)) rows.Add(new PosFileRow(number, cells));
            }
            return rows;
        }
        catch (Exception exception) when (exception is InvalidDataException or XmlException or FormatException)
        {
            throw Unreadable("is not a valid XLSX workbook");
        }
    }

    private static string TextOf(XElement cell, IReadOnlyList<string> shared)
    {
        var value = cell.Elements().FirstOrDefault(element => element.Name.LocalName == "v")?.Value ?? "";
        switch (cell.Attribute("t")?.Value)
        {
            case "s":
                return int.TryParse(value, out var index) && index >= 0 && index < shared.Count ? shared[index] : "";
            case "inlineStr":
                return TextRuns(cell.Elements().FirstOrDefault(element => element.Name.LocalName == "is"));
            case "str" or "d" or "b" or "e":
                return value;
            default:
                // A number. 143 may be stored as 143.0 or 1.43E2; written back plainly either way.
                return decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
                    ? number.ToString("0.############", CultureInfo.InvariantCulture)
                    : value;
        }
    }

    private static List<string> SharedStrings(ZipArchive archive) =>
    [
        .. (Load(archive, "xl/sharedStrings.xml")?.Root?.Elements().Where(element => element.Name.LocalName == "si") ?? [])
            .Select(TextRuns),
    ];

    /// <summary>The text of a string item: one run, or several rich-text runs. Phonetic guides are not text.</summary>
    private static string TextRuns(XElement? item) =>
        item is null
            ? ""
            : string.Concat(item.Descendants()
                .Where(element => element.Name.LocalName == "t"
                                  && element.Ancestors().All(ancestor => ancestor.Name.LocalName != "rPh"))
                .Select(element => element.Value));

    /// <summary>The part that holds the first sheet of the workbook, by its relationship.</summary>
    private static string FirstSheetPath(ZipArchive archive)
    {
        var relationshipId = Load(archive, "xl/workbook.xml")?.Descendants()
            .FirstOrDefault(element => element.Name.LocalName == "sheet")?.Attributes()
            .FirstOrDefault(attribute => attribute.Name.LocalName == "id")?.Value;
        var target = relationshipId is null
            ? null
            : Load(archive, "xl/_rels/workbook.xml.rels")?.Descendants()
                .FirstOrDefault(element => element.Name.LocalName == "Relationship"
                                           && element.Attribute("Id")?.Value == relationshipId)
                ?.Attribute("Target")?.Value;

        if (target is not null) return target.StartsWith('/') ? target.TrimStart('/') : $"xl/{target}";
        return archive.Entries.Select(entry => entry.FullName)
                   .Where(name => name.StartsWith("xl/worksheets/", StringComparison.OrdinalIgnoreCase)
                                  && name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                   .Order(StringComparer.OrdinalIgnoreCase).FirstOrDefault()
               ?? "xl/worksheets/sheet1.xml";
    }

    private static XDocument? Load(ZipArchive archive, string path)
    {
        var entry = archive.Entries.FirstOrDefault(e => e.FullName.Equals(path, StringComparison.OrdinalIgnoreCase));
        if (entry is null) return null;
        if (entry.Length > MaxUnpackedBytes) throw Unreadable("is too large to read");

        using var stream = entry.Open();
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
            MaxCharactersInDocument = MaxUnpackedBytes,
        });
        return XDocument.Load(reader);
    }

    /// <summary>"C7" is column 2; "AB3" is column 27.</summary>
    private static int? ColumnOf(string? reference)
    {
        if (string.IsNullOrEmpty(reference)) return null;
        var column = 0;
        foreach (var c in reference.TakeWhile(char.IsAsciiLetter))
        {
            column = column * 26 + (char.ToUpperInvariant(c) - 'A' + 1);
            if (column > 16_384) return null;
        }
        return column == 0 ? null : column - 1;
    }

    // ---------------------------------------------------------------- shared

    /// <summary>The whole stream, or null when it holds more than the limit.</summary>
    private static async Task<byte[]?> ReadBoundedAsync(Stream content, int limit, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await content.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > limit) return null;
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    private static DomainException Unreadable(string issue) =>
        DomainException.Validation(ErrorCodes.ImportLayout, "The file cannot be imported.", [new ErrorDetail("file", issue)]);
}
