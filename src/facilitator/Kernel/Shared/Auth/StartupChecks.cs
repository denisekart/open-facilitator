using Facilitator.Kernel.Link;

namespace Facilitator.Kernel.Shared.Auth;

/// <summary>
/// Fail-closed startup gate: a non-Development environment without
/// <c>Link:EnrollmentToken</c> must not serve enrollments at all.
/// </summary>
public sealed class LinkEnrollmentStartupCheck(LinkEnrollmentSecret secret) : IHostedService
{
    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!secret.IsConfigured)
        {
            throw new InvalidOperationException(
                "Link:EnrollmentToken is unset and the environment is not Development — refusing to start (fail closed).");
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
