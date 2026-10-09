using System.Buffers.Binary;
using System.Buffers.Text;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BrewForge.Application.Abstractions;
using Microsoft.Extensions.Options;

namespace BrewForge.Infrastructure.Notifications;

public sealed class WebPushOptions
{
    public const string Section = "WebPush";

    /// <summary>Who runs this application server, as a <c>mailto:</c> or https address. Push services ask for it.</summary>
    public string Subject { get; set; } = "";

    /// <summary>The VAPID public key: an uncompressed P-256 point, 65 bytes, in base64url.</summary>
    public string PublicKey { get; set; } = "";

    /// <summary>The VAPID private key: 32 bytes, in base64url. A secret.</summary>
    public string PrivateKey { get; set; } = "";

    /// <summary>How long a push service keeps a message for a browser that is offline.</summary>
    public int TtlSeconds { get; set; } = 24 * 60 * 60;
}

/// <summary>
/// The message encryption of Web Push (RFC 8291) in the <c>aes128gcm</c>
/// content coding (RFC 8188): a single record, encrypted for the key pair of
/// one browser with a key pair made for this message alone.
/// </summary>
public static class WebPushEncryption
{
    private const int RecordSize = 4096;

    /// <summary>The largest plaintext that fits the one record a push service must accept.</summary>
    public const int MaxPlaintextBytes = RecordSize - 16 /* tag */ - 1 /* delimiter */ - 86 /* header */;

    public static byte[] Encrypt(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> userAgentPublicKey,
        ReadOnlySpan<byte> authSecret)
    {
        using var sender = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        return Encrypt(plaintext, userAgentPublicKey, authSecret, sender, RandomNumberGenerator.GetBytes(16));
    }

    /// <summary>The same with the two things that are random in real use given, so that it can be checked against the RFC.</summary>
    public static byte[] Encrypt(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> userAgentPublicKey,
        ReadOnlySpan<byte> authSecret, ECDiffieHellman sender, ReadOnlySpan<byte> salt)
    {
        if (plaintext.Length > MaxPlaintextBytes) throw new ArgumentException("The message is too long for one record.", nameof(plaintext));
        if (authSecret.Length != 16) throw new ArgumentException("The authentication secret is 16 bytes.", nameof(authSecret));
        if (salt.Length != 16) throw new ArgumentException("The salt is 16 bytes.", nameof(salt));

        var senderPublic = ExportPoint(sender.ExportParameters(includePrivateParameters: false));
        using var receiver = ECDiffieHellman.Create(ImportPoint(userAgentPublicKey));
        var sharedSecret = sender.DeriveRawSecretAgreement(receiver.PublicKey);

        // RFC 8291 section 3.4: the input keying material binds both public keys.
        var keyInfo = Concat("WebPush: info\0"u8, userAgentPublicKey, senderPublic);
        var ikm = HKDF.DeriveKey(HashAlgorithmName.SHA256, sharedSecret, 32, authSecret.ToArray(), keyInfo);

        // RFC 8188 section 2.2 and 2.3: content encryption key and nonce.
        var prk = HKDF.Extract(HashAlgorithmName.SHA256, ikm, salt.ToArray());
        var key = HKDF.Expand(HashAlgorithmName.SHA256, prk, 16, "Content-Encoding: aes128gcm\0"u8.ToArray());
        var nonce = HKDF.Expand(HashAlgorithmName.SHA256, prk, 12, "Content-Encoding: nonce\0"u8.ToArray());

        // One record, which is also the last: the data, then the delimiter 0x02.
        var record = new byte[plaintext.Length + 1];
        plaintext.CopyTo(record);
        record[^1] = 0x02;
        var ciphertext = new byte[record.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(key, tagSizeInBytes: 16)) aes.Encrypt(nonce, record, ciphertext, tag);

        // Header: salt (16) | record size (4) | key id length (1) | key id, which is the sender's public key (65).
        var body = new byte[16 + 4 + 1 + senderPublic.Length + ciphertext.Length + tag.Length];
        salt.CopyTo(body);
        BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(16), RecordSize);
        body[20] = (byte)senderPublic.Length;
        senderPublic.CopyTo(body.AsSpan(21));
        ciphertext.CopyTo(body.AsSpan(21 + senderPublic.Length));
        tag.CopyTo(body.AsSpan(21 + senderPublic.Length + ciphertext.Length));
        return body;
    }

    /// <summary>An uncompressed P-256 point: 0x04, X, Y.</summary>
    public static ECParameters ImportPoint(ReadOnlySpan<byte> point, ReadOnlySpan<byte> privateKey = default)
    {
        if (point.Length != 65 || point[0] != 0x04) throw new ArgumentException("Not an uncompressed P-256 point.", nameof(point));
        return new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = point.Slice(1, 32).ToArray(), Y = point.Slice(33, 32).ToArray() },
            D = privateKey.IsEmpty ? null : privateKey.ToArray(),
        };
    }

    public static byte[] ExportPoint(ECParameters parameters) =>
        Concat([0x04], parameters.Q.X, parameters.Q.Y);

    private static byte[] Concat(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, ReadOnlySpan<byte> c)
    {
        var all = new byte[a.Length + b.Length + c.Length];
        a.CopyTo(all);
        b.CopyTo(all.AsSpan(a.Length));
        c.CopyTo(all.AsSpan(a.Length + b.Length));
        return all;
    }
}

/// <summary>
/// Sends a message to the push service of a browser: encrypted for that
/// browser (RFC 8291) and signed with the VAPID key of this server (RFC
/// 8292), so the push service knows who is sending without an account.
/// </summary>
public sealed class WebPushSender : IPushSender
{
    private static readonly TimeSpan TokenLifetime = TimeSpan.FromHours(12);

    private readonly HttpClient _http;
    private readonly WebPushOptions _options;
    private readonly TimeProvider _clock;
    private readonly byte[]? _publicKey;
    private readonly byte[]? _privateKey;

    public WebPushSender(HttpClient http, IOptions<WebPushOptions> options, TimeProvider clock)
    {
        _http = http;
        _options = options.Value;
        _clock = clock;
        _publicKey = Decode(_options.PublicKey, bytes: 65);
        _privateKey = Decode(_options.PrivateKey, bytes: 32);
    }

    public bool IsConfigured =>
        _publicKey is not null && _privateKey is not null && !string.IsNullOrWhiteSpace(_options.Subject);

    public string? PublicKey => IsConfigured ? Base64Url.EncodeToString(_publicKey) : null;

    public async Task<PushResult> SendAsync(PushTarget target, string payloadJson, CancellationToken cancellationToken)
    {
        if (!IsConfigured) throw new InvalidOperationException("Web Push is not configured.");

        var endpoint = new Uri(target.Endpoint);
        var body = WebPushEncryption.Encrypt(Encoding.UTF8.GetBytes(payloadJson),
            Base64Url.DecodeFromChars(target.P256dh), Base64Url.DecodeFromChars(target.Auth));

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Content = new ByteArrayContent(body);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        request.Content.Headers.ContentEncoding.Add("aes128gcm");
        request.Headers.TryAddWithoutValidation("TTL", _options.TtlSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
        request.Headers.TryAddWithoutValidation("Authorization",
            $"vapid t={CreateToken(endpoint)}, k={Base64Url.EncodeToString(_publicKey)}");

        using var response = await _http.SendAsync(request, cancellationToken);
        return response.StatusCode switch
        {
            HttpStatusCode.NotFound or HttpStatusCode.Gone => PushResult.Gone,
            _ when response.IsSuccessStatusCode => PushResult.Delivered,
            _ => PushResult.Failed,
        };
    }

    /// <summary>The VAPID token: a JWT for the origin of the push service, signed with ES256.</summary>
    internal string CreateToken(Uri endpoint)
    {
        var header = Base64Url.EncodeToString("""{"typ":"JWT","alg":"ES256"}"""u8);
        var claims = Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(new
        {
            aud = endpoint.GetLeftPart(UriPartial.Authority),
            exp = _clock.GetUtcNow().Add(TokenLifetime).ToUnixTimeSeconds(),
            sub = _options.Subject,
        }));
        var signingInput = $"{header}.{claims}";

        using var key = ECDsa.Create(WebPushEncryption.ImportPoint(_publicKey, _privateKey));
        var signature = key.SignData(Encoding.ASCII.GetBytes(signingInput), HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return $"{signingInput}.{Base64Url.EncodeToString(signature)}";
    }

    private static byte[]? Decode(string? value, int bytes)
    {
        value = value?.Trim();
        // IsValid first: decoding throws on a character that is not base64url.
        if (string.IsNullOrEmpty(value) || value.Length > 128 || !Base64Url.IsValid(value)) return null;
        var buffer = new byte[128];
        return Base64Url.TryDecodeFromChars(value, buffer, out var written) && written == bytes
            ? buffer[..written]
            : null;
    }
}
