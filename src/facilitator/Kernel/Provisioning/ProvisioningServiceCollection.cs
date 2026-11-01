using Facilitator.Kernel.Provisioning;

using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Plan 35 C composition: the provisioning decision core. Options bind from
/// the <c>Provision</c> section (LauncherEndpoint + the Workloads spec
/// array); the processor is a singleton hosted service next to the
/// NudgeSweeper — same cadence law, tests drive <c>SweepOnceAsync</c>.
/// </summary>
public static class ProvisioningServiceCollection
{
    /// <summary>Register the provisioning feature (call after the shared kernel).</summary>
    public static WebApplicationBuilder AddProvisioningFeature(this WebApplicationBuilder builder)
    {
        // Lazy factory: resolved after the host is built, so test hosts'
        // UseSetting overrides (and late-mounted config) are visible.
        builder.Services.AddSingleton(sp =>
            sp.GetRequiredService<IConfiguration>().GetSection(ProvisioningOptions.SectionName).Get<ProvisioningOptions>()
            ?? new ProvisioningOptions());
        builder.Services.AddSingleton<ProvisioningProcessor>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<ProvisioningProcessor>());
        return builder;
    }
}
