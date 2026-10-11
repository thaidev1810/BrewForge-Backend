using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using BrewForge.Application.Abstractions;
using BrewForge.Domain.Common;

namespace BrewForge.Infrastructure.Files;

/// <summary>
/// Plain text and Markdown are read as UTF-8. A Word document is read
/// without a document library: it is a zip archive whose main part is XML,
/// and the text is that of its paragraphs in order, with the cells of a
/// table row on one line.
/// </summary>
public sealed class DocumentTextReader : IDocumentTextReader
{
    /// <summary>The largest file that is read.</summary>
    public const int MaxBytes = 2 * 1024 * 1024;

    /// <summary>The largest main part of a Word document that is unpacked: a small archive can hold a very large one.</summary>
    private const long MaxUnpackedBytes = 16 * 1024 * 1024;

    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    public async Task<string> ReadAsync(Stream content, string? fileName, CancellationToken cancellationToken)
    {
        var extension = Path.GetExtension(fileName?.Trim() ?? "").ToLowerInvariant();
        new FieldErrors()
            .Check(extension is ".txt" or ".md" or ".docx", "file", "must be a .txt, .md or .docx document")
            .ThrowIfAny();

        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await content.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > MaxBytes) throw Refused($"is larger than {MaxBytes / (1024 * 1024)} MB");
            buffer.Write(chunk, 0, read);
        }
        if (buffer.Length == 0) throw Refused("is empty");
        buffer.Position = 0;

        return extension == ".docx" ? ReadWord(buffer) : ReadPlain(buffer);
    }

    private static string ReadPlain(MemoryStream buffer)
    {
        try
        {
            // Strict: bytes that are not UTF-8 are a file of another kind, not text with odd characters.
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(buffer.GetBuffer(), 0, (int)buffer.Length).TrimStart('﻿');
        }
        catch (DecoderFallbackException)
        {
            throw Refused("is not UTF-8 text");
        }
    }

    private static string ReadWord(MemoryStream buffer)
    {
        try
        {
            using var archive = new ZipArchive(buffer, ZipArchiveMode.Read, leaveOpen: true);
            var part = archive.GetEntry("word/document.xml") ?? throw Refused("is not a Word document");
            if (part.Length > MaxUnpackedBytes) throw Refused("is too large to read");

            using var stream = part.Open();
            using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            var body = XDocument.Load(reader).Root?.Element(W + "body") ?? throw Refused("is not a Word document");

            var text = new StringBuilder();
            foreach (var block in body.Elements())
            {
                if (block.Name == W + "p")
                {
                    text.AppendLine(ParagraphText(block));
                }
                else if (block.Name == W + "tbl")
                {
                    foreach (var row in block.Elements(W + "tr"))
                    {
                        text.AppendLine(string.Join(" | ", row.Elements(W + "tc")
                            .Select(cell => string.Join(" ", cell.Descendants(W + "p").Select(ParagraphText)).Trim())));
                    }
                }
            }
            return text.ToString();
        }
        catch (Exception exception) when (exception is InvalidDataException or XmlException)
        {
            throw Refused("is not a Word document");
        }
    }

    private static string ParagraphText(XElement paragraph)
    {
        var text = new StringBuilder();
        foreach (var node in paragraph.Descendants())
        {
            if (node.Name == W + "t") text.Append(node.Value);
            else if (node.Name == W + "tab") text.Append('\t');
            else if (node.Name == W + "br" || node.Name == W + "cr") text.Append('\n');
        }
        return text.ToString();
    }

    private static DomainException Refused(string issue) =>
        DomainException.Validation("The document could not be read.", new ErrorDetail("file", issue));
}
