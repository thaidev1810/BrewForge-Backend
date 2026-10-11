namespace BrewForge.Application.Abstractions;

/// <summary>
/// Reads the text out of a recipe document somebody uploaded: plain text,
/// Markdown or a Word document. Pictures and layout are not read; what a
/// document says only in a picture is not text and is not transcribed.
/// </summary>
public interface IDocumentTextReader
{
    /// <exception cref="BrewForge.Domain.Common.DomainException">
    /// The file is of a type that is not read, is too large, or is not what its name says.
    /// </exception>
    Task<string> ReadAsync(Stream content, string? fileName, CancellationToken cancellationToken);
}
