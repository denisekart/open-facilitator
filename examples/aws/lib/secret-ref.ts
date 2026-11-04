import { Secret } from "aws-cdk-lib/aws-secretsmanager";
import type { ISecret } from "aws-cdk-lib/aws-secretsmanager";
import { SecretValue } from "aws-cdk-lib";
import type { IKey } from "aws-cdk-lib/aws-kms";
import { Construct } from "constructs";

/**
 * Create-or-reference secret wrapper: org-shared secrets (IdP client
 * secret, provider webhooks the company already rotates centrally) come
 * in by name/arn; everything the example owns is created under the
 * account CMK. Team enrollment secrets are created with a placeholder and
 * filled by the documented manual hand-off — short-lived re-enroll is the
 * rotation story, so no rotation machinery lives here.
 */
export interface SecretRefProps {
  /** Set ⇒ import by name/arn instead of creating. */
  readonly secretName?: string;
  readonly secretArn?: string;
  /** Created-path only. */
  readonly initialPlaceholders?: Record<string, string>;
  readonly description?: string;
  readonly encryptionKey?: IKey;
}

export class SecretRef extends Construct {
  readonly secret: ISecret;
  /** True when this construct created the secret (placeholder path). */
  readonly created: boolean;

  constructor(scope: Construct, id: string, props: SecretRefProps) {
    super(scope, id);
    if (props.secretArn || props.secretName) {
      this.secret = props.secretArn
        ? Secret.fromSecretCompleteArn(this, "Imported", props.secretArn)
        : Secret.fromSecretNameV2(this, "Imported", props.secretName!);
      this.created = false;
    } else {
      this.secret = new Secret(this, "Created", {
        secretObjectValue: Object.fromEntries(
          Object.entries(props.initialPlaceholders ?? { value: "REPLACE_ME" }).map(([k, v]) => [
            k,
            SecretValue.unsafePlainText(v),
          ]),
        ) as { [key: string]: SecretValue },
        ...(props.description ? { description: props.description } : {}),
        ...(props.encryptionKey ? { encryptionKey: props.encryptionKey } : {}),
      });
      this.created = true;
    }
  }
}
