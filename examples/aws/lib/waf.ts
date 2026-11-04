import { CfnWebACL } from "aws-cdk-lib/aws-wafv2";
import { Construct } from "constructs";

/**
 * WAFv2 web ACL for the single web face. AWS managed groups catch the
 * protocol/input garbage (KnownBadInputs, IPReputation, SQLi); the rate
 * rule protects the two public path sets — the webhook ingress (third
 * parties POST here) and the link-plane door (enrollment is the
 * unauthenticated endpoint; the kernel rate-limits it, this rule keeps
 * abuse off the limiter itself). Separate construct: deleting it detaches
 * protection without touching the serving path.
 */
export interface WafProps {
  /** Path prefixes the rate rule guards (webhook + enroll). */
  readonly rateLimitedPaths?: string[];
  readonly rateRequestsPer5Min?: number;
}

const visibility = (metric: string): CfnWebACL.VisibilityConfigProperty => ({
  cloudWatchMetricsEnabled: true,
  metricName: metric,
  sampledRequestsEnabled: true,
});

export class Waf extends Construct {
  readonly webAcl: CfnWebACL;

  constructor(scope: Construct, id: string, props: WafProps = {}) {
    super(scope, id);
    const ratePaths = props.rateLimitedPaths ?? ["/api/ingress/", "/api/link/v1/enroll"];
    this.webAcl = new CfnWebACL(this, "Acl", {
      scope: "REGIONAL",
      defaultAction: { allow: {} },
      visibilityConfig: visibility("ofac-webacl"),
      rules: [
        {
          name: "ofac-rate-public-paths",
          priority: 1,
          action: { block: {} },
          statement: {
            rateBasedStatement: {
              limit: props.rateRequestsPer5Min ?? 500,
              aggregateKeyType: "IP",
              scopeDownStatement: {
                orStatement: {
                  statements: ratePaths.map((prefix): CfnWebACL.StatementProperty => ({
                    byteMatchStatement: {
                      fieldToMatch: { uriPath: {} },
                      positionalConstraint: "STARTS_WITH",
                      searchString: prefix,
                      textTransformations: [{ priority: 0, type: "NONE" }],
                    },
                  })),
                },
              },
            },
          },
          visibilityConfig: visibility("ofac-rate-public-paths"),
        },
        ...[
          ["KnownBadInputs", "AWSManagedRulesKnownBadInputsRuleSet"],
          ["IPReputation", "AWSManagedRulesAmazonIpReputationList"],
          ["SQLi", "AWSManagedRulesSQLiRuleSet"],
        ].map(([suffix, groupName], i): CfnWebACL.RuleProperty => ({
          name: `ofac-${suffix}`,
          priority: 10 + i,
          action: { block: {} },
          statement: {
            managedRuleGroupStatement: { vendorName: "AWS", name: groupName },
          },
          visibilityConfig: visibility(`ofac-${suffix}`),
        })),
      ],
    });
  }
}
