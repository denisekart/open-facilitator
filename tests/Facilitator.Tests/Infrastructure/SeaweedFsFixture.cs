using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;

using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;

/// <summary>
/// One SeaweedFS container for the WHOLE suite's s3 matrix leg (same
/// static-Lazy pattern as <see cref="PostgresFixture"/>/<see cref="KeycloakFixture"/>
/// — a generic container, no vendor module exists): single-binary
/// <c>server -s3</c> speaking the S3 wire standard on 8333 with a fixed
/// test identity mounted as <c>-s3.config</c> (signed requests fail closed
/// without it, verified against the pinned 4.48 image). The bucket is
/// created once through the S3 API itself (SDK <c>PutBucketAsync</c>), so
/// the fixture bootstraps exactly the surface the kernel later uses. The
/// image tag mirrors <c>reference/versions.env SEAWEEDFS_TAG</c> (the CI
/// meta job guards the pin).
/// </summary>
public sealed class SeaweedFsFixture : IAsyncLifetime
{
    private const ushort S3Port = 8333;

    /// <summary>Bucket every s3-leg kernel creates (content-addressed keys make cross-run collisions the dedupe law, not a problem).</summary>
    public const string Bucket = "ofac-test";

    /// <summary>Test identity the mounted s3.config declares.</summary>
    public const string AccessKey = "ofac-test-access";

    /// <summary>Test secret for <see cref="AccessKey"/>.</summary>
    public const string SecretKey = "ofac-test-secret";

    private static readonly string IdentityConfig =
        """
        {"identities":[{"name":"ofac-admin",
          "credentials":[{"accessKey":"ofac-test-access","secretKey":"ofac-test-secret"}],
          "actions":["Read","Write","List","Tagging","Admin","GetBucketPolicy","PutBucketPolicy"]}]}
        """;

    private static readonly Task<IContainer> Shared =
        new Lazy<Task<IContainer>>(StartSharedAsync).Value;

    /// <inheritdoc />
    public Task InitializeAsync() => Shared;

    /// <inheritdoc />
    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>S3 endpoint URL the kernel + tests address (valid after initialization).</summary>
    public static string Endpoint =>
        $"http://{Shared.Result.Hostname}:{Shared.Result.GetMappedPublicPort(S3Port)}";

    private static async Task<IContainer> StartSharedAsync()
    {
        var config = Path.Combine(Path.GetTempPath(), $"ofac-seaweed-s3-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(config, IdentityConfig);
        var container = new ContainerBuilder("docker.io/chrislusf/seaweedfs:4.48")
            .WithCommand(
                "server",
                "-s3",
                "-s3.port=8333",
                "-ip=127.0.0.1",
                "-ip.bind=0.0.0.0",
                "-dir=/tmp",
                "-s3.config=/etc/s3.json")
            // -ip.bind=0.0.0.0: -ip only names the node in the topology; without
            // the bind override the listeners stay on the container's loopback
            // and the published port never answers. -dir=/tmp: the image's
            // default volume layout stalls the filer's raft boot on ephemeral
            // single-node runs (verified against the pinned 4.48 image).
            .WithBindMount(config, "/etc/s3.json", AccessMode.ReadOnly)
            .WithPortBinding(S3Port, true)
            .WithCleanUp(true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(S3Port))
            .Build();
        await container.StartAsync();

        // The endpoint must come from the container handle, NOT the static
        // Endpoint property: reading it here re-enters this task's own
        // Lazy<Task<IContainer>> (Task.Wait on the still-running init ->
        // guaranteed deadlock).
        var endpoint = $"http://{container.Hostname}:{container.GetMappedPublicPort(S3Port)}";

        // Bucket bootstrap through the S3 API proper, retried until the filer
        // finishes raft leader election (the port answers before buckets do).
        using var client = new AmazonS3Client(
            new BasicAWSCredentials(AccessKey, SecretKey),
            new AmazonS3Config
            {
                ServiceURL = endpoint,
            });
        var deadline = DateTime.UtcNow.AddMinutes(2);
        while (true)
        {
            try
            {
                await client.PutBucketAsync(Bucket);
                break;
            }
            catch (AmazonS3Exception ex) when (
                ex.StatusCode == System.Net.HttpStatusCode.Conflict
                || ex.ErrorCode is "BucketAlreadyOwnedByYou" or "BucketAlreadyExists")
            {
                // Shared container: an earlier run already made the bucket.
                break;
            }
            catch (AmazonServiceException) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(1000);
            }
            catch (AmazonClientException) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(1000);
            }
        }

        return container;
    }
}
