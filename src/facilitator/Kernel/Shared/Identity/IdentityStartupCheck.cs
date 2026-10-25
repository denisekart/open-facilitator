using Microsoft.Extensions.Options;

namespace Facilitator.Kernel.Shared.Identity;

/// <summary>
/// Fail-closed startup gate (generalizes <c>BootstrapAuthStartupCheck</c>):
/// the kernel refuses to boot in ANY environment without a valid, complete
/// <c>identity:</c> block — staged rules per <see cref="IdentityOptions.Validate"/>.
/// </summary>
public sealed class IdentityStartupCheck(IOptions<IdentityOptions> options) : IHostedService
{
    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var errors = options.Value.Validate();
        if (errors.Count > 0)
        {
            throw new InvalidOperationException(
                "identity: configuration invalid — refusing to start (fail closed): "
                + string.Join(" | ", errors));
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
