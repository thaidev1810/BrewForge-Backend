using System.Security.Cryptography;
using BrewForge.Application.Abstractions;
using Microsoft.Extensions.Options;

namespace BrewForge.Infrastructure.Files;

public sealed class StorageOptions
{
    public const string Section = "Storage";

    /// <summary>The folder the recordings of practicals are kept in. A relative path starts at the working directory.</summary>
    public string PracticalVideoRoot { get; set; } = "storage/practical-videos";
}

/// <summary>
/// Keeps the recordings on the disk of the server, one file per recording
/// under a key the storage chooses itself: nothing a client sent is ever
/// part of a path.
/// </summary>
public sealed class LocalPracticalVideoStorage(IOptions<StorageOptions> options) : IPracticalVideoStorage
{
    private readonly string _root = Path.GetFullPath(options.Value.PracticalVideoRoot);

    public async Task<StoredVideo?> SaveAsync(Stream content, string extension, long maxBytes,
        CancellationToken cancellationToken)
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
        return new StoredVideo(key, size, Convert.ToHexStringLower(hash.GetHashAndReset()));
    }

    public Stream? OpenRead(string key)
    {
        var path = PathOf(key);
        return File.Exists(path)
            ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 81920, useAsync: true)
            : null;
    }

    public Task DeleteAsync(string key, CancellationToken cancellationToken)
    {
        File.Delete(PathOf(key));
        return Task.CompletedTask;
    }

    private string PathOf(string key)
    {
        var path = Path.GetFullPath(Path.Combine(_root, key));
        // Keys are made here, but a key read back from the database is still not allowed to leave the root.
        if (!path.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The storage key does not name a file of the video storage.");
        }
        return path;
    }
}
