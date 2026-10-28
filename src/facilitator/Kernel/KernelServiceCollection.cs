using System.Threading.RateLimiting;

using Facilitator.Kernel.Link;
using Facilitator.Kernel.Shared.Auth;
using Facilitator.Kernel.Shared.Dispatch;
using Facilitator.Kernel.Shared.Evidence;
using Facilitator.Kernel.Shared.Identity;
using Facilitator.Kernel.Shared.Memory;
using Facilitator.Kernel.Shared.Persistence;
using Facilitator.Kernel.Shared.Storage;
using Facilitator.Kernel.Work;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.DependencyInjection;

using Ofac.Protocol.Validation;

using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Facilitator.Kernel;

/// <summary>
/// Kernel composition root: one feature-extension method per folder under
/// <c>Kernel/</c> — a new feature is one line in <see cref="AddKernel"/> plus
/// its own extension. Also the OTel wiring (component packages — the
/// OpenTelemetry.AspNetCore meta-package is not used; OTLP exporter only when
/// <c>Otel:Endpoint</c> is configured, spec §22.2).
/// </summary>
public static class KernelServiceCollection
{
    /// <summary>Register every kernel feature on the builder.</summary>
    public static WebApplicationBuilder AddKernel(this WebApplicationBuilder builder)
    {
        builder.AddMountedConfigFile();
        builder.AddSharedKernel();
        builder.AddIdentityFeature();
        builder.AddWorkFeature();
        builder.AddStorageFeature();
        builder.AddLinkFeature();
        builder.AddMemoryFeature();
        return builder;
    }

    /// <summary>
    /// Mounted production config (<c>config/facilitator.json</c> under the content
    /// root; path overridable via <c>Config:File</c>, i.e. env <c>Config__File</c>).
    /// Appended, then the environment-variable provider is re-appended after it
    /// so env vars keep overriding the file (a <see cref="Microsoft.Extensions.Configuration.ConfigurationManager"/>
    /// ignores <c>Sources</c> surgery — precedence follows append order).
    /// Optional by design: dev/source runs without the file are unaffected. No
    /// YAML provider ships with net10.0 (the Microsoft <c>…Configuration.Yaml*</c>
    /// packages never left preview), hence JSON.
    /// </summary>
    private static WebApplicationBuilder AddMountedConfigFile(this WebApplicationBuilder builder)
    {
        var path = builder.Configuration["Config:File"]
            ?? Path.Combine(builder.Environment.ContentRootPath, "config", "facilitator.json");
        if (!File.Exists(path))
        {
            return builder;
        }

        builder.Configuration
            .AddJsonFile(path, optional: true, reloadOnChange: false)
            .AddEnvironmentVariables();
        return builder;
    }

    /// <summary>Persistence, dispatcher + sinks, auth seam, validator, OTel: everything not a feature.</summary>
    private static WebApplicationBuilder AddSharedKernel(this WebApplicationBuilder builder)
    {
        builder.Services.AddDbContextFactory<FacilitatorDbContext>(options =>
            options.UseNpgsql(builder.Configuration.GetConnectionString("Facilitator")));

        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton(EnvelopeValidator.CreateDefault());
        builder.Services.AddSingleton(ControlFrameValidator.CreateDefault());
        builder.Services.AddSingleton<KernelMetrics>();
        builder.Services.AddSingleton<DeliveryLedger>();
        builder.Services.AddSingleton<IDatabaseInitializer, DatabaseInitializer>();
        builder.Services.AddSingleton<PgWakeListener>();
        builder.Services.AddSingleton<OutboxRepository>();
        builder.Services.AddSingleton<OutboxAdminStore>();
        builder.Services.AddSingleton<IngressStore>();
        builder.Services.AddSingleton<ISink, KernelLogSink>();
        builder.Services.AddSingleton<IEventDispatcher, DbEventDispatcher>();
        builder.Services.AddHostedService<OutboxDispatcher>();

        builder.Services.AddProblemDetails();
        builder.AddOpenTelemetry();
        return builder;
    }

    /// <summary>Human-plane identity: JwtBearer (OIDC), role store, audit spine, startup gate.</summary>
    private static WebApplicationBuilder AddIdentityFeature(this WebApplicationBuilder builder)
    {
        var identity = builder.Configuration.GetSection(IdentityOptions.SectionName).Get<IdentityOptions>() ?? new IdentityOptions();
        builder.Services.Configure<IdentityOptions>(
            builder.Configuration.GetSection(IdentityOptions.SectionName));
        builder.Services.AddSingleton<AuditWriter>();
        builder.Services.AddSingleton<OidcJwks>();
        builder.Services.AddSingleton<RoleStore>();
        builder.Services.AddSingleton<IdentityStartupCheck>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<IdentityStartupCheck>());
        builder.Services.AddAuthentication(IdentityAuthentication.Scheme)
            .AddJwtBearer(IdentityAuthentication.Scheme, options => IdentityJwt.Configure(options, identity));
        return builder;
    }

    /// <summary>Work lifecycle: coordination store + ingress use-cases.</summary>
    private static WebApplicationBuilder AddWorkFeature(this WebApplicationBuilder builder)
    {
        builder.Services.AddScoped<ICoordinationStore, EfCoordinationStore>();
        builder.Services.AddSingleton<IngressPipeline>();
        builder.Services.AddScoped<IngressProcessor>();
        builder.Services.AddScoped<CloudEventIngress>();
        builder.Services.AddScoped<ArtifactIngress>();
        builder.Services.AddScoped<ResultIngress>();
        builder.Services.AddScoped<IResultManifestLedger, ResultManifestLedger>();
        builder.Services.AddScoped<ApprovalIngress>();
        builder.Services.AddScoped<MemoryIngress>();
        return builder;
    }

    /// <summary>Workspace Link server: enrollment, sessions, ledger, pump, sink, ingress.</summary>
    private static WebApplicationBuilder AddLinkFeature(this WebApplicationBuilder builder)
    {
        var linkOptions = Facilitator.Kernel.Link.LinkOptions.FromConfiguration(builder.Configuration);
        builder.Services.AddSingleton(linkOptions);
        builder.Services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy<string>("enroll", context =>
                RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "anonymous",
                    factory: _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 30,
                        Window = TimeSpan.FromMinutes(1),
                        AutoReplenishment = true,
                    }));
        });
        builder.Services.AddSingleton<LinkEnrollmentSecret>();
        builder.Services.AddSingleton<LinkEnrollmentStartupCheck>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<LinkEnrollmentStartupCheck>());
        builder.Services.AddSingleton<LinkEnrollmentStore>();
        builder.Services.AddSingleton<IEvidenceStore, EvidenceStore>();
        builder.Services.AddScoped<IApprovalLedger, ApprovalLedger>();
        builder.Services.AddHostedService<TeamRosterSeeder>();
        builder.Services.AddSingleton<ILinkTokenValidator, StoreLinkTokenValidator>();
        builder.Services.AddSingleton<LinkService>();
        builder.Services.AddSingleton<LinkConnectionRegistry>();
        builder.Services.AddSingleton<LinkLedger>();
        builder.Services.AddSingleton<LinkDeliveryPump>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<LinkDeliveryPump>());
        builder.Services.AddSingleton<EvidenceValidator>();
        builder.Services.AddSingleton<AdmissionGate>();
        builder.Services.AddSingleton<LinkReceipts>();
        builder.Services.AddScoped<LinkIngress>();
        builder.Services.AddSingleton<ISink, LinkSink>();

        var heartbeat = TimeSpan.FromSeconds(linkOptions.HeartbeatSeconds);
        builder.Services.AddSignalR(options =>
        {
            options.KeepAliveInterval = heartbeat;
            options.ClientTimeoutInterval = heartbeat + heartbeat;
            options.HandshakeTimeout = TimeSpan.FromSeconds(15);
        });
        builder.Services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, LinkBearerHandler>(LinkBearerHandler.SchemeName, null);
        builder.Services.AddAuthorization();
        return builder;
    }

    /// <summary>Artifact storage: options (fail-closed provider check), the byte-store port, the ledger.</summary>
    private static WebApplicationBuilder AddStorageFeature(this WebApplicationBuilder builder)
    {
        var storage = builder.Configuration.GetSection(ArtifactOptions.SectionName).Get<ArtifactOptions>() ?? new ArtifactOptions();
        builder.Services.AddSingleton(storage);
        builder.Services.AddSingleton<IArtifactStore>(sp => storage.Provider switch
        {
            // Read once at composition: no hot-swap.
            "filesystem" => new FileSystemArtifactStore(sp.GetRequiredService<ArtifactOptions>()),
            "s3" => new S3ArtifactStore(sp.GetRequiredService<ArtifactOptions>()),
            _ => throw new InvalidOperationException(
                $"Storage:Provider '{storage.Provider}' is unknown (only 'filesystem' and 's3' ship) — fail-closed."),
        });
        builder.Services.AddSingleton<StorageStartupCheck>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<StorageStartupCheck>());
        builder.Services.AddScoped<IArtifactLedger, ArtifactLedger>();
        builder.Services.AddSingleton<RetentionGcService>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<RetentionGcService>());
        return builder;
    }

    /// <summary>Memory broker (Plan 16): options (fail-closed provider check), the provider port, the broker.</summary>
    private static WebApplicationBuilder AddMemoryFeature(this WebApplicationBuilder builder)
    {
        var memory = builder.Configuration.GetSection(MemoryOptions.SectionName).Get<MemoryOptions>() ?? new MemoryOptions();
        builder.Services.AddSingleton(memory);
        builder.Services.AddSingleton<IMemoryProvider>(sp => memory.Provider switch
        {
            // Read once at composition: no hot-swap. postgres-fts is the only
            // shipped profile (spec §14.7); hindsight/custom are documented seams.
            "postgres-fts" => new PostgresFtsMemoryProvider(),
            _ => throw new InvalidOperationException(
                $"Memory:Provider '{memory.Provider}' is unknown (only 'postgres-fts' ships) — fail-closed."),
        });
        builder.Services.AddScoped<MemoryBroker>();
        builder.Services.AddSingleton<MemoryRetentionService>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<MemoryRetentionService>());
        return builder;
    }

    private static WebApplicationBuilder AddOpenTelemetry(this WebApplicationBuilder builder)
    {
        var otel = builder.Services.AddOpenTelemetry();
        otel.WithTracing(t =>
        {
            t.AddSource(KernelActivity.SourceName);
            t.AddAspNetCoreInstrumentation();
        });
        otel.WithMetrics(m => m.AddMeter(KernelMetrics.MeterName));

        if (!string.IsNullOrEmpty(builder.Configuration["Otel:Endpoint"]))
        {
            otel.WithMetrics(m => m.AddOtlpExporter());
            otel.WithTracing(t => t.AddOtlpExporter());
        }

        return builder;
    }
}
