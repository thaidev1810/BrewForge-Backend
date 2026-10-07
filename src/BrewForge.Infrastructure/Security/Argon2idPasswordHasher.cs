using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using BrewForge.Application.Abstractions;
using Konscious.Security.Cryptography;
using Microsoft.Extensions.Options;

namespace BrewForge.Infrastructure.Security;

public sealed class Argon2Options
{
    public const string Section = "Argon2";

    /// <summary>Memory cost in KiB. The default follows the OWASP minimum for Argon2id.</summary>
    public int MemoryKib { get; set; } = 19 * 1024;
    public int Iterations { get; set; } = 2;
    public int Parallelism { get; set; } = 1;
}

/// <summary>
/// Argon2id hashes in the PHC string format
/// (<c>$argon2id$v=19$m=19456,t=2,p=1$salt$hash</c>). The parameters travel
/// with each hash, so raising the cost later does not invalidate stored
/// passwords.
/// </summary>
public sealed class Argon2idPasswordHasher(IOptions<Argon2Options> options) : IPasswordHasher
{
    private const int SaltBytes = 16;
    private const int HashBytes = 32;
    private const string Prefix = "$argon2id$v=19$";

    public string Hash(string password)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);
        var o = options.Value;
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Derive(password, salt, o.MemoryKib, o.Iterations, o.Parallelism, HashBytes);

        return string.Create(CultureInfo.InvariantCulture,
            $"{Prefix}m={o.MemoryKib},t={o.Iterations},p={o.Parallelism}${Encode(salt)}${Encode(hash)}");
    }

    public bool Verify(string password, string passwordHash)
    {
        if (string.IsNullOrEmpty(password) || !TryParse(passwordHash, out var parsed)) return false;

        var actual = Derive(password, parsed.Salt, parsed.MemoryKib, parsed.Iterations, parsed.Parallelism,
            parsed.Hash.Length);
        return CryptographicOperations.FixedTimeEquals(actual, parsed.Hash);
    }

    private static byte[] Derive(string password, byte[] salt, int memoryKib, int iterations, int parallelism,
        int length)
    {
        using var argon2 = new Argon2id(Encoding.UTF8.GetBytes(password))
        {
            Salt = salt,
            MemorySize = memoryKib,
            Iterations = iterations,
            DegreeOfParallelism = parallelism,
        };
        return argon2.GetBytes(length);
    }

    private static bool TryParse(string? encoded, out ParsedHash parsed)
    {
        parsed = default;
        if (encoded is null || !encoded.StartsWith(Prefix, StringComparison.Ordinal)) return false;

        // m=..,t=..,p=.. $ salt $ hash
        var parts = encoded[Prefix.Length..].Split('$');
        if (parts.Length != 3) return false;

        int memory = 0, iterations = 0, parallelism = 0;
        foreach (var pair in parts[0].Split(','))
        {
            var kv = pair.Split('=');
            if (kv.Length != 2 || !int.TryParse(kv[1], NumberStyles.None, CultureInfo.InvariantCulture, out var n))
            {
                return false;
            }
            switch (kv[0])
            {
                case "m": memory = n; break;
                case "t": iterations = n; break;
                case "p": parallelism = n; break;
                default: return false;
            }
        }
        if (memory <= 0 || iterations <= 0 || parallelism <= 0) return false;

        try
        {
            parsed = new ParsedHash(memory, iterations, parallelism, Decode(parts[1]), Decode(parts[2]));
            return parsed.Salt.Length > 0 && parsed.Hash.Length > 0;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    // PHC strings use base64 without padding.
    private static string Encode(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=');

    private static byte[] Decode(string text) =>
        Convert.FromBase64String(text.PadRight(text.Length + (4 - text.Length % 4) % 4, '='));

    private readonly record struct ParsedHash(int MemoryKib, int Iterations, int Parallelism, byte[] Salt,
        byte[] Hash);
}
