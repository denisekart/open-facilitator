using Facilitator.Kernel.Auth;
using Facilitator.Kernel.Dispatch;
using Facilitator.Kernel.Ingress;
using Facilitator.Kernel.Link;
using Facilitator.Kernel.Persistence;

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
/// Kernel composition root: persistence, coordination store, dispatcher +
/// sinks, auth seam, shared envelope validator, and OTel wiring (component
/// packages — the OpenTelemetry.AspNetCore meta-package is not used; OTLP
/// exporter only when <c>Otel:Endpoint</c> is configured, spec §22.2).
/// </summary>
public static class KernelServiceCollection
{
    /// <summary>Register every kernel service on the builder.</summary>
    public static WebApplicationBuilder AddKernel(this WebApplicationBuilder builder)
    {
        builder.Services.AddDbContextFactory<FacilitatorDbContext>(options =>
            options.UseNpgsql(builder.Configuration.GetConnectionString("Facilitator")));

        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton(EnvelopeValidator.CreateDefault());
        builder.Services.AddSingleton(ControlFrameValidator.CreateDefault());
        builder.Services.AddSingleton<KernelMetrics>();
        builder.Services.AddSingleton<DeliveryLedger>();
        builder.Services.AddSingleton<ISink, KernelLogSink>();
        builder.Services.AddSingleton<IDatabaseInitializer, DatabaseInitializer>();
        builder.Services.AddScoped<ICoordinationStore, EfCoordinationStore>();
        builder.Services.AddSingleton<IngressStore>();
        builder.Services.AddScoped<IngressProcessor>();
        builder.Services.AddSingleton<IEventDispatcher, DbEventDispatcher>();
        builder.Services.AddSingleton<IIngressTokenValidator, BootstrapTokenValidator>();
        builder.Services.AddSingleton<BootstrapAuthStartupCheck>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<BootstrapAuthStartupCheck>());
        builder.Services.AddHostedService<OutboxDispatcher>();

        // Plan 03 link server: enrollment, sessions, ledger, pump, sink, ingress.
        var linkOptions = Link.LinkOptions.FromConfiguration(builder.Configuration);
        builder.Services.AddSingleton(linkOptions);
        builder.Services.AddSingleton<LinkEnrollmentSecret>();
        builder.Services.AddSingleton<LinkEnrollmentStartupCheck>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<LinkEnrollmentStartupCheck>());
        builder.Services.AddSingleton<LinkEnrollmentStore>();
        builder.Services.AddSingleton<ILinkTokenValidator, StoreLinkTokenValidator>();
        builder.Services.AddSingleton<LinkService>();
        builder.Services.AddSingleton<LinkConnectionRegistry>();
        builder.Services.AddSingleton<LinkLedger>();
        builder.Services.AddSingleton<LinkDeliveryPump>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<LinkDeliveryPump>());
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

        builder.Services.AddProblemDetails();
        builder.AddOpenTelemetry();

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
