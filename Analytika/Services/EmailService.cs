using System.Net;
using System.Net.Mail;

namespace Analytika.Services;

public class EmailService : IEmailService
{
    private readonly IConfiguration _config;
    private readonly ILogger<EmailService> _logger;

    public EmailService(IConfiguration config, ILogger<EmailService> logger)
    {
        _config = config;
        _logger = logger;
    }

    public async Task SendReportAsync(string to, string reportId, string reportType, string filePath)
    {
        var host        = _config["Smtp:Host"]        ?? string.Empty;
        var port        = int.Parse(_config["Smtp:Port"] ?? "587");
        var enableSsl   = bool.Parse(_config["Smtp:EnableSsl"] ?? "true");
        var userName    = _config["Smtp:UserName"]    ?? string.Empty;
        var password    = _config["Smtp:Password"]    ?? string.Empty;
        var fromAddress = _config["Smtp:FromAddress"] ?? userName;
        var fromName    = _config["Smtp:FromName"]    ?? "Analytika Reports";

        if (string.IsNullOrWhiteSpace(host))
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
                From    = new MailAddress(fromAddress, fromName),
                Subject = $"[Analytika] Report {reportId} — {reportType}",
                Body    = $"Hello,\n\nYour {reportType} report ({reportId}) has been generated and is attached to this email.\n\nThis is an automated message from Analytika. Please do not reply.\n\nRegards,\nAnalytika Reports",
                IsBodyHtml = false
            };

            foreach (var recipient in recipients)
                message.To.Add(new MailAddress(recipient));

            if (File.Exists(filePath))
                message.Attachments.Add(new Attachment(filePath));

            using var client = new SmtpClient(host, port)
            {
                EnableSsl            = enableSsl,
                DeliveryMethod       = SmtpDeliveryMethod.Network,
                UseDefaultCredentials = false,
                Credentials          = new NetworkCredential(userName, password)
            };

            await client.SendMailAsync(message);
            _logger.LogInformation("Report {ReportId} emailed to {Recipients}.", reportId, string.Join(", ", recipients));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send report {ReportId} to {To}.", reportId, to);
            // Do not re-throw — email failure should not fail the whole report job
        }
    }
}
