namespace Facilitator.Kernel.Shared.Storage;

/// <summary>
/// Fail-closed startup gate for the storage feature (the
/// <see cref="Facilitator.Kernel.Shared.Identity.IdentityStartupCheck"/>
/// precedent): an <c>s3</c> provider with an incomplete or malformed
/// <c>Storage:S3</c> block refuses to boot, naming every offender — a half-
/// configured object store must never serve tickets that 500 on first PUT.
/// </summary>
public sealed class StorageStartupCheck(ArtifactOptions options) : IHostedService
{
    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!string.Equals(options.Provider, "s3", StringComparison.Ordinal))
        {
            return Task.CompletedTask;
        }

        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(options.S3.Endpoint) || !Uri.IsWellFormedUriString(options.S3.Endpoint, UriKind.Absolute))
        {
            errors.Add("Storage:S3:Endpoint (absolute URL)");
        }

        if (string.IsNullOrWhiteSpace(options.S3.Bucket))
        {
            errors.Add("Storage:S3:Bucket");
        }

        if (string.IsNullOrWhiteSpace(options.S3.Region))
        {
            errors.Add("Storage:S3:Region");
        }

        if (string.IsNullOrWhiteSpace(options.S3.AccessKey))
        {
            errors.Add("Storage:S3:AccessKey");
        }

        if (string.IsNullOrWhiteSpace(options.S3.SecretKey))
        {
            errors.Add("Storage:S3:SecretKey");
        }

        if (errors.Count > 0)
        {
            throw new InvalidOperationException(
                "Storage: provider=s3 configuration invalid — refusing to start (fail closed): "
                + string.Join(" | ", errors));
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
