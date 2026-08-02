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
        services.AddAiModule();
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
                    // Default Timeout=120: SQLite busy/command timeout. The 30s default made
                    // every app write die with an unhandled exception whenever a long-running
                    // job (parse backfill, match pass) held the write lock — dead credential
                    // saves, killed download streams, Cloudflare 524s. 120s rides out normal
                    // lock windows; truly long jobs still fail rather than hang forever.
                    .UseSqlite($"Data Source={dbPath};Pooling=True;Foreign Keys=True;Default Timeout=120")
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
            // Raised from 8 per the onboarding plan. Applies to new passwords and changes
            // only — Identity does not re-validate at sign-in, so existing accounts keep
            // working. Seeded accounts read their password from configuration for the same
            // reason (see SeedData.SeedPassword), otherwise a fresh install could no longer
            // create its own administrator.
            options.Password.RequiredLength = 12;
            options.Password.RequireNonAlphanumeric = true;
            options.Password.RequireUppercase = true;
            options.Password.RequireLowercase = true;
            options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
            options.Lockout.MaxFailedAccessAttempts = 5;
            options.Lockout.AllowedForNewUsers = true;
        })
        .AddEntityFrameworkStores<AppDbContext>()
        .AddDefaultTokenProviders();

        services.ConfigureApplicationCookie(options =>
        {
            options.LoginPath = "/Home/Index";
            options.LogoutPath = "/Home/LogOut";
            options.AccessDeniedPath = "/Home/Index";
            options.ExpireTimeSpan = TimeSpan.FromHours(8);
            options.SlidingExpiration = true;
            options.Cookie.MaxAge = TimeSpan.FromHours(8);
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
        services.AddScoped<DenialAnalystService>();
        services.AddScoped<PortalSyncService>();
        services.AddScoped<ReconciliationService>();
        services.AddScoped<RemittanceParserService>();
        services.AddScoped<XmlParsingService>();
        services.AddScoped<Analytika.Security.FacilityScopeService>();
        // Dev console command engine. Registration is unconditional (it has no side
        // effects); reachability is decided by DevCliController, which 404s outside
        // the Development environment.
        services.AddScoped<IDevCliService, DevCliService>();
        // Claude-backed assistant on the same screen. Generous timeout: a turn can run
        // several tool rounds against the database before it answers.
        services.AddHttpClient("anthropic", c => c.Timeout = TimeSpan.FromSeconds(180));
        services.AddScoped<IDevChatService, DevChatService>();
        services.AddHttpContextAccessor();
        services.AddScoped<Analytika.Security.ITenantContext, Analytika.Security.TenantContext>();
        return services;
    }

    private static IServiceCollection AddReportingModule(this IServiceCollection services)
    {
        // Reporting logic is already represented by the existing service graph.
        return services;
    }

    private static IServiceCollection AddAiModule(this IServiceCollection services)
    {
        // NVIDIA (OpenAI-compatible) analyst agent. Timeout is governed per-request
        // by the service (settings.TimeoutSeconds); keep the client timeout generous.
        services.AddHttpClient("nvidia", c => c.Timeout = TimeSpan.FromSeconds(300));
        services.AddScoped<IAiSettingsService, AiSettingsService>();
        services.AddScoped<INvidiaAnalystService, NvidiaAnalystService>();

        // Analytics assistant. Keeps its two-stage intent-catalogue architecture — the
        // model picks an approved intent and never writes SQL — which is what lets
        // facility-scoped users use it. Only the transport is the cloud provider now.
        // Scoped, not singleton: it reads settings through the scoped IAiSettingsService,
        // and a singleton holding a scoped dependency is a captive that fails scope
        // validation at startup. The old Ollama client only took IHttpClientFactory, so
        // singleton was safe there and is not here.
        services.AddScoped<ILlmChatClient, LlmChatClient>();
        services.AddScoped<IBixAssistantService, BixAssistantService>();
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
