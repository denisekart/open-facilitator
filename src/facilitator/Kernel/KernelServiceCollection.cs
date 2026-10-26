using System.Threading.RateLimiting;

using Facilitator.Kernel.Link;
using Facilitator.Kernel.Shared.Auth;
using Facilitator.Kernel.Shared.Dispatch;
using Facilitator.Kernel.Shared.Identity;
using Facilitator.Kernel.Shared.Persistence;
using Facilitator.Kernel.Work;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
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
        builder.AddSharedKernel();
        builder.AddIdentityFeature();
        builder.AddWorkFeature();
        builder.AddLinkFeature();
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
        builder.Services.AddHostedService<TeamRosterSeeder>();
        builder.Services.AddSingleton<ILinkTokenValidator, StoreLinkTokenValidator>();
        builder.Services.AddSingleton<LinkService>();
        builder.Services.AddSingleton<LinkConnectionRegistry>();
        builder.Services.AddSingleton<LinkLedger>();
        builder.Services.AddSingleton<LinkDeliveryPump>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<LinkDeliveryPump>());
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
