using System.Net;
using System.Net.Mail;
using BrewForge.Application.Abstractions;
using Microsoft.Extensions.Options;

namespace BrewForge.Infrastructure.Notifications;

public sealed class EmailOptions
{
    public const string Section = "Email";

    /// <summary>The address notifications are sent from. Required for the channel to be configured.</summary>
    public string From { get; set; } = "";
    public string FromName { get; set; } = "BrewForge";

    /// <summary>The SMTP server. Empty means no server.</summary>
    public string Host { get; set; } = "";
    public int Port { get; set; } = 587;
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public bool EnableSsl { get; set; } = true;

    /// <summary>
    /// A folder to write each message to as an .eml file instead of sending
    /// it. For development and demonstrations without a mail server; when it
    /// is set, <see cref="Host"/> is not used.
    /// </summary>
    public string PickupDirectory { get; set; } = "";
}

/// <summary>Plain-text e-mail through an SMTP server, or into a pickup folder.</summary>
public sealed class SmtpEmailSender(IOptions<EmailOptions> options) : IEmailSender
{
    private readonly EmailOptions _options = options.Value;

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_options.From)
        && (!string.IsNullOrWhiteSpace(_options.Host) || !string.IsNullOrWhiteSpace(_options.PickupDirectory));

    public async Task SendAsync(string toAddress, string toName, string subject, string body,
        CancellationToken cancellationToken)
    {
        if (!IsConfigured) throw new InvalidOperationException("E-mail is not configured.");

        using var message = new MailMessage(new MailAddress(_options.From, _options.FromName),
            new MailAddress(toAddress, toName))
        {
            Subject = subject,
            Body = body,
            IsBodyHtml = false,
        };

        using var client = new SmtpClient();
        if (!string.IsNullOrWhiteSpace(_options.PickupDirectory))
        {
            var folder = Path.GetFullPath(_options.PickupDirectory);
            Directory.CreateDirectory(folder);
            client.DeliveryMethod = SmtpDeliveryMethod.SpecifiedPickupDirectory;
            client.PickupDirectoryLocation = folder;
        }
        else
        {
            client.Host = _options.Host;
            client.Port = _options.Port;
            client.EnableSsl = _options.EnableSsl;
            if (!string.IsNullOrWhiteSpace(_options.Username))
            {
                client.Credentials = new NetworkCredential(_options.Username, _options.Password);
            }
        }
        await client.SendMailAsync(message, cancellationToken);
    }
}
