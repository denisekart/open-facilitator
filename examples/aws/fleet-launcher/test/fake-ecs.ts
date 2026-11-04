// The fake ECS API: records RunTask/StopTask calls and hands out sequential
// task ARNs, so the whole launcher loop runs without AWS (the oci tier's
// FakeDocker analog, at the seam level instead of a unix socket).
import type { EcsApi, EcsRunTaskParams, EcsStopTaskParams } from "../src/ecs.js";

export class FakeEcs implements EcsApi {
  readonly runs: EcsRunTaskParams[] = [];
  readonly stops: EcsStopTaskParams[] = [];
  private next = 1;

  async runTask(params: EcsRunTaskParams): Promise<{ taskArn: string }> {
    this.runs.push(params);
    return { taskArn: `arn:aws:ecs:eu-west-1:222222222222:task/fleet-${this.next++}` };
  }

  async stopTask(params: EcsStopTaskParams): Promise<void> {
    this.stops.push(params);
  }
}
