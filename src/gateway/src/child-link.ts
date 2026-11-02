// The Gateway-side fleet: `ChildLink` is a ChildExecutor over an established
// child connection (the 05 seam carried over the socket), `ChildFleet` is the
// live registry the runtime routes over — capability union index, deny-
// ambiguity conflict law, per-child quotas, the child-generation mirror, and
// the union manifest the debounced re-POST serializes.
//
// Fleet-of-one law: the embedded child is ONE MEMBER of the fleet, not a
// special case — routing, quota, status, and reconcile read the same member
// shape; only construction differs.
import { randomBytes } from "node:crypto";
import type { CapabilityDescriptor, CapabilityManifestV1, EnvelopeEvent } from "@ofac/protocol";
import { type LinkLogger } from "@ofac/workspace-link";
import { digestOfManifest } from "@ofac/workspace-link/node";
import type { ChildExecutor, ChildMessage, ChildReply } from "./child.js";
import type { ChildEntryConfig } from "./config.js";
import type { ChildConnectionState, ChildStateRow } from "./ledger.js";
import type {
  CommandFrame,
  ReplyFrame,
  EventFrame,
  ChildWireFrame,
  GoawayReason,
} from "./childwire.js";
/** Hard bound on concurrent child connections (boring, locked). */
export const MAX_CHILDREN = 32;

/** Transport-dead answer: the caller applies the lost/failed law per site. */
export class ChildTransportError extends Error {
  constructor(readonly reason: string) {
    super(`child transport: ${reason}`);
    this.name = "ChildTransportError";
  }
}

/** One routable fleet member: an embedded builtin or a live socket child. */
export interface FleetMember {
  readonly childId: string;
  readonly manifest: CapabilityManifestV1;
  readonly executor: ChildExecutor;
  /** Child-connection fence (0 for embedded: no transport, no re-register). */
  readonly generation: number;
  readonly connected: boolean;
  /** Socket children answer `goaway` before the socket dies. */
  goaway(reason: GoawayReason): void;
}

/** The embedded builtin wrapped as a fleet member (fleet-of-one law). */
export class EmbeddedMember implements FleetMember {
  readonly generation = 0;
  connected = true;
  constructor(
    readonly childId: string,
    readonly manifest: CapabilityManifestV1,
    readonly executor: ChildExecutor,
  ) {}
  goaway(): void {
    this.connected = false;
  }
}

function newRequestId(): string {
  return `ch-${randomBytes(8).toString("hex")}`;
}

interface Pending {
  resolve(reply: ChildReply): void;
  reject(error: Error): void;
  timer: ReturnType<typeof setTimeout>;
}

/**
 * Gateway-side executor over one child connection. `send()` pairs
 * requestId→reply with a FIFO (one in-flight command per connection), a
 * reply timeout rejects as transport-dead (the caller applies the binding
 * law), and inbound frames arriving under a superseded `child_generation`
 * are dropped — the stale-child fence.
 */
export class ChildLink implements FleetMember, ChildExecutor {
  readonly childId: string;
  readonly manifest: CapabilityManifestV1;
  readonly generation: number;
  readonly timeoutSeconds: number;

  private readonly pending = new Map<string, Pending>();
  private readonly eventQueue: EnvelopeEvent[] = [];
  private readonly eventLog: LinkLogger;
  private chain: Promise<unknown> = Promise.resolve();
  private publisher: ((env: EnvelopeEvent) => Promise<void>) | null = null;
  private _connected = true;
  private closed = false;

  constructor(
    private readonly wire: {
      childWorkspaceId: string;
      manifest: CapabilityManifestV1;
      generation: number;
      entry: ChildEntryConfig;
      write(frame: ChildWireFrame): void;
      logger: LinkLogger;
    },
  ) {
    this.childId = wire.childWorkspaceId;
    this.manifest = wire.manifest;
    this.generation = wire.generation;
    this.timeoutSeconds = wire.entry.timeoutSeconds;
    this.eventLog = wire.logger;
  }

  // FleetMember seam: a link IS its child's executor.
  readonly executor: ChildExecutor = this;

  get childWorkspaceId(): string {
    return this.wire.childWorkspaceId;
  }

  get entry(): ChildEntryConfig {
    return this.wire.entry;
  }

  get connected(): boolean {
    return this._connected && !this.closed;
  }

  /** Live events flow straight out (05 publisher law); pre-welcome queues. */
  setPublisher(publisher: (env: EnvelopeEvent) => Promise<void>): void {
    this.publisher = publisher;
  }

  async register(): Promise<ChildMessage & { kind: "register" }> {
    return { kind: "register", childWorkspaceId: this.childId, manifest: this.manifest };
  }

  /**
   * One in-flight command per connection: sends serialize behind a promise
   * chain (FIFO). Transport-dead (disconnect/timeout) rejects with
   * ChildTransportError — the runtime maps it per the binding law.
   */
  send(msg: ChildMessage): Promise<ChildReply> {
    if (this.closed || !this._connected) {
      return Promise.reject(new ChildTransportError("child-disconnected"));
    }
    if (msg.kind === "register") {
      return Promise.resolve({ kind: "accepted", executionId: "" });
    }
    const send = this.chain.then(
      () =>
        new Promise<ChildReply>((resolve, reject) => {
          if (!this._connected || this.closed) {
            reject(new ChildTransportError("child-disconnected"));
            return;
          }
          const requestId = newRequestId();
          const timer = setTimeout(() => {
            this.pending.delete(requestId);
            reject(new ChildTransportError("child-timeout"));
          }, this.timeoutSeconds * 1000);
          timer.unref?.();
          this.pending.set(requestId, { resolve, reject, timer });
          const frame: CommandFrame = { v: 1, type: "command", requestId, message: msg };
          try {
            this.wire.write(frame);
          } catch (error) {
            clearTimeout(timer);
            this.pending.delete(requestId);
            reject(
              new ChildTransportError(error instanceof Error ? error.message : "write-failed"),
            );
          }
        }),
    );
    // The chain never rejects onward: each sender owns its own failure.
    this.chain = send.catch(() => undefined);
    return send;
  }

  drainEvents(): EnvelopeEvent[] {
    return this.eventQueue.splice(0, this.eventQueue.length);
  }

  /** Route an inbound reply/event frame (post-admission) into this link. */
  onFrame(frame: ReplyFrame | EventFrame): void {
    if (frame.type === "reply") {
      const waiting = this.pending.get(frame.requestId);
      if (waiting === undefined) {
        this.eventLog.warn("orphan-child-reply-dropped", {
          childId: this.childId,
          requestId: frame.requestId,
        });
        return;
      }
      clearTimeout(waiting.timer);
      this.pending.delete(frame.requestId);
      waiting.resolve(frame.reply);
      return;
    }
    if (this.publisher !== null) {
      const publish = this.publisher;
      void publish(frame.envelope).catch((error: unknown) => {
        this.eventLog.warn("child-event-forward-failed", {
          childId: this.childId,
          reason: error instanceof Error ? error.message : String(error),
        });
      });
    } else {
      this.eventQueue.push(frame.envelope);
    }
  }

  /** Socket died: every waiter fails honestly; the fleet marks the member offline. */
  notifyDisconnected(): void {
    this._connected = false;
    for (const [requestId, waiting] of this.pending) {
      clearTimeout(waiting.timer);
      this.pending.delete(requestId);
      waiting.reject(new ChildTransportError("child-disconnected"));
    }
  }

  goaway(reason: GoawayReason): void {
    try {
      this.wire.write({ v: 1, type: "goaway", reason });
    } catch {
      /* best-effort notice (locked) */
    }
    this._connected = false;
  }

  async close(): Promise<void> {
    this.notifyDisconnected();
    this.closed = true;
  }
}

interface OfflineRecord {
  /** Undefined when the persisted manifest bytes were unparseable. */
  manifest: CapabilityManifestV1 | undefined;
  manifestDigest: string;
  generation: number;
}

export interface ChildStatusRow {
  childWorkspaceId: string;
  state: ChildConnectionState;
  childGeneration: number;
  manifestDigest: string;
  inFlight: number;
}

/**
 * The live fleet + its durable shadow: `child_state` rows seed the offline
 * side at boot, registrations move children online, disconnects move them
 * back offline (routes to an offline child answer honestly
 * `child-disconnected`: children can disconnect or be replaced).
 */
export class ChildFleet {
  /** Per-child config (quota/timeout/token), keyed by childWorkspaceId. */
  readonly entries = new Map<string, ChildEntryConfig>();
  private readonly online = new Map<string, FleetMember>();
  private readonly offline = new Map<string, OfflineRecord>();
  /** capabilityId → childId over ONLINE members (the routing + POST union). */
  private readonly onlineCaps = new Map<string, string>();
  /** `${id}|${version}` → {childId, json}: the deny-ambiguity conflict gate. */
  private readonly descriptors = new Map<string, { childId: string; json: string }>();
  /** capabilityId → childId over offline members (honest rejections only). */
  private readonly offlineCaps = new Map<string, string>();
  /** childId → pending `waitUntilOnline` resolvers (boot-race hold). */
  private readonly onlineWaiters = new Map<string, Set<(online: boolean) => void>>();

  constructor(private readonly logger: LinkLogger) {}

  /** Boot seeding: persisted generations + honest offline routes. */
  seed(rows: ChildStateRow[]): void {
    for (const row of rows) {
      let manifest: CapabilityManifestV1 | undefined;
      try {
        manifest = JSON.parse(row.manifestJson) as CapabilityManifestV1;
      } catch {
        this.logger.warn("child-state-manifest-unparseable", { childId: row.childId });
      }
      this.offline.set(row.childId, {
        manifest,
        manifestDigest: row.manifestDigest,
        generation: row.childGeneration,
      });
      if (manifest !== undefined) {
        for (const capability of manifest.capabilities) {
          this.offlineCaps.set(capability.id, row.childId);
        }
      }
    }
  }

  /**
   * Deny-ambiguity gate: a (id, version) already held by a DIFFERENT member
   * with non-identical descriptor JSON refuses the newcomer (first-come);
   * byte-identical duplicates are fine (they dedupe in the union).
   */
  conflicts(manifest: CapabilityManifestV1): boolean {
    for (const capability of manifest.capabilities) {
      const key = `${capability.id}|${capability.version}`;
      const held = this.descriptors.get(key);
      if (held !== undefined && held.json !== JSON.stringify(capability)) return true;
    }
    return false;
  }

  /** Bring a member online (embedded construction or socket admission). */
  register(member: FleetMember): void {
    const existing = this.online.get(member.childId) ?? this.offline.get(member.childId);
    if (existing !== undefined && existing !== null && this.online.has(member.childId)) {
      this.release(member.childId); // replace-in-place (same id re-register)
    }
    this.offline.delete(member.childId);
    this.online.set(member.childId, member);
    for (const capability of member.manifest.capabilities) {
      const key = `${capability.id}|${capability.version}`;
      const json = JSON.stringify(capability);
      const held = this.descriptors.get(key);
      if (held === undefined || held.childId === member.childId || held.json === json) {
        this.descriptors.set(key, { childId: member.childId, json });
        this.onlineCaps.set(capability.id, member.childId);
      }
    }
    this.fireOnlineWaiters(member.childId);
  }

  /**
   * Boot-race hold (runtime assign path): resolve `true` the moment
   * `childId` registers, `false` after `graceMs` — an offline route waits
   * briefly for a child that is mid-boot rather than answering early with a
   * rejection a running child would never have produced.
   */
  waitUntilOnline(childId: string, graceMs: number): Promise<boolean> {
    if (this.online.has(childId)) return Promise.resolve(true);
    return new Promise((resolve) => {
      let settled = false;
      const settle = (online: boolean): void => {
        if (settled) return;
        settled = true;
        clearTimeout(timer);
        this.onlineWaiters.get(childId)?.delete(settle);
        resolve(online);
      };
      const timer = setTimeout(() => settle(false), graceMs);
      timer.unref?.();
      const waiters = this.onlineWaiters.get(childId) ?? new Set();
      waiters.add(settle);
      this.onlineWaiters.set(childId, waiters);
    });
  }

  private fireOnlineWaiters(childId: string): void {
    const waiters = this.onlineWaiters.get(childId);
    if (waiters === undefined || waiters.size === 0) return;
    this.onlineWaiters.delete(childId);
    for (const settle of waiters) settle(true);
  }

  /** Socket died / child closed: capabilities leave the union, honest routes stay. */
  markOffline(childId: string): void {
    const member = this.online.get(childId);
    if (member === undefined) return;
    this.online.delete(childId);
    this.release(childId);
    this.offline.set(childId, {
      manifest: member.manifest,
      manifestDigest: digestOf(member.manifest),
      generation: member.generation,
    });
    for (const capability of member.manifest.capabilities) {
      this.offlineCaps.set(capability.id, childId);
      this.onlineCaps.delete(capability.id);
      const key = `${capability.id}|${capability.version}`;
      if (this.descriptors.get(key)?.childId === childId) this.descriptors.delete(key);
    }
  }

  private release(childId: string): void {
    for (const [key, held] of this.descriptors) {
      if (held.childId === childId) this.descriptors.delete(key);
    }
    for (const [capId, owner] of this.onlineCaps) {
      if (owner === childId) this.onlineCaps.delete(capId);
    }
  }

  /**
   * Routing law: an ONLINE advertiser wins; an offline advertiser
   * stays routable so the answer is the honest `child-disconnected`, never a
   * silent re-route.
   */
  route(capabilityId: string): { childId: string; online: boolean } | undefined {
    const online = this.onlineCaps.get(capabilityId);
    if (online !== undefined) return { childId: online, online: true };
    const offline = this.offlineCaps.get(capabilityId);
    return offline === undefined ? undefined : { childId: offline, online: false };
  }

  /** Advertised descriptor for policy (online first, offline honest). */
  descriptorOf(capabilityId: string): CapabilityDescriptor | undefined {
    const online = this.onlineCaps.get(capabilityId);
    if (online !== undefined) {
      return this.online.get(online)?.manifest.capabilities.find((c) => c.id === capabilityId);
    }
    const offline = this.offlineCaps.get(capabilityId);
    const record = offline === undefined ? undefined : this.offline.get(offline);
    return record?.manifest?.capabilities.find((c) => c.id === capabilityId);
  }

  member(childId: string): FleetMember | undefined {
    return this.online.get(childId);
  }

  get size(): number {
    return this.online.size;
  }

  members(): FleetMember[] {
    return [...this.online.values()];
  }

  generationOf(childId: string): number {
    return this.online.get(childId)?.generation ?? this.offline.get(childId)?.generation ?? 0;
  }

  /** The child's own ownerTeam never travels: the Gateway's team owns the union. */
  unionManifest(team: string): CapabilityManifestV1 {
    const capabilities: CapabilityDescriptor[] = [];
    const seen = new Set<string>();
    for (const member of this.online.values()) {
      for (const capability of member.manifest.capabilities) {
        const key = `${capability.id}|${capability.version}`;
        if (seen.has(key)) continue;
        seen.add(key);
        capabilities.push(capability);
      }
    }
    return {
      apiVersion: "facilitator.framework/v1",
      kind: "CapabilityManifest",
      ownerTeam: team,
      // A zero-child union is the boot placeholder: never POSTed (schema
      // demands >=1 capability); the POST site guards on emptiness.
      capabilities: capabilities as CapabilityManifestV1["capabilities"],
    };
  }

  /** /status fleet rows: live members + durable offline rows, quota-aware. */
  statusRows(runningByChild: Map<string, number>): ChildStatusRow[] {
    const rows = new Map<string, ChildStatusRow>();
    for (const [childId, record] of this.offline) {
      rows.set(childId, {
        childWorkspaceId: childId,
        state: "offline",
        childGeneration: record.generation,
        manifestDigest: record.manifestDigest,
        inFlight: runningByChild.get(childId) ?? 0,
      });
    }
    for (const [childId, member] of this.online) {
      rows.set(childId, {
        childWorkspaceId: childId,
        state: "connected",
        childGeneration: member.generation,
        manifestDigest: digestOf(member.manifest),
        inFlight: runningByChild.get(childId) ?? 0,
      });
    }
    return [...rows.values()].sort((a, b) => a.childWorkspaceId.localeCompare(b.childWorkspaceId));
  }

  configuredEntry(childId: string): ChildEntryConfig | undefined {
    return this.entries.get(childId);
  }
}

/** `sha256:<hex>` over the exact JSON bytes of the child's own manifest —
 * the SAME helper `postCapabilities` digests POSTed bytes with, so a fleet
 * row's digest and a hello digest are comparable quantities. */
export const digestOf: (manifest: CapabilityManifestV1) => string = (manifest) =>
  digestOfManifest(JSON.stringify(manifest));
