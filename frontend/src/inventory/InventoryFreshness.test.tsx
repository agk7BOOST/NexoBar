import { act, cleanup, render, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { NotificationSseProvider } from "../notifications/NotificationSseProvider.tsx";
import { InventoryPanel } from "./InventoryPanel.tsx";
import {
  InventoryProblemError,
  listInventoryConfigurationItems,
  listInventoryOperationalItems,
  type InventoryOperationalItem,
} from "./inventoryClient.ts";

vi.mock("./inventoryClient.ts", async (original) => ({
  ...await original<typeof import("./inventoryClient.ts")>(),
  listInventoryConfigurationItems: vi.fn(),
  listInventoryOperationalItems: vi.fn(),
}));

const item: InventoryOperationalItem = {
  itemId: "item-1",
  operationalName: "Harina",
  operationalUnit: "kg",
  currentRegisteredQuantity: "10",
  quantityEstablished: true,
  hasNegativeBalanceInconsistency: false,
  asOfMovementRevision: 1,
};

class Stream {
  static sources: Stream[] = [];
  readonly url: string;
  closed = false;
  onopen: (() => void) | null = null;
  onerror: (() => void) | null = null;
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

  invalidate(payload: unknown) {
    this.listener?.(new MessageEvent("invalidation", { data: JSON.stringify(payload) }));
  }
}

function tree(identity = "identity-a", onUnauthorized = vi.fn()) {
  return (
    <NotificationSseProvider key={identity} identityId={identity}>
      <InventoryPanel onUnauthorized={onUnauthorized} />
    </NotificationSseProvider>
  );
}

function deferred<T>() {
  let resolve!: (value: T) => void;
  let reject!: (reason: unknown) => void;
  const promise = new Promise<T>((res, rej) => {
    resolve = res;
    reject = rej;
  });
  return { promise, resolve, reject };
}

async function loaded() {
  const view = render(tree());
  await screen.findByText("Harina", { selector: "h4" });
  await waitFor(() => expect(Stream.sources).toHaveLength(1));
  return view;
}

beforeEach(() => {
  vi.clearAllMocks();
  vi.mocked(listInventoryConfigurationItems).mockResolvedValue([]);
  vi.mocked(listInventoryOperationalItems).mockReset().mockResolvedValue([item]);
  Stream.sources = [];
  vi.stubGlobal("EventSource", Stream);
});

afterEach(() => {
  cleanup();
  vi.useRealTimers();
  vi.unstubAllGlobals();
});

describe("Inventory operational SSE freshness", () => {
  it("subscribes only after the operational read is authorized and removes the static scope on unmount", async () => {
    const view = await loaded();
    expect(new URL(Stream.sources[0].url, "http://localhost").searchParams.getAll("scope"))
      .toEqual(["inventory.operation"]);
    view.unmount();
    expect(Stream.sources[0].closed).toBe(true);
  });

  it("does not subscribe from the configuration-only surface", async () => {
    vi.mocked(listInventoryOperationalItems).mockRejectedValueOnce(
      new InventoryProblemError(403),
    );
    render(tree());
    await screen.findByRole("button", { name: "Crear elemento" });
    await screen.findByRole("alert");
    expect(Stream.sources).toHaveLength(0);
  });

  it("reconciles authoritatively on open, Inventory invalidation, and reconnect but ignores unrelated events", async () => {
    await loaded();
    await act(async () => Stream.sources[0].onopen?.());
    expect(listInventoryOperationalItems).toHaveBeenCalledTimes(2);

    vi.mocked(listInventoryOperationalItems).mockResolvedValueOnce([
      { ...item, currentRegisteredQuantity: "15", asOfMovementRevision: 2 },
    ]);
    await act(async () => Stream.sources[0].invalidate({ kind: "inventory.operation.changed" }));
    await screen.findByText("15 kg");

    await act(async () => Stream.sources[0].invalidate({
      kind: "preparation.destination.changed",
      scopeId: "11111111-1111-4111-8111-111111111111",
    }));
    await act(async () => Stream.sources[0].invalidate({
      kind: "order.changed",
      scopeId: "22222222-2222-4222-8222-222222222222",
    }));
    expect(listInventoryOperationalItems).toHaveBeenCalledTimes(3);

    act(() => Stream.sources[0].onerror?.());
    await waitFor(() => expect(Stream.sources).toHaveLength(2), { timeout: 2_000 });
    await act(async () => Stream.sources[1].onopen?.());
    expect(listInventoryOperationalItems).toHaveBeenCalledTimes(4);
  });

  it("fences obsolete responses and errors, coalescing repeated invalidations into one follow-up", async () => {
    await loaded();
    expect(listInventoryOperationalItems).toHaveBeenCalledTimes(1);
    const old = deferred<InventoryOperationalItem[]>();
    const current = deferred<InventoryOperationalItem[]>();
    vi.mocked(listInventoryOperationalItems)
      .mockReturnValueOnce(old.promise)
      .mockReturnValueOnce(current.promise);

    await act(async () => Stream.sources[0].invalidate({ kind: "inventory.operation.changed" }));
    await act(async () => {
      Stream.sources[0].invalidate({ kind: "inventory.operation.changed" });
      Stream.sources[0].invalidate({ kind: "inventory.operation.changed" });
    });
    expect(listInventoryOperationalItems).toHaveBeenCalledTimes(2);
    await act(async () => old.resolve([{ ...item, operationalName: "Obsoleta" }]));
    expect(screen.queryByText("Obsoleta", { selector: "h4" })).not.toBeInTheDocument();
    expect(listInventoryOperationalItems).toHaveBeenCalledTimes(3);
    await act(async () => current.resolve([{ ...item, operationalName: "Vigente" }]));
    expect(await screen.findByText("Vigente", { selector: "h4" })).toBeVisible();

    const obsoleteError = deferred<InventoryOperationalItem[]>();
    vi.mocked(listInventoryOperationalItems)
      .mockReturnValueOnce(obsoleteError.promise)
      .mockResolvedValueOnce([{ ...item, operationalName: "Recuperada" }]);
    await act(async () => Stream.sources[0].invalidate({ kind: "inventory.operation.changed" }));
    await act(async () => Stream.sources[0].invalidate({ kind: "inventory.operation.changed" }));
    await act(async () => obsoleteError.reject(new InventoryProblemError(500)));
    expect(await screen.findByText("Recuperada", { selector: "h4" })).toBeVisible();
    expect(screen.queryByText(/No se pudo cargar el estado/i)).not.toBeInTheDocument();
  });

  it("renders negative authoritative State and fences an old Identity response", async () => {
    const old = deferred<InventoryOperationalItem[]>();
    vi.mocked(listInventoryOperationalItems).mockReturnValueOnce(old.promise);
    const view = render(tree("identity-a"));
    await waitFor(() => expect(listInventoryOperationalItems).toHaveBeenCalledTimes(1));
    vi.mocked(listInventoryOperationalItems).mockResolvedValueOnce([{
      ...item,
      currentRegisteredQuantity: "-5",
      hasNegativeBalanceInconsistency: true,
      asOfMovementRevision: 2,
    }]);
    view.rerender(tree("identity-b"));
    expect(await screen.findByText("-5 kg")).toBeVisible();
    expect(screen.getByText("Inconsistencia de saldo")).toBeVisible();
    await act(async () => old.resolve([{ ...item, operationalName: "Identity anterior" }]));
    expect(screen.queryByText("Identity anterior", { selector: "h4" })).not.toBeInTheDocument();
  });
});
