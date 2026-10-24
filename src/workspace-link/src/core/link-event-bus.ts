// The client's listener registry: one place owns the `state`/`error`/`welcome`
// callback lists, the current state, and the transition log. `LinkClient.on`
// delegates here; everything that changes state routes through `setState` so
// the debug trail and the subscriber fan-out can never disagree.
import { FrameEmitter } from "./frame-emitter.js";
import type { LinkLogger, LinkSession, LinkState } from "./types.js";

export type ClientEvents = {
  state: (state: LinkState, previous: LinkState) => void;
  error: (error: Error) => void;
  welcome: (session: LinkSession) => void;
};

export class LinkEventBus {
  private readonly states = new FrameEmitter<[LinkState, LinkState]>();
  private readonly errors = new FrameEmitter<Error>();
  private readonly welcomes = new FrameEmitter<LinkSession>();
  private _state: LinkState = "idle";

  constructor(
    private readonly logger: LinkLogger,
    private readonly endpointId: string,
  ) {}

  get state(): LinkState {
    return this._state;
  }

  setState(next: LinkState): void {
    if (this._state === next) return;
    const previous = this._state;
    this._state = next;
    this.logger.debug(`link state ${previous} → ${next}`, { endpointId: this.endpointId });
    this.states.emit([next, previous]);
  }

  emitError(error: Error): void {
    this.errors.emit(error);
  }

  emitWelcome(session: LinkSession): void {
    this.welcomes.emit(session);
  }

  on<K extends keyof ClientEvents>(event: K, cb: ClientEvents[K]): () => void {
    switch (event) {
      case "state": {
        const stateCb = cb as ClientEvents["state"];
        return this.states.subscribe(([state, previous]) => stateCb(state, previous));
      }
      case "error":
        return this.errors.subscribe(cb as ClientEvents["error"]);
      case "welcome":
        return this.welcomes.subscribe(cb as ClientEvents["welcome"]);
    }
  }
}
