using Analytika.Models;
using Analytika.Security;
using Analytika.Services;
using Hangfire;
using Hangfire.PostgreSql;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Polly;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Analytika.Modules;

public static class ModuleRegistration
{
    public static IServiceCollection AddAnalytikaModules(
        this IServiceCollection services,
        IConfiguration configuration,
        string dbPath,
        bool hangfireServerEnabled,
        bool recurringJobsEnabled,
        bool pendingDownloadHostedServiceEnabled)
    {
        services.AddCoreModule(configuration, dbPath);
        services.AddDashboardModule();
        services.AddPortalModule();
        services.AddReportingModule();
        services.AddJobsModule(configuration, hangfireServerEnabled, recurringJobsEnabled, pendingDownloadHostedServiceEnabled);
        return services;
    }

    private static IServiceCollection AddCoreModule(this IServiceCollection services, IConfiguration configuration, string dbPath)
    {
        if (DatabaseConfig.GetProvider(configuration) == DatabaseConfig.Postgres)
        {
            var conn = DatabaseConfig.GetPostgresConnectionString(configuration)
                ?? throw new InvalidOperationException(
                    "Database provider is 'postgres' but no connection string was found. " +
                    "Set ConnectionStrings:Postgres or DATABASE_URL.");
            services.AddDbContext<AppDbContext>(options => options.UseNpgsql(conn));
        }
        else
        {
            // Pooled context + WAL/pragmas via interceptor for SQLite installs.
            services.AddDbContextPool<AppDbContext>(options =>
                options
                    .UseSqlite($"Data Source={dbPath};Pooling=True;Foreign Keys=True")
                    .AddInterceptors(new SqlitePragmaInterceptor()));
        }

        // Data Protection keys persisted beside the data so credential decryption
        // and auth cookies survive restarts/redeploys (mounted volume in Docker)
        var dataDir = Path.GetDirectoryName(Path.GetFullPath(dbPath)) ?? ".";
        var keysDir = Directory.CreateDirectory(Path.Combine(dataDir, "dp-keys"));
        services.AddDataProtection()
            .PersistKeysToFileSystem(keysDir)
            .SetApplicationName("Analytika");
        services.AddSingleton<ICredentialProtector, CredentialProtector>();

        services.AddHealthChecks()
            .AddDbContextCheck<AppDbContext>()
            .AddCheck<SyncHealthCheck>("portal-sync");

        // Telemetry export is opt-in: set OTEL_EXPORTER_OTLP_ENDPOINT
        // (Grafana Cloud / Better Stack / any OTLP collector) to enable.
        if (!string.IsNullOrWhiteSpace(configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
        {
            services.AddOpenTelemetry()
                .ConfigureResource(r => r.AddService(
                    serviceName: configuration["OTEL_SERVICE_NAME"] ?? "ghaf-bix",
                    serviceInstanceId: Environment.MachineName))
                .WithTracing(t => t
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddOtlpExporter())
                .WithMetrics(m => m
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddRuntimeInstrumentation()
                    .AddOtlpExporter());
        }

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

        // Certificate validation is enforced by default; Portal:AllowInvalidCertificates=true is an
        // explicit operator opt-out for portals with broken certificate chains.
        var allowInvalidCerts = configuration.GetValue("Portal:AllowInvalidCertificates", false);

        HttpClientHandler CreatePortalHandler() => allowInvalidCerts
            ? new HttpClientHandler { ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator }
            : new HttpClientHandler();

        void AddPortalHttpClient(string name) =>
            services.AddHttpClient(name, c => c.Timeout = Timeout.InfiniteTimeSpan) // resilience pipeline governs timeouts
                .ConfigurePrimaryHttpMessageHandler(CreatePortalHandler)
                .AddStandardResilienceHandler(o =>
                {
                    o.AttemptTimeout.Timeout = TimeSpan.FromSeconds(60);
                    o.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(240);
                    o.Retry.MaxRetryAttempts = 3;
                    o.Retry.BackoffType = DelayBackoffType.Exponential;
                    o.Retry.UseJitter = true;
                    o.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(120);
                });

        AddPortalHttpClient("DHA");
        AddPortalHttpClient("RHA");

        services.AddSingleton<ICredentialProtector, CredentialProtector>();
        services.AddHealthChecks().AddDbContextCheck<AppDbContext>("database");

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
        IConfiguration configuration,
        bool hangfireServerEnabled,
        bool recurringJobsEnabled,
        bool pendingDownloadHostedServiceEnabled)
    {
        if (hangfireServerEnabled || recurringJobsEnabled)
        {
            services.AddHangfire(config =>
            {
                config
                    .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
                    .UseSimpleAssemblyNameTypeSerializer()
                    .UseRecommendedSerializerSettings();

                // Durable job storage on Postgres: queued/recurring jobs survive
                // restarts and redeploys. SQLite installs keep in-memory storage.
                var pg = DatabaseConfig.GetPostgresConnectionString(configuration);
                if (DatabaseConfig.GetProvider(configuration) == DatabaseConfig.Postgres && pg != null)
                    config.UsePostgreSqlStorage(o => o.UseNpgsqlConnection(pg));
                else
                    config.UseInMemoryStorage();
            });
        }

        if (hangfireServerEnabled)
            services.AddHangfireServer();

        services.AddScoped<DatabaseMaintenanceService>();

        if (pendingDownloadHostedServiceEnabled)
            services.AddHostedService<PendingDownloadService>();

        return services;
    }
}
