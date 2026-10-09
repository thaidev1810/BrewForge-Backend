namespace BrewForge.Application.Abstractions;

/// <summary>What the storage kept of a file it was given.</summary>
public sealed record StoredVideo(string Key, long SizeBytes, string Sha256);

/// <summary>
/// Where the recordings of practicals are kept. A port, so that the disk of
/// the server can give way to an object store without the application
/// noticing.
/// </summary>
public interface IPracticalVideoStorage
{
    /// <summary>
    /// Stores the content under a new key. Stops, keeps nothing and answers
    /// null as soon as the content turns out to be longer than
    /// <paramref name="maxBytes"/>: the length a client announces is not
    /// trusted.
    /// </summary>
    Task<StoredVideo?> SaveAsync(Stream content, string extension, long maxBytes, CancellationToken cancellationToken);

    /// <summary>The content stored under the key, or null if there is none.</summary>
    Stream? OpenRead(string key);

    Task DeleteAsync(string key, CancellationToken cancellationToken);
}
