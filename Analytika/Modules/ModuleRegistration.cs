using Analytika.Models;
using Analytika.Services;
using Hangfire;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Analytika.Modules;

public static class ModuleRegistration
{
    public static IServiceCollection AddAnalytikaModules(
        this IServiceCollection services,
        string dbPath,
        bool hangfireServerEnabled,
        bool recurringJobsEnabled,
        bool pendingDownloadHostedServiceEnabled)
    {
        services.AddCoreModule(dbPath);
        services.AddDashboardModule();
        services.AddPortalModule();
        services.AddReportingModule();
        services.AddJobsModule(hangfireServerEnabled, recurringJobsEnabled, pendingDownloadHostedServiceEnabled);
        return services;
    }

    private static IServiceCollection AddCoreModule(this IServiceCollection services, string dbPath)
    {
        // Pooled context + tuned SQLite connection (WAL/pragmas via interceptor).
        // Pooling removes per-request DbContext allocation under load; the context
        // is options-only so pooling is safe here.
        services.AddDbContextPool<AppDbContext>(options =>
            options
                .UseSqlite($"Data Source={dbPath};Pooling=True;Foreign Keys=True")
                .AddInterceptors(new SqlitePragmaInterceptor()));

        services.AddIdentity<ApplicationUser, IdentityRole>(options =>
        {
            options.SignIn.RequireConfirmedAccount = false;
            options.Password.RequireDigit = true;
            options.Password.RequiredLength = 6;
            options.Password.RequireNonAlphanumeric = false;
        })
        .AddEntityFrameworkStores<AppDbContext>()
        .AddDefaultTokenProviders();

        services.ConfigureApplicationCookie(options =>
        {
            options.LoginPath = "/Home/Index";
            options.LogoutPath = "/Home/LogOut";
            options.AccessDeniedPath = "/Home/Index";
            options.ExpireTimeSpan = TimeSpan.FromDays(30);
            options.SlidingExpiration = true;
            options.Cookie.MaxAge = TimeSpan.FromDays(30);
        });

        services.AddMemoryCache();

        // Response compression (Brotli + Gzip) — large HTML/JSON/CSS payloads
        // shrink ~70-80% on the wire, cutting render-start time over slow links.
        services.AddResponseCompression(o =>
        {
            o.EnableForHttps = true;
            o.Providers.Add<Microsoft.AspNetCore.ResponseCompression.BrotliCompressionProvider>();
            o.Providers.Add<Microsoft.AspNetCore.ResponseCompression.GzipCompressionProvider>();
            o.MimeTypes = Microsoft.AspNetCore.ResponseCompression.ResponseCompressionDefaults.MimeTypes
                .Concat(new[] { "application/json", "image/svg+xml" });
        });
        services.Configure<Microsoft.AspNetCore.ResponseCompression.BrotliCompressionProviderOptions>(
            o => o.Level = System.IO.Compression.CompressionLevel.Fastest);
        services.Configure<Microsoft.AspNetCore.ResponseCompression.GzipCompressionProviderOptions>(
            o => o.Level = System.IO.Compression.CompressionLevel.Fastest);

        services.AddControllersWithViews();
        services.AddSession(options =>
        {
            options.IdleTimeout = TimeSpan.FromHours(8);
            options.Cookie.HttpOnly = true;
            options.Cookie.IsEssential = true;
        });

        services.AddHttpClient("DHA").ConfigurePrimaryHttpMessageHandler(() =>
            new HttpClientHandler { ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator });
        services.AddHttpClient("RHA").ConfigurePrimaryHttpMessageHandler(() =>
            new HttpClientHandler { ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator });

        return services;
    }

    private static IServiceCollection AddDashboardModule(this IServiceCollection services)
    {
        services.AddScoped<IDashboardService, DashboardService>();
        return services;
    }

    private static IServiceCollection AddPortalModule(this IServiceCollection services)
    {
        services.AddScoped<IEmailService, EmailService>();
        services.AddScoped<IReportService, ReportService>();
        services.AddScoped<IDhaPortalService, DhaPortalService>();
        services.AddScoped<IRhaPortalService, RhaPortalService>();
        services.AddScoped<PortalSyncService>();
        services.AddScoped<ReconciliationService>();
        services.AddScoped<RemittanceParserService>();
        services.AddScoped<XmlParsingService>();
        return services;
    }

    private static IServiceCollection AddReportingModule(this IServiceCollection services)
    {
        // Reporting logic is already represented by the existing service graph.
        return services;
    }

    private static IServiceCollection AddJobsModule(
        this IServiceCollection services,
        bool hangfireServerEnabled,
        bool recurringJobsEnabled,
        bool pendingDownloadHostedServiceEnabled)
    {
        if (hangfireServerEnabled || recurringJobsEnabled)
        {
            services.AddHangfire(config => config
                .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
                .UseSimpleAssemblyNameTypeSerializer()
                .UseRecommendedSerializerSettings()
                .UseInMemoryStorage());
        }

        if (hangfireServerEnabled)
            services.AddHangfireServer();

        if (pendingDownloadHostedServiceEnabled)
            services.AddHostedService<PendingDownloadService>();

        return services;
    }
}
