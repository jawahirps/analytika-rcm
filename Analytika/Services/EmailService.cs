using Analytika.Models;
using Microsoft.EntityFrameworkCore;
using System.Net;
using System.Net.Mail;

namespace Analytika.Services;

public class EmailService : IEmailService
{
    private readonly IConfiguration _config;
    private readonly ILogger<EmailService> _logger;
    private readonly IServiceProvider _services;

    public EmailService(IConfiguration config, ILogger<EmailService> logger, IServiceProvider services)
    {
        _config = config;
        _logger = logger;
        _services = services;
    }

    public async Task SendReportAsync(string to, string reportId, string reportType, string filePath)
    {
        var smtp = await GetSmtpSettingsAsync();

        if (string.IsNullOrWhiteSpace(smtp.Host))
        {
            _logger.LogWarning("SMTP host is not configured — skipping email for {ReportId}.", reportId);
            return;
        }

        var recipients = to.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (recipients.Length == 0) return;

        try
        {
            using var message = new MailMessage
            {
                From = new MailAddress(smtp.FromAddress, smtp.FromName),
                Subject = $"[GhafBI] Report {reportId} — {reportType}",
                Body = $"Hello,\n\nYour {reportType} report ({reportId}) has been generated and is attached.\n\nThis is an automated message from GhafBI. Please do not reply.\n\nRegards,\nGhafBI Reports",
                IsBodyHtml = false
            };

            foreach (var recipient in recipients)
                message.To.Add(new MailAddress(recipient));

            if (File.Exists(filePath))
                message.Attachments.Add(new Attachment(filePath));

            using var client = new SmtpClient(smtp.Host, smtp.Port)
            {
                EnableSsl = smtp.EnableSsl,
                DeliveryMethod = SmtpDeliveryMethod.Network,
                UseDefaultCredentials = false,
                Credentials = new NetworkCredential(smtp.UserName, smtp.Password)
            };

            await client.SendMailAsync(message);
            _logger.LogInformation("Report {ReportId} emailed to {Recipients}.", reportId, string.Join(", ", recipients));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send report {ReportId} to {To}.", reportId, to);
        }
    }

    public async Task SendEmailAsync(string to, string subject, string body)
    {
        var smtp = await GetSmtpSettingsAsync();

        if (string.IsNullOrWhiteSpace(smtp.Host))
        {
            _logger.LogWarning("SMTP host is not configured — skipping email '{Subject}'.", subject);
            return;
        }

        var recipients = to.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (recipients.Length == 0) return;

        try
        {
            using var message = new MailMessage
            {
                From = new MailAddress(smtp.FromAddress, smtp.FromName),
                Subject = subject,
                Body = body,
                IsBodyHtml = false
            };

            foreach (var recipient in recipients)
                message.To.Add(new MailAddress(recipient));

            using var client = new SmtpClient(smtp.Host, smtp.Port)
            {
                EnableSsl = smtp.EnableSsl,
                DeliveryMethod = SmtpDeliveryMethod.Network,
                UseDefaultCredentials = false,
                Credentials = new NetworkCredential(smtp.UserName, smtp.Password)
            };

            await client.SendMailAsync(message);
            _logger.LogInformation("Email '{Subject}' sent to {Recipients}.", subject, string.Join(", ", recipients));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send email '{Subject}' to {To}.", subject, to);
        }
    }

    // ── Read SMTP config from DB → fallback to appsettings ────────

    public async Task<SmtpSettings> GetSmtpSettingsAsync()
    {
        Dictionary<string, string?> dbValues = new();
        try
        {
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var rows = await db.SystemSettings
                .AsNoTracking()
                .Where(s => s.Category == "SMTP")
                .ToListAsync();
            dbValues = rows.ToDictionary(r => r.Key, r => r.Value);
        }
        catch { /* DB not ready yet — use appsettings only */ }

        string Cfg(string key, string fallback) =>
            dbValues.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v
            : _config[$"Smtp:{key}"] ?? fallback;

        return new SmtpSettings
        {
            Host = Cfg("Host", string.Empty),
            Port = int.TryParse(Cfg("Port", "587"), out var p) ? p : 587,
            EnableSsl = bool.TryParse(Cfg("EnableSsl", "true"), out var ssl) && ssl,
            UserName = Cfg("UserName", string.Empty),
            Password = Cfg("Password", string.Empty),
            FromAddress = Cfg("FromAddress", string.Empty),
            FromName = Cfg("FromName", "GhafBI Reports")
        };
    }
}

public record SmtpSettings
{
    public string Host { get; init; } = string.Empty;
    public int Port { get; init; } = 587;
    public bool EnableSsl { get; init; } = true;
    public string UserName { get; init; } = string.Empty;
    public string Password { get; init; } = string.Empty;
    public string FromAddress { get; init; } = string.Empty;
    public string FromName { get; init; } = "GhafBI Reports";
}
