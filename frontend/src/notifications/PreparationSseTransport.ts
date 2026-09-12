export interface PreparationDestinationChanged {
  destinationId: string;
}

interface EventSourceConnection {
  close(): void;
  onopen: ((event: Event) => void) | null;
  onerror: ((event: Event) => void) | null;
  addEventListener(
    type: "invalidation",
    listener: (event: MessageEvent<string>) => void,
  ): void;
}

export type EventSourceFactory = (url: string) => EventSourceConnection;

interface PreparationSseTransportOptions {
  eventSourceFactory?: EventSourceFactory;
  setTimeout?: (callback: () => void, delayMs: number) => ReturnType<typeof setTimeout>;
  clearTimeout?: (timer: ReturnType<typeof setTimeout>) => void;
  random?: () => number;
}

const streamPath = "/api/notifications/stream";
const initialReconnectDelayMs = 1_000;
const maximumReconnectDelayMs = 30_000;
const reconnectJitterRatio = 0.2;
const uuidPattern =
  /^[0-9a-f]{8}-[0-9a-f]{4}-[1-8][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i;

function createBrowserEventSource(url: string): EventSourceConnection {
  return new EventSource(url) as unknown as EventSourceConnection;
}

function isPreparationDestinationChanged(
  value: unknown,
): value is { kind: "preparation.destination.changed"; scopeId: string } {
  if (value === null || typeof value !== "object") return false;
  const payload = value as Record<string, unknown>;
  return (
    payload.kind === "preparation.destination.changed" &&
    typeof payload.scopeId === "string" &&
    uuidPattern.test(payload.scopeId)
  );
}

/** App-owned, non-authoritative notification transport. */
export class PreparationSseTransport {
  private readonly listeners = new Map<
    string,
    Set<(notification: PreparationDestinationChanged) => void>
  >();
  private readonly connectedListeners = new Set<(generation: number) => void>();
  private readonly eventSourceFactory: EventSourceFactory;
  private readonly scheduleTimeout: (
    callback: () => void,
    delayMs: number,
  ) => ReturnType<typeof setTimeout>;
  private readonly cancelTimeout: (timer: ReturnType<typeof setTimeout>) => void;
  private readonly random: () => number;
  private source: EventSourceConnection | null = null;
  private reconnectTimer: ReturnType<typeof setTimeout> | null = null;
  private activeIdentityId: string | null = null;
  private lifecycleToken = 0;
  private reconnectAttempt = 0;
  private connectedGeneration = 0;

  constructor(options: PreparationSseTransportOptions = {}) {
    this.eventSourceFactory = options.eventSourceFactory ?? createBrowserEventSource;
    this.scheduleTimeout = options.setTimeout ?? globalThis.setTimeout;
    this.cancelTimeout = options.clearTimeout ?? globalThis.clearTimeout;
    this.random = options.random ?? Math.random;
  }

  get connectionGeneration(): number {
    return this.connectedGeneration;
  }

  setActiveIdentity(identityId: string | null): void {
    if (this.activeIdentityId === identityId) return;
    this.activeIdentityId = identityId;
    this.replaceConnection();
  }

  subscribe(
    destinationId: string,
    listener: (notification: PreparationDestinationChanged) => void,
  ): () => void {
    let destinationListeners = this.listeners.get(destinationId);
    const snapshotChanged = destinationListeners === undefined;
    if (destinationListeners === undefined) {
      destinationListeners = new Set();
      this.listeners.set(destinationId, destinationListeners);
    }
    destinationListeners.add(listener);
    if (snapshotChanged) this.replaceConnection();

    let subscribed = true;
    return () => {
      if (!subscribed) return;
      subscribed = false;
      const currentListeners = this.listeners.get(destinationId);
      if (currentListeners === undefined) return;
      currentListeners.delete(listener);
      if (currentListeners.size !== 0) return;
      this.listeners.delete(destinationId);
      this.replaceConnection();
    };
  }

  onConnected(listener: (generation: number) => void): () => void {
    this.connectedListeners.add(listener);
    return () => this.connectedListeners.delete(listener);
  }

  dispose(): void {
    this.activeIdentityId = null;
    this.listeners.clear();
    this.closeCurrentConnection();
    this.connectedListeners.clear();
  }

  private replaceConnection(): void {
    this.closeCurrentConnection();
    if (this.activeIdentityId !== null && this.listeners.size !== 0) {
      this.openConnection(this.lifecycleToken);
    }
  }

  private closeCurrentConnection(): void {
    this.lifecycleToken += 1;
    if (this.reconnectTimer !== null) {
      this.cancelTimeout(this.reconnectTimer);
      this.reconnectTimer = null;
    }
    if (this.source !== null) {
      this.source.close();
      this.source = null;
    }
    this.reconnectAttempt = 0;
  }

  private openConnection(token: number): void {
    if (!this.isCurrent(token)) return;
    const source = this.eventSourceFactory(this.createUrl());
    let opened = false;
    this.source = source;
    source.addEventListener("invalidation", (event) => {
      if (!this.isCurrentSource(token, source)) return;
      this.deliverInvalidation(event.data);
    });
    source.onopen = () => {
      if (opened || !this.isCurrentSource(token, source)) return;
      opened = true;
      this.reconnectAttempt = 0;
      this.connectedGeneration += 1;
      for (const listener of this.connectedListeners) {
        listener(this.connectedGeneration);
      }
    };
    source.onerror = () => {
      if (!this.isCurrentSource(token, source)) return;
      source.close();
      this.source = null;
      this.scheduleReconnect(token);
    };
  }

  private scheduleReconnect(token: number): void {
    if (!this.isCurrent(token) || this.reconnectTimer !== null) return;
    const exponentialDelay = Math.min(
      initialReconnectDelayMs * 2 ** this.reconnectAttempt,
      maximumReconnectDelayMs,
    );
    this.reconnectAttempt += 1;
    const jitter = 1 - reconnectJitterRatio + this.random() * reconnectJitterRatio * 2;
    this.reconnectTimer = this.scheduleTimeout(() => {
      this.reconnectTimer = null;
      if (this.isCurrent(token)) this.openConnection(token);
    }, Math.round(exponentialDelay * jitter));
  }

  private isCurrent(token: number): boolean {
    return (
      token === this.lifecycleToken &&
      this.activeIdentityId !== null &&
      this.listeners.size !== 0
    );
  }

  private isCurrentSource(token: number, source: EventSourceConnection): boolean {
    return this.isCurrent(token) && this.source === source;
  }

  private createUrl(): string {
    const query = new URLSearchParams();
    for (const destinationId of [...this.listeners.keys()].sort()) {
      query.append("scope", `preparation.destination:${destinationId}`);
    }
    return `${streamPath}?${query.toString()}`;
  }

  private deliverInvalidation(data: string): void {
    let payload: unknown;
    try {
      payload = JSON.parse(data);
    } catch {
      return;
    }
    if (!isPreparationDestinationChanged(payload)) return;
    const destinationListeners = this.listeners.get(payload.scopeId);
    if (destinationListeners === undefined) return;
    const notification: PreparationDestinationChanged = {
      destinationId: payload.scopeId,
    };
    for (const listener of destinationListeners) {
      listener(notification);
    }
  }
}
