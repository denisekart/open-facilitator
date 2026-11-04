import { Duration, RemovalPolicy } from "aws-cdk-lib";
import {
  Mfa,
  OAuthScope,
  UserPool,
  UserPoolClient,
  UserPoolDomain,
  UserPoolIdentityProviderOidc,
  UserPoolIdentityProviderSaml,
  UserPoolIdentityProviderSamlMetadata,
  VerificationEmailStyle,
} from "aws-cdk-lib/aws-cognito";
import type { IUserPool, IUserPoolClient } from "aws-cdk-lib/aws-cognito";
import { Construct } from "constructs";
import type { Federation } from "./env.js";

/**
 * Cognito front door for the admin plane + SPA. The client set mirrors
 * `reference/keycloak/realm.json`:
 *  - `facilitator-api` — the SPA: public client, authorization-code +
 *    PKCE, no implicit flow.
 *  - `facilitator-cli` — the machine leg: client credentials (the CLI and
 *    gateways mint tokens against this pool).
 * A corporate IdP (OIDC or SAML) federates IN, so the AWS-native front
 * door remains the single issuer the kernel pins
 * (`identity:oidc:authority` = this pool's issuer,
 * `identity:oidc:audience` = the SPA client id). Keycloak-on-ECS is the
 * 1:1 port of the compose reference env and is documented, not stacked.
 */
export interface IdentityProps {
  /** Managed-login domain prefix (e.g. "acme-kernel"). */
  readonly domainPrefix: string;
  /** The public kernel origin the SPA redirects back to. */
  readonly kernelDomain: string;
  readonly production?: boolean;
  readonly federation?: Federation | null;
}

export class Identity extends Construct {
  readonly userPool: IUserPool;
  readonly spaClient: IUserPoolClient;
  readonly machineClient: IUserPoolClient;
  /** Token issuer the kernel pins; byte-match contract. */
  readonly issuer: string;
  readonly apiClientId: string;

  constructor(scope: Construct, id: string, props: IdentityProps) {
    super(scope, id);
    const prod = props.production ?? false;
    this.userPool = new UserPool(this, "Pool", {
      userPoolName: "facilitator",
      selfSignUpEnabled: false,
      signInCaseSensitive: false,
      passwordPolicy: {
        minLength: 12,
        requireLowercase: true,
        requireUppercase: true,
        requireDigits: true,
        requireSymbols: true,
        tempPasswordValidity: Duration.days(3),
      },
      userVerification: {
        emailStyle: VerificationEmailStyle.CODE,
      },
      mfa: Mfa.OPTIONAL,
      removalPolicy: prod ? RemovalPolicy.RETAIN : RemovalPolicy.DESTROY,
    });
    this.issuer = `https://cognito-idp.${this.userPool.stack.region}.amazonaws.com/${this.userPool.userPoolId}`;
    new UserPoolDomain(this, "Domain", {
      userPool: this.userPool,
      cognitoDomain: { domainPrefix: props.domainPrefix },
    });
    const origin = `https://${props.kernelDomain}`;
    this.spaClient = new UserPoolClient(this, "ApiClient", {
      userPool: this.userPool,
      userPoolClientName: "facilitator-api",
      generateSecret: false,
      oAuth: {
        // authorizationCodeGrant + PKCE (public client, no implicit).
        flows: { authorizationCodeGrant: true },
        callbackUrls: [`${origin}/`],
        logoutUrls: [`${origin}/`],
        scopes: [OAuthScope.OPENID, OAuthScope.EMAIL, OAuthScope.PROFILE],
      },
      idTokenValidity: Duration.hours(1),
      accessTokenValidity: Duration.hours(1),
      preventUserExistenceErrors: true,
    });
    this.machineClient = new UserPoolClient(this, "CliClient", {
      userPool: this.userPool,
      userPoolClientName: "facilitator-cli",
      generateSecret: true,
      oAuth: {
        flows: { clientCredentials: true },
        scopes: [OAuthScope.OPENID],
      },
      preventUserExistenceErrors: true,
    });
    this.apiClientId = this.spaClient.userPoolClientId;
    const fed = props.federation;
    if (fed?.type === "oidc" && fed.metadataUrl && fed.clientId) {
      new UserPoolIdentityProviderOidc(this, "CorpOidc", {
        userPool: this.userPool,
        name: fed.name,
        clientId: fed.clientId,
        // Cognito needs the plaintext secret at configure time; it arrives
        // via cdk context, never committed.
        clientSecret: fed.clientSecret ?? "",
        issuerUrl: fed.metadataUrl,
        attributeMapping: { email: { attributeName: "email" } },
      });
    }
    if (fed?.type === "saml" && fed.metadataXml) {
      new UserPoolIdentityProviderSaml(this, "CorpSaml", {
        userPool: this.userPool,
        name: fed.name,
        metadata: UserPoolIdentityProviderSamlMetadata.file(fed.metadataXml),
        attributeMapping: { email: { attributeName: "email" } },
      });
    }
  }
}
