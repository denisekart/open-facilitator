// The ECS seam (the oci launcher's DockerApi analog): a thin injectable
// surface over @aws-sdk/client-ecs so the whole launcher loop runs against a
// fake in tests. The production client takes NO static keys — the default
// credential chain (task role → IMDSv2) is the AWS creds law; region rides
// AWS_REGION (task env), never code.
import { ECSClient, RunTaskCommand, StopTaskCommand } from "@aws-sdk/client-ecs";

export interface EnvVar {
  name: string;
  value: string;
}

export interface EcsTag {
  key: string;
  value: string;
}

export interface EcsRunTaskParams {
  cluster: string;
  taskDefinition: string;
  /** awsvpc placement pin; empty ⇒ the task definition's own placement. */
  subnets: readonly string[];
  /** The expanded spec env, shipped as containerOverrides.environment
   *  (ECS merge law: same-name env overrides the def, new names add). */
  environment: readonly EnvVar[];
  /** spec.entrypoint analog (the container command override). */
  command?: readonly string[] | undefined;
  tags: readonly EcsTag[];
  startedBy: string;
}

export interface EcsStopTaskParams {
  cluster: string;
  taskArn: string;
}

export interface EcsApi {
  /** Run ONE task; resolves with the started task's ARN. */
  runTask(params: EcsRunTaskParams): Promise<{ taskArn: string }>;
  /** Stop ONE task by ARN. */
  stopTask(params: EcsStopTaskParams): Promise<void>;
}

export class EcsClient implements EcsApi {
  private readonly client: ECSClient;

  constructor(client: ECSClient = new ECSClient()) {
    this.client = client;
  }

  async runTask(params: EcsRunTaskParams): Promise<{ taskArn: string }> {
    const output = await this.client.send(
      new RunTaskCommand({
        cluster: params.cluster,
        taskDefinition: params.taskDefinition,
        startedBy: params.startedBy,
        enableECSManagedTags: true,
        tags: params.tags.map((t) => ({ key: t.key, value: t.value })),
        ...(params.subnets.length === 0
          ? {}
          : {
              networkConfiguration: {
                awsvpcConfiguration: {
                  subnets: [...params.subnets],
                },
              },
            }),
        overrides: {
          containerOverrides: [
            {
              // No containerName: the override targets the fleet task
              // definition's containers generically (index 0 = the single
              // workload container, the TeamFleetStack contract).
              environment: params.environment.map((e) => ({ name: e.name, value: e.value })),
              ...(params.command === undefined || params.command.length === 0
                ? {}
                : { command: [...params.command] }),
            },
          ],
        },
      }),
    );
    const task = output.tasks?.[0];
    const taskArn = task?.taskArn;
    if (taskArn === undefined || taskArn === "") {
      throw new Error("RunTask answered with no task ARN (failures: refused to guess)");
    }
    return { taskArn };
  }

  async stopTask(params: EcsStopTaskParams): Promise<void> {
    await this.client.send(new StopTaskCommand({ cluster: params.cluster, task: params.taskArn }));
  }
}
