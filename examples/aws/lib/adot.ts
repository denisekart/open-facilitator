// Shared ADOT sidecar shape: the collector config is code, not a drifted
// ops artifact, so KernelService and TeamPlane ship the IDENTICAL sidecar.
//
// Essential-container law: otelcol-contrib EXITS immediately without a
// `--config`, and an essential sidecar failure kills the task — so the
// config must ride with the container. The contrib image's ENTRYPOINT is
// `/otelcol-contrib`; ECS `command` overrides CMD (not ENTRYPOINT), so
// `command: ["--config=env:ADOT_CONFIG"]` runs `/otelcol-contrib
// --config=…`, and the env provider reads the config CONTENT from the
// env var (YAML or JSON accepted).
//
// The metric namespace is a SEPARATE env var (`ADOT_NAMESPACE`) expanded
// inside the config at startup: the stack wiring passes the same prop
// value to the CloudWatch alarms, so sidecar and alarms cannot drift.
import { AwsLogDriver, ContainerImage } from "aws-cdk-lib/aws-ecs";
import type { ContainerDefinitionOptions } from "aws-cdk-lib/aws-ecs";
import type { LogGroup } from "aws-cdk-lib/aws-logs";

/** ADOT collector image — pinned tag; provenance documented in README. */
export const ADOT_IMAGE = "public.ecr.aws/otel/opentelemetry-collector-contrib:0.144.0";

/** Default OTLP→CloudWatch custom-metric namespace (README: confirm at
 *  first real deploy; one prop feeds sidecar config AND the alarms). */
export const DEFAULT_METRIC_NAMESPACE = "facilitator";

/**
 * Collector config: OTLP in on 4317 (grpc) + 4318 (http/protobuf) — the
 * ports the app containers point at on localhost; batch; metrics to
 * CloudWatch via awsemf under ADOT_NAMESPACE, traces to X-Ray, logs to
 * debug (the sidecar's own stdout is already collected by its awslogs
 * driver, so no duplicate log pipeline).
 */
export function adotConfig(): string {
  return JSON.stringify({
    receivers: {
      otlp: {
        protocols: {
          grpc: { endpoint: "0.0.0.0:4317" },
          http: { endpoint: "0.0.0.0:4318" },
        },
      },
    },
    processors: { batch: {} },
    exporters: {
      // namespace alone: every kernel gauge lands under it unfiltered
      // (declarations are for aggregation/drop rules, not required).
      awsemf: { namespace: "${env:ADOT_NAMESPACE}" },
      awsxray: {},
      debug: {},
    },
    service: {
      pipelines: {
        metrics: { receivers: ["otlp"], processors: ["batch"], exporters: ["awsemf"] },
        traces: { receivers: ["otlp"], processors: ["batch"], exporters: ["awsxray"] },
        logs: { receivers: ["otlp"], processors: ["batch"], exporters: ["debug"] },
      },
    },
  });
}

/**
 * The sidecar container options: pinned image, its own log group, config
 * via the env provider, namespace injected per deployment.
 */
export function adotContainerOptions(
  logGroup: LogGroup,
  metricNamespace: string,
): ContainerDefinitionOptions {
  return {
    image: ContainerImage.fromRegistry(ADOT_IMAGE),
    logging: new AwsLogDriver({ logGroup, streamPrefix: "adot" }),
    command: ["--config=env:ADOT_CONFIG"],
    environment: {
      ADOT_CONFIG: adotConfig(),
      ADOT_NAMESPACE: metricNamespace,
    },
  };
}
