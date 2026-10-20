using System.Security.Cryptography;
using BrewForge.Application.Abstractions;
using Microsoft.Extensions.Options;

namespace BrewForge.Infrastructure.Files;

public sealed class StorageOptions
{
    public const string Section = "Storage";

    /// <summary>The folder the recordings of practicals are kept in. A relative path starts at the working directory.</summary>
    public string PracticalVideoRoot { get; set; } = "storage/practical-videos";

    /// <summary>The folder the pictures of lessons are kept in. A relative path starts at the working directory.</summary>
    public string LessonMediaRoot { get; set; } = "storage/lesson-media";
}

/// <summary>
/// Keeps files on the disk of the server under one root, each under a key
/// the store chooses itself: nothing a client sent is ever part of a path.
/// </summary>
internal sealed class LocalFileStore(string root)
{
    private readonly string _root = Path.GetFullPath(root);

    /// <summary>Null, and nothing kept, as soon as the content turns out to be longer than <paramref name="maxBytes"/>.</summary>
    public async Task<(string Key, long SizeBytes, string Sha256)?> SaveAsync(Stream content, string extension,
        long maxBytes, CancellationToken cancellationToken)
    {
        var key = $"{DateTime.UtcNow:yyyy/MM}/{Guid.NewGuid():N}{extension}";
        var path = PathOf(key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        long size = 0;
        var tooLong = false;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        try
        {
            await using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             bufferSize: 81920, useAsync: true))
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await content.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    size += read;
                    if (size > maxBytes)
                    {
                        tooLong = true;
                        break;
                    }
                    hash.AppendData(buffer, 0, read);
                    await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }
            }
        }
        catch
        {
            File.Delete(path);
            throw;
        }

        if (tooLong)
        {
            File.Delete(path);
            return null;
        }
        return (key, size, Convert.ToHexStringLower(hash.GetHashAndReset()));
    }

    public Stream? OpenRead(string key)
    {
        var path = PathOf(key);
        return File.Exists(path)
            ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 81920, useAsync: true)
            : null;
    }

    public void Delete(string key) => File.Delete(PathOf(key));

    private string PathOf(string key)
    {
        var path = Path.GetFullPath(Path.Combine(_root, key));
        // Keys are made here, but a key read back from the database is still not allowed to leave the root.
        if (!path.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The storage key does not name a file of this storage.");
        }
        return path;
    }
}

/// <summary>The recordings of practicals, on the disk of the server.</summary>
public sealed class LocalPracticalVideoStorage(IOptions<StorageOptions> options) : IPracticalVideoStorage
{
    private readonly LocalFileStore _store = new(options.Value.PracticalVideoRoot);

    public async Task<StoredVideo?> SaveAsync(Stream content, string extension, long maxBytes,
        CancellationToken cancellationToken) =>
        await _store.SaveAsync(content, extension, maxBytes, cancellationToken) is { } stored
            ? new StoredVideo(stored.Key, stored.SizeBytes, stored.Sha256)
            : null;

    public Stream? OpenRead(string key) => _store.OpenRead(key);

    public Task DeleteAsync(string key, CancellationToken cancellationToken)
    {
        _store.Delete(key);
        return Task.CompletedTask;
    }
}

/// <summary>The pictures of lessons, on the disk of the server.</summary>
public sealed class LocalLessonMediaStorage(IOptions<StorageOptions> options) : ILessonMediaStorage
{
    private readonly LocalFileStore _store = new(options.Value.LessonMediaRoot);

    public async Task<StoredFile?> SaveAsync(Stream content, string extension, long maxBytes,
        CancellationToken cancellationToken) =>
        await _store.SaveAsync(content, extension, maxBytes, cancellationToken) is { } stored
            ? new StoredFile(stored.Key, stored.SizeBytes, stored.Sha256)
            : null;

    public Stream? OpenRead(string key) => _store.OpenRead(key);

    public Task DeleteAsync(string key, CancellationToken cancellationToken)
    {
        _store.Delete(key);
        return Task.CompletedTask;
    }
}
