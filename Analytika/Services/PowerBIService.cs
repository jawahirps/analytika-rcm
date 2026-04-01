using Analytika.Models;
using Microsoft.EntityFrameworkCore;

namespace Analytika.Services;

public class PowerBIService : IPowerBIService
{
    private readonly AppDbContext _context;
    private readonly ILogger<PowerBIService> _logger;

    public PowerBIService(AppDbContext context, ILogger<PowerBIService> logger)
    {
        _context = context;
        _logger = logger;
    }

    public async Task<EmbedConfig?> GetEmbedConfigAsync(string tabName)
    {
        var embed = await _context.DashboardEmbeds
            .FirstOrDefaultAsync(e => e.TabName == tabName && e.IsActive);

        if (embed == null) return null;

        return new EmbedConfig
        {
            AccessToken = embed.EmbedToken,
            EmbedUrl = embed.EmbedUrl,
            ReportId = embed.ReportId,
            TabName = embed.TabName
        };
    }

    public async Task RefreshTokensAsync()
    {
        var expiredEmbeds = await _context.DashboardEmbeds
            .Where(e => e.IsActive && e.TokenExpiry <= DateTime.UtcNow.AddMinutes(10))
            .ToListAsync();

        foreach (var embed in expiredEmbeds)
        {
            // In production, call Power BI API to get new token
            embed.EmbedToken = "REFRESHED_TOKEN_" + embed.TabName.ToUpper() + "_" + DateTime.UtcNow.Ticks;
            embed.TokenExpiry = DateTime.UtcNow.AddHours(1);
            _logger.LogInformation("Refreshed token for tab: {TabName}", embed.TabName);
        }

        await _context.SaveChangesAsync();
    }
}
