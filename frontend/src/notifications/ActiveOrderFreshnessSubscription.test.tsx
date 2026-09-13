import { act, cleanup, render, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { PreparationReadCoordinator } from "../preparation/PreparationReadCoordinator.ts";
import { ActiveOrderFreshnessSubscription } from "./ActiveOrderFreshnessSubscription.tsx";
import { PreparationSseProvider } from "./PreparationSseProvider.tsx";

const orderA = "33333333-3333-4333-8333-333333333333";
const orderB = "44444444-4444-4444-8444-444444444444";

class Stream {
  static sources: Stream[] = [];
  readonly url: string;
  closed = false;
  onopen: ((event: Event) => void) | null = null;
  onerror: ((event: Event) => void) | null = null;
  listener?: (event: MessageEvent<string>) => void;

  constructor(url: string) {
    this.url = url;
    Stream.sources.push(this);
  }

  close() {
    this.closed = true;
  }

  addEventListener(type: string, listener: (event: MessageEvent<string>) => void) {
    if (type === "invalidation") this.listener = listener;
  }

  invalidate(orderId: string) {
    this.listener?.(new MessageEvent("invalidation", {
      data: JSON.stringify({ kind: "order.changed", scopeId: orderId }),
    }));
  }
}

function Probe({ orderId, invalidate }: { orderId: string | null; invalidate: () => void }) {
  return <ActiveOrderFreshnessSubscription orderId={orderId} invalidate={invalidate} />;
}

function tree(orderId: string | null, invalidate = vi.fn()) {
  return <PreparationSseProvider identityId="identity-a">
    <Probe orderId={orderId} invalidate={invalidate} />
  </PreparationSseProvider>;
}

function deferred<T>() {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>((done) => { resolve = done; });
  return { promise, resolve };
}

beforeEach(() => {
  Stream.sources = [];
  vi.stubGlobal("EventSource", Stream);
});
afterEach(() => {
  cleanup();
  vi.useRealTimers();
  vi.unstubAllGlobals();
});

describe("active Order SSE freshness", () => {
  it("subscribes only while an active Order is mounted and replaces A with B", async () => {
    const invalidated = vi.fn();
    const view = render(tree(null, invalidated));
    expect(Stream.sources).toHaveLength(0);

    view.rerender(tree(orderA, invalidated));
    await waitFor(() => expect(Stream.sources).toHaveLength(1));
    expect(Stream.sources[0].url).toContain(`order.active%3A${orderA}`);
    Stream.sources[0].invalidate(orderB);
    expect(invalidated).not.toHaveBeenCalled();

    view.rerender(tree(orderB, invalidated));
    await waitFor(() => expect(Stream.sources).toHaveLength(2));
    expect(Stream.sources[0].closed).toBe(true);
    expect(Stream.sources[1].url).toContain(`order.active%3A${orderB}`);
    view.unmount();
    expect(Stream.sources[1].closed).toBe(true);
  });

  it("invalidates on the exact event and successful stream opens", async () => {
    const invalidated = vi.fn();
    render(tree(orderA, invalidated));
    await waitFor(() => expect(Stream.sources).toHaveLength(1));
    await act(async () => Stream.sources[0].onopen?.(new Event("open")));
    Stream.sources[0].invalidate(orderB);
    Stream.sources[0].invalidate(orderA);
    expect(invalidated).toHaveBeenCalledTimes(2);
  });

  it("fences an obsolete response and coalesces repeated invalidations", async () => {
    const coordinator = new PreparationReadCoordinator();
    const old = deferred<string>();
    const current = deferred<string>();
    const applied: string[] = [];
    coordinator.invalidate(async (isCurrent) => {
      const value = await old.promise;
      if (isCurrent()) applied.push(value);
    });
    coordinator.invalidate(async (isCurrent) => {
      const value = await current.promise;
      if (isCurrent()) applied.push(value);
    });
    coordinator.invalidate(async (isCurrent) => {
      const value = await current.promise;
      if (isCurrent()) applied.push(value);
    });

    await act(async () => old.resolve("obsolete"));
    expect(applied).toEqual([]);
    await act(async () => current.resolve("current"));
    expect(applied).toEqual(["current"]);
  });
});
