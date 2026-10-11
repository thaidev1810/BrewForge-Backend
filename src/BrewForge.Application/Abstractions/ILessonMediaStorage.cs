namespace BrewForge.Application.Abstractions;

/// <summary>What the storage kept of a file it was given.</summary>
public sealed record StoredFile(string Key, long SizeBytes, string Sha256);

/// <summary>
/// Where the pictures of lessons are kept. A port, like the storage of the
/// recordings of practicals, and for the same reason: the disk of the server
/// can give way to an object store without the application noticing.
/// </summary>
public interface ILessonMediaStorage
{
    /// <summary>Stores the content under a new key. Answers null, and keeps nothing, when it is longer than <paramref name="maxBytes"/>.</summary>
    Task<StoredFile?> SaveAsync(Stream content, string extension, long maxBytes, CancellationToken cancellationToken);

    /// <summary>The content stored under the key, or null if there is none.</summary>
    Stream? OpenRead(string key);

    Task DeleteAsync(string key, CancellationToken cancellationToken);
}
