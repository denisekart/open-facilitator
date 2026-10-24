// One subscriber list + fan-out loop, shared by every push surface: the
// transports' Deliver/FlowControl/Disconnect registries and the client's
// event bus. Subscribing after an emit never replays (frames are live-only;
// durability is the ledger's job).
export class FrameEmitter<T> {
  private readonly callbacks: ((frame: T) => void)[] = [];

  /** Register a listener; the returned function detaches exactly it. */
  subscribe(cb: (frame: T) => void): () => void {
    this.callbacks.push(cb);
    return () => {
      const index = this.callbacks.indexOf(cb);
      if (index >= 0) this.callbacks.splice(index, 1);
    };
  }

  emit(frame: T): void {
    for (const cb of this.callbacks) cb(frame);
  }
}
