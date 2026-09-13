import { afterEach, describe, expect, it, vi } from "vitest";
import { PreparationSseTransport } from "./PreparationSseTransport.ts";

const destinationA = "11111111-1111-4111-8111-111111111111";
const destinationB = "22222222-2222-4222-8222-222222222222";
const orderA = "33333333-3333-4333-8333-333333333333";
const orderB = "44444444-4444-4444-8444-444444444444";

class FakeEventSource {
  closed = false;
  onopen: ((event: Event) => void) | null = null;
  onerror: ((event: Event) => void) | null = null;
  private invalidationListener: ((event: MessageEvent<string>) => void) | null = null;

  close(): void {
    this.closed = true;
  }

  addEventListener(
    type: "invalidation",
    listener: (event: MessageEvent<string>) => void,
  ): void {
    if (type === "invalidation") this.invalidationListener = listener;
  }

  open(): void {
    this.onopen?.(new Event("open"));
  }

  error(): void {
    this.onerror?.(new Event("error"));
  }

  invalidate(payload: string): void {
    this.invalidationListener?.(new MessageEvent("invalidation", { data: payload }));
  }
}

function createTransport() {
  const sources: FakeEventSource[] = [];
  const urls: string[] = [];
  const transport = new PreparationSseTransport({
    eventSourceFactory: (url) => {
      urls.push(url);
      const source = new FakeEventSource();
      sources.push(source);
      return source;
    },
    random: () => 0.5,
  });
  transport.setActiveIdentity("identity-a");
  return { transport, sources, urls };
}

afterEach(() => vi.useRealTimers());

describe("PreparationSseTransport", () => {
  it("keeps no stream without scopes and one stream for its deduplicated snapshot", () => {
    const { transport, sources, urls } = createTransport();
    expect(sources).toHaveLength(0);

    const unsubscribeA = transport.subscribe(destinationA, vi.fn());
    const unsubscribeADuplicate = transport.subscribe(destinationA, vi.fn());
    expect(sources).toHaveLength(1);
    expect(urls[0]).toBe(
      `/api/notifications/stream?scope=preparation.destination%3A${destinationA}`,
    );
    expect(urls[0]).not.toContain("identity-a");
    expect(urls[0]).not.toMatch(/token|credential|session/i);

    unsubscribeADuplicate();
    expect(sources[0].closed).toBe(false);
    unsubscribeA();
    expect(sources[0].closed).toBe(true);
  });

  it("replaces the one stream when its distinct scope snapshot changes deterministically", () => {
    const { transport, sources, urls } = createTransport();
    const unsubscribeB = transport.subscribe(destinationB, vi.fn());
    transport.subscribe(destinationA, vi.fn());

    expect(sources).toHaveLength(2);
    expect(sources[0].closed).toBe(true);
    expect(urls[1]).toBe(
      `/api/notifications/stream?scope=preparation.destination%3A${destinationA}&scope=preparation.destination%3A${destinationB}`,
    );
    unsubscribeB();
    expect(sources).toHaveLength(3);
    expect(sources[1].closed).toBe(true);
  });

  it("uses one mixed-scope stream and delivers order.changed only to its exact Order", () => {
    const { transport, sources, urls } = createTransport();
    const preparationChanged = vi.fn();
    const orderChanged = vi.fn();
    transport.subscribe(destinationA, preparationChanged);
    transport.subscribeOrder(orderA, orderChanged);

    expect(sources).toHaveLength(2);
    expect(urls[1]).toBe(
      `/api/notifications/stream?scope=preparation.destination%3A${destinationA}&scope=order.active%3A${orderA}`,
    );
    sources[1].invalidate(JSON.stringify({ kind: "order.changed", scopeId: orderB }));
    sources[1].invalidate(JSON.stringify({ kind: "order.changed", scopeId: orderA }));

    expect(orderChanged).toHaveBeenCalledTimes(1);
    expect(orderChanged).toHaveBeenLastCalledWith({ orderId: orderA });
    expect(preparationChanged).not.toHaveBeenCalled();
  });

  it("delivers only valid invalidations to subscribers for that destination", () => {
    const { transport, sources } = createTransport();
    const changedA = vi.fn();
    const changedB = vi.fn();
    transport.subscribe(destinationA, changedA);
    transport.subscribe(destinationB, changedB);
    const current = sources[1];

    current.invalidate(JSON.stringify({ kind: "preparation.destination.changed", scopeId: destinationA }));
    current.invalidate(JSON.stringify({ kind: "preparation.destination.changed", scopeId: destinationB }));
    current.invalidate("not json");
    current.invalidate(JSON.stringify({ kind: "order.changed", scopeId: destinationA }));
    current.invalidate(JSON.stringify({ kind: "preparation.destination.changed", scopeId: "not-a-uuid" }));

    expect(changedA).toHaveBeenCalledTimes(1);
    expect(changedA).toHaveBeenLastCalledWith({ destinationId: destinationA });
    expect(changedB).toHaveBeenCalledTimes(1);
  });

  it("ignores messages from a superseded EventSource", () => {
    const { transport, sources } = createTransport();
    const changed = vi.fn();
    transport.subscribe(destinationA, changed);
    const oldSource = sources[0];
    transport.subscribe(destinationB, vi.fn());

    oldSource.invalidate(JSON.stringify({ kind: "preparation.destination.changed", scopeId: destinationA }));
    expect(changed).not.toHaveBeenCalled();
  });

  it("signals each successful current open, including reconnects", async () => {
    vi.useFakeTimers();
    const { transport, sources } = createTransport();
    const connected = vi.fn();
    transport.onConnected(connected);
    transport.subscribe(destinationA, vi.fn());
    sources[0].open();
    sources[0].error();
    await vi.advanceTimersByTimeAsync(1_000);
    sources[1].open();
    sources[0].open();

    expect(connected).toHaveBeenNthCalledWith(1, 1);
    expect(connected).toHaveBeenNthCalledWith(2, 2);
    expect(connected).toHaveBeenCalledTimes(2);
  });

  it("uses one bounded reconnect timer, resets after open, and fences stale timers", async () => {
    vi.useFakeTimers();
    const { transport, sources } = createTransport();
    transport.subscribe(destinationA, vi.fn());
    sources[0].error();
    sources[0].error();
    await vi.advanceTimersByTimeAsync(999);
    expect(sources).toHaveLength(1);
    await vi.advanceTimersByTimeAsync(1);
    expect(sources).toHaveLength(2);

    sources[1].error();
    transport.subscribe(destinationB, vi.fn());
    expect(sources).toHaveLength(3);
    await vi.advanceTimersByTimeAsync(30_000);
    expect(sources).toHaveLength(3);

    sources[2].open();
    sources[2].error();
    await vi.advanceTimersByTimeAsync(1_000);
    expect(sources).toHaveLength(4);
  });

  it("closes old authority on session removal or replacement and opens a fresh stream", () => {
    const { transport, sources } = createTransport();
    transport.subscribe(destinationA, vi.fn());
    transport.setActiveIdentity(null);
    expect(sources[0].closed).toBe(true);

    transport.setActiveIdentity("identity-b");
    expect(sources).toHaveLength(2);
    transport.setActiveIdentity("identity-c");
    expect(sources[1].closed).toBe(true);
    expect(sources).toHaveLength(3);
  });

  it("cancels reconnect work when disposed", async () => {
    vi.useFakeTimers();
    const { transport, sources } = createTransport();
    transport.subscribe(destinationA, vi.fn());
    sources[0].error();
    transport.dispose();
    await vi.advanceTimersByTimeAsync(30_000);
    expect(sources).toHaveLength(1);
  });
});
