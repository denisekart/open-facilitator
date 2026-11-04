import { Duration } from "aws-cdk-lib";
import { Alarm, ComparisonOperator, Metric, TreatMissingData } from "aws-cdk-lib/aws-cloudwatch";
import { SnsAction } from "aws-cdk-lib/aws-cloudwatch-actions";
import { PolicyStatement } from "aws-cdk-lib/aws-iam";
import type { IRole } from "aws-cdk-lib/aws-iam";
import { Topic } from "aws-cdk-lib/aws-sns";
import { EmailSubscription } from "aws-cdk-lib/aws-sns-subscriptions";
import type { IApplicationLoadBalancer } from "aws-cdk-lib/aws-elasticloadbalancingv2";
import { Construct } from "constructs";

/**
 * Observability plane: the IAM fragments an ADOT sidecar needs to ship
 * telemetry and the alarm set that makes the kernel's health visible,
 * behind one SNS pager. The sidecar container itself rides the task
 * definition (prop-gated); deleting this stack removes the pipeline's
 * permissions and alarms — telemetry goes dark, nothing else moves.
 *
 * The outbox-depth gauge arrives via the ADOT OTLP→CloudWatch exporter.
 * Its metric NAMESPACE depends on the collector's configuration at deploy
 * time, so it is a prop (default `facilitator`) — the first real deploy
 * confirms or adjusts the prop, never the code.
 */
export interface ObservabilityProps {
  /** OTLP→CW exporter namespace; confirmed at first deploy. */
  readonly metricNamespace?: string;
  /** Pager for every alarm here. */
  readonly alarmEmail?: string;
  readonly kernelTaskRole?: IRole;
  readonly loadBalancer?: IApplicationLoadBalancer;
}

export class Observability extends Construct {
  readonly topic: Topic;

  constructor(scope: Construct, id: string, props: ObservabilityProps) {
    super(scope, id);
    const ns = props.metricNamespace ?? "facilitator";
    this.topic = new Topic(this, "Alarms", {
      displayName: "ofac kernel alarms",
    });
    if (props.alarmEmail) {
      this.topic.addSubscription(new EmailSubscription(props.alarmEmail));
    }
    const alarm = (name: string, metric: Metric, threshold: number): void => {
      new Alarm(this, name, {
        metric,
        threshold,
        evaluationPeriods: 2,
        datapointsToAlarm: 2,
        comparisonOperator: ComparisonOperator.GREATER_THAN_THRESHOLD,
        treatMissingData: TreatMissingData.NOT_BREACHING,
      }).addAlarmAction(new SnsAction(this.topic));
    };
    // Kernel saturation: OTLP gauge from the kernel's own metrics.
    // Gauge, not counter: max over the window is the honest signal.
    alarm(
      "OutboxDepth",
      new Metric({
        namespace: ns,
        metricName: "facilitator.outbox.queue_depth",
        statistic: "Maximum",
        period: Duration.minutes(5),
      }),
      500,
    );
    // Dead letters piling up = a sink stopped consuming.
    alarm(
      "DeadLetters",
      new Metric({
        namespace: ns,
        metricName: "facilitator.outbox.dead_letters",
        statistic: "Maximum",
        period: Duration.minutes(5),
      }),
      1,
    );
    if (props.loadBalancer) {
      // 5xx at the face: kernel sick or rolled wrong. The ALB dimension
      // wants the ARN suffix (app/net/...), not the name.
      const suffix = props.loadBalancer.loadBalancerArn.split("/").slice(-2).join("/");
      alarm(
        "Alb5xx",
        new Metric({
          namespace: "AWS/ApplicationELB",
          metricName: "HTTPCode_Target_5XX_Count",
          dimensionsMap: { LoadBalancer: suffix },
          statistic: "Sum",
          period: Duration.minutes(5),
        }),
        25,
      );
    }
    // The ADOT sidecar's publish identity: OTLP metrics/logs + X-Ray.
    // Write-only telemetry verbs; account-scoped resources because
    // PutMetricData carries no resource ARN.
    if (props.kernelTaskRole) {
      props.kernelTaskRole.addToPrincipalPolicy(
        new PolicyStatement({
          actions: [
            "cloudwatch:PutMetricData",
            "logs:CreateLogStream",
            "logs:PutLogEvents",
            "logs:DescribeLogGroups",
            "logs:DescribeLogStreams",
            "logs:CreateLogGroup",
            "xray:PutTraceSegments",
            "xray:PutTelemetryRecords",
            "xray:GetSamplingRules",
            "xray:GetSamplingTargets",
          ],
          resources: ["*"],
        }),
      );
    }
  }
}
