namespace Analytika.Services;

public interface IEmailService
{
    /// <summary>
    /// Send a report file as an email attachment.
    /// </summary>
    /// <param name="to">Comma-separated list of recipient email addresses.</param>
    /// <param name="reportId">Report identifier shown in the subject line.</param>
    /// <param name="reportType">Human-readable report type name.</param>
    /// <param name="filePath">Absolute path to the generated file on disk.</param>
    Task SendReportAsync(string to, string reportId, string reportType, string filePath);

    /// <summary>
    /// Send a plain-text email without attachments (used for alerting).
    /// </summary>
    /// <param name="to">Comma-separated list of recipient email addresses.</param>
    Task SendEmailAsync(string to, string subject, string body);
    Task<SmtpSettings> GetSmtpSettingsAsync();
}
