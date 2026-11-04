import { Duration, RemovalPolicy } from "aws-cdk-lib";
import { BlockPublicAccess, Bucket, BucketEncryption, StorageClass } from "aws-cdk-lib/aws-s3";
import type { IBucket } from "aws-cdk-lib/aws-s3";
import type { IKey } from "aws-cdk-lib/aws-kms";
import { Key } from "aws-cdk-lib/aws-kms";
import { Construct } from "constructs";

/**
 * Artifact store: versioned SSE-KMS bucket with public access fully
 * blocked. Content-addressed keys (the kernel's SHA-256 law) make
 * overwrites idempotent, so versioning is pure audit; noncurrent versions
 * expire — the kernel's retention GC owns the live set, S3 lifecycle owns
 * the tombstones. Access logs land in a separate, locked-down bucket so
 * the log sink can never be written to by the workload it audits.
 */
export interface ArtifactBucketProps {
  readonly production?: boolean;
}

export class ArtifactBucket extends Construct {
  readonly bucket: IBucket;
  readonly accessLogs: IBucket;
  readonly encryptionKey: IKey;

  constructor(scope: Construct, id: string, props: ArtifactBucketProps = {}) {
    super(scope, id);
    const prod = props.production ?? false;
    this.encryptionKey = new Key(this, "KmsKey", {
      enableKeyRotation: true,
      description: "ofac artifact-store CMK",
    });
    this.accessLogs = new Bucket(this, "AccessLogs", {
      encryption: BucketEncryption.S3_MANAGED,
      blockPublicAccess: BlockPublicAccess.BLOCK_ALL,
      enforceSSL: true,
      lifecycleRules: [{ expiration: Duration.days(180) }],
      removalPolicy: prod ? RemovalPolicy.RETAIN : RemovalPolicy.DESTROY,
    });
    this.bucket = new Bucket(this, "Artifacts", {
      versioned: true,
      encryption: BucketEncryption.KMS,
      encryptionKey: this.encryptionKey,
      blockPublicAccess: BlockPublicAccess.BLOCK_ALL,
      enforceSSL: true,
      serverAccessLogsBucket: this.accessLogs,
      serverAccessLogsPrefix: "artifacts/",
      lifecycleRules: [
        {
          // Tombstone versions expire fast; live digests stay until GC.
          noncurrentVersionExpiration: Duration.days(30),
          abortIncompleteMultipartUploadAfter: Duration.days(3),
        },
        {
          // Cold digests ride IA from day 90; deeper tiers are a policy
          // choice the consuming team makes, not the example's default.
          transitions: [
            {
              storageClass: StorageClass.INFREQUENT_ACCESS,
              transitionAfter: Duration.days(90),
            },
          ],
        },
      ],
      removalPolicy: prod ? RemovalPolicy.RETAIN : RemovalPolicy.DESTROY,
    });
  }
}
