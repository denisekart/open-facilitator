// Route53 zone resolution (create-or-reference law): a company zone is
// referenced, never registered. Three modes, chosen in this order —
//  1. explicit `hostedZoneId` in the env file ⇒ import attributes, fully
//     offline (the CI/default path: no AWS credentials anywhere);
//  2. OFAC_AWS_LOOKUP set ⇒ real `fromLookup` by domain (operator who has
//     credentials and wants discovery; fails loudly when the zone is
//     missing — the create-or-reference contract);
//  3. neither ⇒ placeholder id + warning. Synth works offline; a real
//     deploy MUST resolve to 1 or 2 first.
import { HostedZone } from "aws-cdk-lib/aws-route53";
import type { IHostedZone } from "aws-cdk-lib/aws-route53";
import { Annotations } from "aws-cdk-lib";
import type { Construct } from "constructs";

export function resolveHostedZone(scope: Construct, domain: string, zoneId?: string): IHostedZone {
  if (zoneId) {
    return HostedZone.fromHostedZoneAttributes(scope, "Zone", {
      zoneName: domain,
      hostedZoneId: zoneId,
    });
  }
  if (process.env.OFAC_AWS_LOOKUP) {
    return HostedZone.fromLookup(scope, "Zone", { domainName: domain });
  }
  Annotations.of(scope).addWarningV2(
    "ofac:offline-zone",
    `Route53 lookup skipped (no hostedZoneId, OFAC_AWS_LOOKUP unset): using placeholder zone for ${domain}. Set platform.hostedZoneId or export OFAC_AWS_LOOKUP=1 before deploying.`,
  );
  return HostedZone.fromHostedZoneAttributes(scope, "Zone", {
    zoneName: domain,
    hostedZoneId: "Z0000000000000",
  });
}
