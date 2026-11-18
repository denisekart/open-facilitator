// A hand-rolled `@microsoft/signalr` for the unit tier: the module namespace
// `test/core/signalr.test.ts` and `test/core/linkd.test.ts` mock the real
// package with (`vi.mock("@microsoft/signalr", () => import(...))`). Every
// built HubConnection is recorded in `hubs` so a test can script `invoke`
// answers, fail a `start`, and read back the pushes it emitted — no live hub,
// no socket, no wall clock.
//
// HubConnectionState's numerics mirror the real enum's shape only as far as
// the transport reads it (Disconnected / Connected).

export const DISCONNECTED = 0;
export const CONNECTED = 2;

export interface FakeHub {
  state: number;
  serverTimeoutInMilliseconds: number;
  handlers: Map<string, (frame: unknown) => void>;
  starts: number;
  stops: number;
  invokes: { method: string; arg: unknown }[];
  invokeResults: Map<string, unknown>;
  invokeErrors: Map<string, unknown>;
  on(event: string, cb: (frame: unknown) => void): void;
  start(): Promise<void>;
  stop(): Promise<void>;
  invoke<T>(method: string, arg: unknown): Promise<T>;
}

/** Every connection the transport built, oldest first. */
export const hubs: FakeHub[] = [];

/** Every URL passed to `withUrl`, in build order. */
export const builderUrls: string[] = [];

/** Cross-hub controls a test sets before driving the transport. */
export const fakes: {
  /** Answers every hub's `invoke(method)` when the hub has no own answer. */
  defaults: Map<string, unknown>;
  /** Thrown by the NEXT `start()` (a failed connect, which rebuilds the hub). */
  nextStartError: unknown;
} = {
  defaults: new Map(),
  nextStartError: undefined,
};

/** The connection a call is talking to: `connect()` rebuilds whenever the
 * current one is Disconnected, so the live hub is the newest one built. */
export function liveHub(): FakeHub {
  return hubs.at(-1) as FakeHub;
}

export function resetFakes(): void {
  hubs.length = 0;
  builderUrls.length = 0;
  fakes.defaults.clear();
  fakes.nextStartError = undefined;
}

function newHub(): FakeHub {
  const hub: FakeHub = {
    state: DISCONNECTED,
    serverTimeoutInMilliseconds: 30_000,
    handlers: new Map(),
    starts: 0,
    stops: 0,
    invokes: [],
    invokeResults: new Map(),
    invokeErrors: new Map(),
    on: (event, cb) => {
      hub.handlers.set(event, cb);
    },
    start: async () => {
      hub.starts += 1;
      const error = fakes.nextStartError ?? hub.invokeErrors.get("start");
      fakes.nextStartError = undefined;
      if (error !== undefined) throw error;
      hub.state = CONNECTED;
    },
    stop: async () => {
      hub.stops += 1;
      hub.state = DISCONNECTED;
    },
    invoke: async <T>(method: string, arg: unknown): Promise<T> => {
      hub.invokes.push({ method, arg });
      const error = hub.invokeErrors.get(method);
      if (error !== undefined) throw error;
      if (hub.invokeResults.has(method)) return hub.invokeResults.get(method) as T;
      return fakes.defaults.get(method) as T;
    },
  };
  return hub;
}

export const fakeSignalr = {
  HttpTransportType: { WebSockets: 1, ServerSentEvents: 2, LongPolling: 4 },
  LogLevel: { Critical: 0, Error: 1, Warning: 2, Information: 3, Debug: 4, None: 0 },
  HubConnectionState: { Disconnected: DISCONNECTED, Connecting: 1, Connected: CONNECTED },
  HubConnectionBuilder: class {
    withUrl(url: string): this {
      builderUrls.push(url);
      return this;
    }
    configureLogging(): this {
      return this;
    }
    build(): FakeHub {
      const hub = newHub();
      hubs.push(hub);
      return hub;
    }
  },
};
