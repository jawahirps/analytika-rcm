using Analytika.Models;
using Hangfire.Common;
using Hangfire.States;
using Hangfire.Storage;
using Microsoft.AspNetCore.Identity;

namespace Analytika.Services;

/// <summary>
/// Emails admins when a Hangfire job reaches the Failed state (i.e. after all
/// automatic retries are exhausted). Recipients come from Alerting:AdminEmails
/// (comma-separated), falling back to the email addresses of Admin-role users.
/// </summary>
public class JobFailureNotificationFilter : JobFilterAttribute, IApplyStateFilter
{
    private readonly IServiceProvider _services;

    public JobFailureNotificationFilter(IServiceProvider services)
    {
        _services = services;
    }

    public void OnStateApplied(ApplyStateContext context, IWriteOnlyTransaction transaction)
    {
        if (context.NewState is not FailedState failed) return;

        // A state filter must never throw — alerting failure shouldn't mask the job failure.
        try
        {
            using var scope = _services.CreateScope();
            var config = scope.ServiceProvider.GetRequiredService<IConfiguration>();
            var logger = scope.ServiceProvider.GetRequiredService<ILogger<JobFailureNotificationFilter>>();

            var jobName = $"{context.BackgroundJob.Job.Type.Name}.{context.BackgroundJob.Job.Method.Name}";
            logger.LogError(failed.Exception, "[JobAlert] Background job {JobName} (id {JobId}) failed permanently", jobName, context.BackgroundJob.Id);

            var recipients = config["Alerting:AdminEmails"];
            if (string.IsNullOrWhiteSpace(recipients))
            {
                var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
                var admins = userManager.GetUsersInRoleAsync("Admin").GetAwaiter().GetResult();
                recipients = string.Join(",", admins.Select(a => a.Email).Where(e => !string.IsNullOrWhiteSpace(e)));
            }
            if (string.IsNullOrWhiteSpace(recipients)) return;

            var email = scope.ServiceProvider.GetRequiredService<IEmailService>();
            var body = $"Background job failed permanently.\n\n" +
                       $"Job: {jobName}\n" +
                       $"Job ID: {context.BackgroundJob.Id}\n" +
                       $"Server: {Environment.MachineName}\n" +
                       $"Time (UTC): {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}\n\n" +
                       $"Error:\n{failed.Exception?.Message}";
            email.SendEmailAsync(recipients, $"[Analytika] Job failed: {jobName}", body).GetAwaiter().GetResult();
        }
        catch
        {
            // swallow — never let alerting break Hangfire state transitions
        }
    }

    public void OnStateUnapplied(ApplyStateContext context, IWriteOnlyTransaction transaction)
    {
    }
}
