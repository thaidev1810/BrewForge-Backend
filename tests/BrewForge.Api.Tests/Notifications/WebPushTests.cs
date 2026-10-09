using System.Buffers.Binary;
using System.Buffers.Text;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BrewForge.Application.Abstractions;
using BrewForge.Infrastructure.Notifications;
using Microsoft.Extensions.Options;

namespace BrewForge.Api.Tests.Notifications;

/// <summary>
/// The two adapters that deliver notifications, without a database: the
/// message encryption and the VAPID signature of Web Push against their
/// RFCs, and e-mail into a pickup folder.
/// </summary>
public sealed class WebPushTests
{
    // RFC 8291, appendix A: the push message used as the example throughout the RFC.
    private const string RfcPlaintext = "When I grow up, I want to be a watermelon";
    private const string RfcSenderPrivate = "yfWPiYE-n46HLnH0KqZOF1fJJU3MYrct3AELtAQ-oRw";
    private const string RfcSenderPublic = "BP4z9KsN6nGRTbVYI_c7VJSPQTBtkgcy27mlmlMoZIIgDll6e3vCYLocInmYWAmS6TlzAC8wEqKK6PBru3jl7A8";
    private const string RfcReceiverPrivate = "q1dXpw3UpT5VOmu_cf_v6ih07Aems3njxI-JWgLcM94";
    private const string RfcReceiverPublic = "BCVxsr7N_eNgVRqvHtD0zTZsEc6-VV-JvLexhqUzORcxaOzi6-AYWXvTBHm4bjyPjs7Vd8pZGH6SRpkNtoIAiw4";
    private const string RfcAuthSecret = "BTBZMqHH6r4Tts7J_aSIgg";
    private const string RfcSalt = "DGv6ra1nlYgDCS1FRnbzlw";
    private const string RfcMessage =
        "DGv6ra1nlYgDCS1FRnbzlwAAEABBBP4z9KsN6nGRTbVYI_c7VJSPQTBtkgcy27mlmlMoZIIgDll6e3vCYLocInmYWAmS6TlzAC8wEqKK6PBru3jl7A_yl95bQpu6cVPTpK4Mqgkf1CXztLVBSt2Ks3oZwbuwXPXLWyouBWLVWGNWQexSgSxsj_Qulcy4a-fN";

    [Fact]
    public void Message_is_encrypted_exactly_as_the_example_of_RFC_8291()
    {
        using var sender = ECDiffieHellman.Create(WebPushEncryption.ImportPoint(
            Base64Url.DecodeFromChars(RfcSenderPublic), Base64Url.DecodeFromChars(RfcSenderPrivate)));

        var message = WebPushEncryption.Encrypt(Encoding.UTF8.GetBytes(RfcPlaintext),
            Base64Url.DecodeFromChars(RfcReceiverPublic), Base64Url.DecodeFromChars(RfcAuthSecret), sender,
            Base64Url.DecodeFromChars(RfcSalt));

        Assert.Equal(RfcMessage, Base64Url.EncodeToString(message));
    }

    [Fact]
    public void Message_can_be_read_by_the_browser_it_was_encrypted_for_and_by_no_other()
    {
        using var browser = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        using var stranger = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var browserPublic = WebPushEncryption.ExportPoint(browser.ExportParameters(false));
        var auth = RandomNumberGenerator.GetBytes(16);
        var plaintext = Encoding.UTF8.GetBytes("""{"title":"Course assigned","body":"Trà sữa ô long, hạn 2026-10-23"}""");

        var first = WebPushEncryption.Encrypt(plaintext, browserPublic, auth);
        var second = WebPushEncryption.Encrypt(plaintext, browserPublic, auth);

        Assert.Equal(plaintext, Decrypt(first, browser, browserPublic, auth));
        // A key pair and a salt of its own for every message: the same text never looks the same twice.
        Assert.NotEqual(first, second);
        Assert.ThrowsAny<CryptographicException>(() => Decrypt(first, stranger, browserPublic, auth));
        Assert.ThrowsAny<CryptographicException>(() => Decrypt(first, browser, browserPublic, RandomNumberGenerator.GetBytes(16)));
    }

    [Fact]
    public void Message_longer_than_one_record_is_refused()
    {
        using var browser = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var browserPublic = WebPushEncryption.ExportPoint(browser.ExportParameters(false));
        var auth = RandomNumberGenerator.GetBytes(16);

        Assert.Equal(4096, WebPushEncryption.Encrypt(new byte[WebPushEncryption.MaxPlaintextBytes], browserPublic, auth).Length);
        Assert.Throws<ArgumentException>(() =>
            WebPushEncryption.Encrypt(new byte[WebPushEncryption.MaxPlaintextBytes + 1], browserPublic, auth));
    }

    [Fact]
    public async Task Push_is_posted_to_the_push_service_encrypted_and_signed_with_the_vapid_key()
    {
        using var server = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var browser = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var browserPublic = WebPushEncryption.ExportPoint(browser.ExportParameters(false));
        var auth = RandomNumberGenerator.GetBytes(16);
        var handler = new RecordingHandler(HttpStatusCode.Created);
        var clock = new FixedClock(new DateTimeOffset(2026, 10, 9, 8, 0, 0, TimeSpan.Zero));
        var sender = Sender(server, handler, clock);
        const string payload = """{"title":"Certificate issued","body":"You are now certified."}""";

        var result = await sender.SendAsync(new PushTarget("https://push.example.test/send/abc?x=1",
            Base64Url.EncodeToString(browserPublic), Base64Url.EncodeToString(auth)), payload, CancellationToken.None);

        Assert.Equal(PushResult.Delivered, result);
        var request = Assert.Single(handler.Requests);
        Assert.Equal((HttpMethod.Post, "https://push.example.test/send/abc?x=1"), (request.Method, request.Uri));
        Assert.Equal(("aes128gcm", "86400"), (request.ContentEncoding, request.Headers["TTL"]));
        Assert.Equal(payload, Encoding.UTF8.GetString(Decrypt(request.Body, browser, browserPublic, auth)));

        // RFC 8292: "vapid t=<JWT>, k=<public key>", the JWT signed by that key for the origin of the push service.
        var publicKey = Base64Url.EncodeToString(WebPushEncryption.ExportPoint(server.ExportParameters(false)));
        Assert.Equal(publicKey, sender.PublicKey);
        var authorization = request.Headers["Authorization"];
        Assert.StartsWith("vapid t=", authorization);
        Assert.EndsWith($", k={publicKey}", authorization);
        var token = authorization["vapid t=".Length..authorization.IndexOf(',')].Split('.');
        Assert.Equal(3, token.Length);
        Assert.True(server.VerifyData(Encoding.ASCII.GetBytes($"{token[0]}.{token[1]}"), Base64Url.DecodeFromChars(token[2]),
            HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
        var header = JsonDocument.Parse(Base64Url.DecodeFromChars(token[0])).RootElement;
        var claims = JsonDocument.Parse(Base64Url.DecodeFromChars(token[1])).RootElement;
        Assert.Equal(("JWT", "ES256"), (header.GetProperty("typ").GetString(), header.GetProperty("alg").GetString()));
        Assert.Equal(("https://push.example.test", "mailto:ops@brewforge.test", clock.GetUtcNow().AddHours(12).ToUnixTimeSeconds()),
            (claims.GetProperty("aud").GetString(), claims.GetProperty("sub").GetString(), claims.GetProperty("exp").GetInt64()));
    }

    [Theory]
    [InlineData(HttpStatusCode.Created, PushResult.Delivered)]
    [InlineData(HttpStatusCode.OK, PushResult.Delivered)]
    [InlineData(HttpStatusCode.Gone, PushResult.Gone)]
    [InlineData(HttpStatusCode.NotFound, PushResult.Gone)]
    [InlineData(HttpStatusCode.TooManyRequests, PushResult.Failed)]
    [InlineData(HttpStatusCode.InternalServerError, PushResult.Failed)]
    public async Task Answer_of_the_push_service_says_whether_the_browser_is_still_there(HttpStatusCode answer, PushResult expected)
    {
        using var server = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var browser = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var sender = Sender(server, new RecordingHandler(answer), TimeProvider.System);

        var result = await sender.SendAsync(new PushTarget("https://push.example.test/send/abc",
            Base64Url.EncodeToString(WebPushEncryption.ExportPoint(browser.ExportParameters(false))),
            Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(16))), "{}", CancellationToken.None);

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("", "", "")]
    [InlineData("mailto:ops@brewforge.test", "not-a-key", "not-a-key")]
    [InlineData("", RfcSenderPublic, RfcSenderPrivate)]
    public async Task Push_without_a_subject_and_a_key_pair_is_not_configured(string subject, string publicKey, string privateKey)
    {
        using var http = new HttpClient(new RecordingHandler(HttpStatusCode.Created));
        var sender = new WebPushSender(http, Options.Create(new WebPushOptions
        {
            Subject = subject, PublicKey = publicKey, PrivateKey = privateKey,
        }), TimeProvider.System);

        Assert.False(sender.IsConfigured);
        Assert.Null(sender.PublicKey);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sender.SendAsync(new PushTarget("https://push.example.test/x", RfcReceiverPublic, RfcAuthSecret), "{}", CancellationToken.None));
    }

    [Fact]
    public async Task Email_is_written_to_the_pickup_folder_when_one_is_configured()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"brewforge-test-mail-{Guid.NewGuid():N}");
        try
        {
            var sender = new SmtpEmailSender(Options.Create(new EmailOptions { From = "noreply@brewforge.test", PickupDirectory = folder }));
            Assert.True(sender.IsConfigured);

            await sender.SendAsync("trainee@brewforge.test", "Tran Thi B", "Course assigned", "You have been enrolled.", CancellationToken.None);

            var message = await File.ReadAllTextAsync(Assert.Single(Directory.GetFiles(folder, "*.eml")));
            Assert.Contains("trainee@brewforge.test", message);
            Assert.Contains("noreply@brewforge.test", message);
            Assert.Contains("Subject: Course assigned", message);
            Assert.Contains("You have been enrolled.", message);
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }

    [Theory]
    [InlineData("", "smtp.example.test", "")]
    [InlineData("noreply@brewforge.test", "", "")]
    public void Email_without_a_sender_address_or_a_server_is_not_configured(string from, string host, string pickup) =>
        Assert.False(new SmtpEmailSender(Options.Create(new EmailOptions { From = from, Host = host, PickupDirectory = pickup })).IsConfigured);

    // ---------------------------------------------------------------- helpers

    private static WebPushSender Sender(ECDsa server, HttpMessageHandler handler, TimeProvider clock)
    {
        var keys = server.ExportParameters(includePrivateParameters: true);
        return new WebPushSender(new HttpClient(handler), Options.Create(new WebPushOptions
        {
            Subject = "mailto:ops@brewforge.test",
            PublicKey = Base64Url.EncodeToString(WebPushEncryption.ExportPoint(keys)),
            PrivateKey = Base64Url.EncodeToString(keys.D),
        }), clock);
    }

    /// <summary>
    /// What a browser does with a push message (RFC 8291 section 3.4, RFC
    /// 8188): written here from the receiving side, independently of the
    /// code that encrypts.
    /// </summary>
    private static byte[] Decrypt(byte[] message, ECDiffieHellman receiver, byte[] receiverPublic, byte[] authSecret)
    {
        var salt = message.AsSpan(0, 16).ToArray();
        Assert.Equal(4096u, BinaryPrimitives.ReadUInt32BigEndian(message.AsSpan(16)));
        var keyIdLength = message[20];
        var senderPublic = message.AsSpan(21, keyIdLength).ToArray();
        var encrypted = message.AsSpan(21 + keyIdLength);

        using var sender = ECDiffieHellman.Create(WebPushEncryption.ImportPoint(senderPublic));
        var shared = receiver.DeriveRawSecretAgreement(sender.PublicKey);
        var info = Encoding.ASCII.GetBytes("WebPush: info\0").Concat(receiverPublic).Concat(senderPublic).ToArray();
        var ikm = HKDF.DeriveKey(HashAlgorithmName.SHA256, shared, 32, authSecret, info);
        var key = HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 16, salt, Encoding.ASCII.GetBytes("Content-Encoding: aes128gcm\0"));
        var nonce = HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 12, salt, Encoding.ASCII.GetBytes("Content-Encoding: nonce\0"));

        var record = new byte[encrypted.Length - 16];
        using var aes = new AesGcm(key, tagSizeInBytes: 16);
        aes.Decrypt(nonce, encrypted[..^16], encrypted[^16..], record);
        Assert.Equal(0x02, record[^1]);
        return record[..^1];
    }

    private sealed record Recorded(HttpMethod Method, string Uri, Dictionary<string, string> Headers, string? ContentEncoding, byte[] Body);

    private sealed class RecordingHandler(HttpStatusCode answer) : HttpMessageHandler
    {
        public List<Recorded> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new Recorded(request.Method, request.RequestUri!.OriginalString,
                request.Headers.ToDictionary(h => h.Key, h => string.Join(", ", h.Value)),
                request.Content!.Headers.ContentEncoding.SingleOrDefault(),
                await request.Content.ReadAsByteArrayAsync(cancellationToken)));
            return new HttpResponseMessage(answer);
        }
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
