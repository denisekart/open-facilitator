using Facilitator.Kernel.Shared.Storage;

namespace Facilitator.Tests.Storage;

/// <summary>
/// The credential-pair law of <see cref="S3ArtifactStore"/>: static keys are
/// optional AS A PAIR — both absent hands signing to the SDK default chain
/// (task role → IMDSv2, the shape on AWS where infra ships zero static keys),
/// a half-set pair is a misconfiguration and fails closed naming both keys.
/// The both-set path is covered by the SeaweedFS legs (static keys against a
/// fake endpoint prove byte-identical behavior).
/// </summary>
public sealed class S3CredentialChainTests : IDisposable
{
    private static ArtifactOptions OptionsNoKeys => new()
    {
        Provider = "s3",
        S3 =
        {
            Endpoint = "https://s3.eu-west-1.amazonaws.com",
            Bucket = "ofac-artifacts",
            Region = "eu-west-1",
            ForcePathStyle = false,
        },
    };

    public void Dispose() => EnvScope.Clear();

    [Fact]
    public async Task NoStaticKeysSelectsTheDefaultChainAndPresignsWithIt()
    {
        // The chain's first leg is the environment; seed it so resolution is
        // deterministic off-AWS. A presigned URL carrying the env access key
        // proves the client signs with chain credentials, not a fail-closed
        // static pair.
        EnvScope.SeedEnv("AKIAOFACTESTKEY00000", "ofac-chain-secret");
        using var store = new S3ArtifactStore(OptionsNoKeys);
        var url = await store.PresignUploadAsync("ab/abcd", TimeSpan.FromMinutes(1), default);
        Assert.NotNull(url);
        Assert.Contains("AKIAOFACTESTKEY00000", url!.Query, StringComparison.Ordinal);
    }

    [Fact]
    public void HalfSetCredentialPairFailsClosedNamingBothKeys()
    {
        var onlyAccess = OptionsNoKeys;
        onlyAccess.S3.AccessKey = "AKIAOFACTESTKEY00000";
        var accessEx = Assert.Throws<InvalidOperationException>(() => new S3ArtifactStore(onlyAccess));
        Assert.Contains("Storage:S3:AccessKey", accessEx.Message, StringComparison.Ordinal);
        Assert.Contains("Storage:S3:SecretKey", accessEx.Message, StringComparison.Ordinal);

        var onlySecret = OptionsNoKeys;
        onlySecret.S3.SecretKey = "ofac-chain-secret";
        var secretEx = Assert.Throws<InvalidOperationException>(() => new S3ArtifactStore(onlySecret));
        Assert.Contains("Storage:S3:AccessKey", secretEx.Message, StringComparison.Ordinal);
        Assert.Contains("Storage:S3:SecretKey", secretEx.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingRegionStillFailsClosedEvenOnTheChainPath()
    {
        var noRegion = OptionsNoKeys;
        noRegion.S3.Region = null;
        var ex = Assert.Throws<InvalidOperationException>(() => new S3ArtifactStore(noRegion));
        Assert.Contains("Storage:S3:Region", ex.Message, StringComparison.Ordinal);
        // The pair is not named: both-absent is the chain request, not a miss.
        Assert.DoesNotContain("Storage:S3:AccessKey", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>AWS env-credential scope, restored on dispose.</summary>
    private static class EnvScope
    {
        public static void SeedEnv(string key, string secret)
        {
            Environment.SetEnvironmentVariable("AWS_ACCESS_KEY_ID", key);
            Environment.SetEnvironmentVariable("AWS_SECRET_ACCESS_KEY", secret);
            Environment.SetEnvironmentVariable("AWS_REGION", "eu-west-1");
        }

        public static void Clear()
        {
            foreach (var name in new[] { "AWS_ACCESS_KEY_ID", "AWS_SECRET_ACCESS_KEY", "AWS_REGION" })
            {
                Environment.SetEnvironmentVariable(name, null);
            }
        }
    }
}
