import { act, cleanup, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { PreparationSseProvider } from "../notifications/PreparationSseProvider.tsx";
import { getAntiforgeryToken, listPreparationDestinations, SessionProblemError } from "../identity/sessionClient.ts";
import { PreparationPanel } from "./PreparationPanel.tsx";
import { listPreparationWork, startPreparationQuantity, markPreparationQuantityReady, type PreparationWork, type PreparationCommandResult } from "./preparationClient.ts";

vi.mock("../identity/sessionClient.ts", async (original) => ({
  ...await original<typeof import("../identity/sessionClient.ts")>(),
  listPreparationDestinations: vi.fn(), getAntiforgeryToken: vi.fn(),
}));
vi.mock("./preparationClient.ts", async (original) => ({
  ...await original<typeof import("./preparationClient.ts")>(),
  listPreparationWork: vi.fn(), startPreparationQuantity: vi.fn(), markPreparationQuantityReady: vi.fn(),
}));

const destinationA = "11111111-1111-4111-8111-111111111111";
const destinationB = "22222222-2222-4222-8222-222222222222";
const destinations = [
  { preparationResponsibilityId: destinationA, operationalName: "Cocina" },
  { preparationResponsibilityId: destinationB, operationalName: "Barra" },
];
const work: PreparationWork = {
  workId: "work-1", preparationResponsibilityId: destinationA,
  operationalReference: "order-1", context: "Mesa 1", incorporationId: "inc-1",
  incorporationOrdinal: 1, contentOrdinal: 1, productId: "product-1",
  productOperationalName: "Papas", instruction: null, totalQuantity: 5,
  pendingQuantity: 2, inPreparationQuantity: 2, readyQuantity: 1,
  deliveredQuantity: 0, confirmedAt: "2026-09-12T12:00:00Z",
};

class Stream {
  static sources: Stream[] = [];
  readonly url: string;
  closed = false;
  onopen: (() => void) | null = null;
  onerror: (() => void) | null = null;
  listener?: (event: MessageEvent<string>) => void;
  constructor(url: string) { this.url = url; Stream.sources.push(this); }
  close() { this.closed = true; }
  addEventListener(type: string, listener: (event: MessageEvent<string>) => void) {
    if (type === "invalidation") this.listener = listener;
  }
  invalidate(destinationId = destinationA) {
    this.listener?.(new MessageEvent("invalidation", { data: JSON.stringify({
      kind: "preparation.destination.changed", scopeId: destinationId,
    }) }));
  }
}
function deferred<T>() {
  let resolve!: (value: T) => void;
  let reject!: (reason: unknown) => void;
  const promise = new Promise<T>((res, rej) => { resolve = res; reject = rej; });
  return { promise, resolve, reject };
}
function tree(identity = "identity-a", onUnauthorized = vi.fn()) {
  // App uses this same authority key to tear down the whole authenticated subtree.
  return <PreparationSseProvider key={identity} identityId={identity}>
    <PreparationPanel onUnauthorized={onUnauthorized} />
  </PreparationSseProvider>;
}
async function loaded() {
  const view = render(tree());
  await screen.findByText("Papas", { selector: "strong" });
  await waitFor(() => expect(Stream.sources).toHaveLength(1));
  return view;
}
const read = vi.mocked(listPreparationWork);

beforeEach(() => {
  vi.clearAllMocks();
  read.mockReset().mockResolvedValue([work]);
  vi.mocked(listPreparationDestinations).mockReset().mockResolvedValue([destinations[0]]);
  vi.mocked(getAntiforgeryToken).mockResolvedValue("csrf-exact");
  vi.mocked(startPreparationQuantity).mockReset();
  vi.mocked(markPreparationQuantityReady).mockReset();
  Stream.sources = [];
  vi.stubGlobal("EventSource", Stream);
});
afterEach(() => { cleanup(); vi.useRealTimers(); vi.unstubAllGlobals(); });

describe("Preparation SSE freshness", () => {
  it("subscribes only to the active destination and unsubscribes on unmount", async () => {
    const view = await loaded();
    expect(new URL(Stream.sources[0].url, "http://localhost").searchParams.getAll("scope"))
      .toEqual([`preparation.destination:${destinationA}`]);
    view.unmount();
    expect(Stream.sources[0].closed).toBe(true);
  });

  it("ignores another destination and applies only the authoritative refetch", async () => {
    await loaded();
    await act(async () => Stream.sources[0].invalidate(destinationB));
    expect(read).toHaveBeenCalledTimes(1);
    const pending = deferred<PreparationWork[]>();
    read.mockReturnValueOnce(pending.promise);
    await act(async () => Stream.sources[0].invalidate());
    expect(read).toHaveBeenCalledTimes(2);
    expect(screen.getByText("Papas", { selector: "strong" })).toBeVisible();
    await act(async () => pending.resolve([{ ...work, productOperationalName: "Autoritativo" }]));
    expect(screen.getByText("Autoritativo", { selector: "strong" })).toBeVisible();
  });

  it("coalesces duplicates and fences an in-flight response before exactly one follow-up", async () => {
    await loaded();
    const old = deferred<PreparationWork[]>();
    const latest = deferred<PreparationWork[]>();
    read.mockReturnValueOnce(old.promise).mockReturnValueOnce(latest.promise);
    await act(async () => Stream.sources[0].invalidate());
    await act(async () => {
      for (let i = 0; i < 20; i++) Stream.sources[0].invalidate();
    });
    expect(read).toHaveBeenCalledTimes(2);
    await act(async () => old.resolve([{ ...work, productOperationalName: "Obsoleto" }]));
    expect(screen.queryByText("Obsoleto")).not.toBeInTheDocument();
    expect(read).toHaveBeenCalledTimes(3);
    await act(async () => latest.resolve([{ ...work, productOperationalName: "Vigente" }]));
    expect(screen.getByText("Vigente")).toBeVisible();
    expect(read).toHaveBeenCalledTimes(3);
  });

  it("ignores an obsolete 401 and follows up successfully", async () => {
    const unauthorized = vi.fn();
    render(tree("identity-a", unauthorized));
    await screen.findByText("Papas", { selector: "strong" });
    const old = deferred<PreparationWork[]>();
    read.mockReturnValueOnce(old.promise).mockResolvedValueOnce([{ ...work, productOperationalName: "Vigente" }]);
    await act(async () => Stream.sources[0].invalidate());
    await act(async () => Stream.sources[0].invalidate());
    await act(async () => old.reject(new SessionProblemError(401, {})));
    expect(unauthorized).not.toHaveBeenCalled();
    expect(screen.queryByRole("alert")).not.toBeInTheDocument();
    expect(screen.getByText("Vigente")).toBeVisible();
  });

  it("keeps one further refresh when invalidations arrive during the follow-up", async () => {
    await loaded();
    const first = deferred<PreparationWork[]>();
    const followUp = deferred<PreparationWork[]>();
    read.mockReturnValueOnce(first.promise).mockReturnValueOnce(followUp.promise);
    await act(async () => Stream.sources[0].invalidate());
    await act(async () => Stream.sources[0].invalidate());
    await act(async () => first.resolve([work]));
    expect(read).toHaveBeenCalledTimes(3);
    await act(async () => {
      Stream.sources[0].invalidate(); Stream.sources[0].invalidate();
    });
    expect(read).toHaveBeenCalledTimes(3);
    await act(async () => followUp.resolve([work]));
    expect(read).toHaveBeenCalledTimes(4);
  });

  it("cancels queued refresh on unmount and ignores a late result", async () => {
    const view = await loaded();
    const pending = deferred<PreparationWork[]>();
    read.mockReturnValueOnce(pending.promise);
    await act(async () => Stream.sources[0].invalidate());
    await act(async () => Stream.sources[0].invalidate());
    view.unmount();
    await act(async () => pending.resolve([work]));
    expect(read).toHaveBeenCalledTimes(2);
    expect(Stream.sources[0].closed).toBe(true);
  });

  it("merges initial open with pending page load and refreshes after reconnect only for the current stream", async () => {
    const initial = deferred<PreparationWork[]>();
    const followUp = deferred<PreparationWork[]>();
    read.mockReturnValueOnce(initial.promise).mockReturnValueOnce(followUp.promise);
    render(tree());
    await waitFor(() => expect(Stream.sources).toHaveLength(1));
    await act(async () => Stream.sources[0].onopen?.());
    expect(read).toHaveBeenCalledTimes(1);
    await act(async () => initial.resolve([{ ...work, productOperationalName: "Old initial" }]));
    expect(read).toHaveBeenCalledTimes(2);
    expect(screen.queryByText("Old initial")).not.toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Actualizar preparación" })).toBeDisabled();
    expect(screen.getByText("Cargando trabajo…")).toBeVisible();
    await act(async () => followUp.resolve([{ ...work, productOperationalName: "Newest initial" }]));
    expect(screen.getByText("Newest initial", { selector: "strong" })).toBeVisible();
    expect(screen.getByRole("button", { name: "Actualizar preparación" })).toBeEnabled();
    expect(screen.queryByText("Cargando trabajo…")).not.toBeInTheDocument();
    expect(read).toHaveBeenCalledTimes(2);
    act(() => Stream.sources[0].onerror?.());
    await waitFor(() => expect(Stream.sources).toHaveLength(2), { timeout: 2000 });
    await act(async () => Stream.sources[1].onopen?.());
    expect(read).toHaveBeenCalledTimes(3);
    await act(async () => { Stream.sources[0].onopen?.(); Stream.sources[0].invalidate(); });
    expect(read).toHaveBeenCalledTimes(3);
  });

  it("fences the previous destination, replaces its subscription and reads the new destination", async () => {
    vi.mocked(listPreparationDestinations).mockResolvedValue(destinations);
    const user = userEvent.setup();
    render(tree());
    await user.selectOptions(await screen.findByRole("combobox"), destinationA);
    await screen.findByText("Papas", { selector: "strong" });
    const old = deferred<PreparationWork[]>();
    read.mockReturnValueOnce(old.promise).mockResolvedValueOnce([{
      ...work, preparationResponsibilityId: destinationB, productOperationalName: "Barra vigente",
    }]);
    await act(async () => Stream.sources[0].invalidate());
    await user.selectOptions(screen.getByRole("combobox"), destinationB);
    expect(Stream.sources[0].closed).toBe(true);
    expect(Stream.sources[1].url).toContain(destinationB);
    await act(async () => { Stream.sources[0].invalidate(); old.resolve([work]); });
    await screen.findByText("Barra vigente");
    expect(screen.queryByText("Papas", { selector: "strong" })).not.toBeInTheDocument();
    expect(read).toHaveBeenLastCalledWith(destinationB);
  });

  it.each(["response", "error"])("ignores an old Identity's late %s after the new Identity read succeeds", async (outcome) => {
    const old = deferred<PreparationWork[]>();
    read.mockReturnValueOnce(old.promise);
    const unauthorized = vi.fn();
    const view = render(tree("identity-a", unauthorized));
    await waitFor(() => expect(read).toHaveBeenCalledTimes(1));
    read.mockResolvedValue([{ ...work, productOperationalName: "Nueva Identity" }]);
    view.rerender(tree("identity-b", unauthorized));
    await screen.findByText("Nueva Identity");
    expect(Stream.sources[0].closed).toBe(true);
    await act(async () => {
      if (outcome === "response") old.resolve([work]);
      else old.reject(new SessionProblemError(401, {}));
    });
    expect(screen.getByText("Nueva Identity")).toBeVisible();
    expect(screen.queryByText("Papas", { selector: "strong" })).not.toBeInTheDocument();
    expect(unauthorized).not.toHaveBeenCalled();
  });

  it("coalesces local command success and SSE through the same refresh coordinator", async () => {
    await loaded();
    const user = userEvent.setup();
    const command = deferred<PreparationCommandResult>();
    vi.mocked(startPreparationQuantity).mockReturnValueOnce(command.promise);
    await user.click(screen.getByRole("button", { name: /Iniciar Papas/ }));
    const old = deferred<PreparationWork[]>();
    read.mockReturnValueOnce(old.promise);
    await act(async () => Stream.sources[0].invalidate());
    await act(async () => {
      command.resolve({ ...work, pendingQuantity: 0, inPreparationQuantity: 4, historyId: "h", occurredAt: "now" });
      Stream.sources[0].invalidate();
    });
    expect(read).toHaveBeenCalledTimes(2);
    await act(async () => old.resolve([work]));
    expect(read).toHaveBeenCalledTimes(3);
    expect(startPreparationQuantity).toHaveBeenCalledTimes(1);
  });

  it.each(["start", "ready"])("preserves an uncertain %s intent and its exact retry across invalidation/refetch", async (kind) => {
    await loaded();
    const user = userEvent.setup();
    const command = vi.mocked(kind === "start" ? startPreparationQuantity : markPreparationQuantityReady);
    command.mockRejectedValue(new TypeError("network"));
    await user.click(screen.getByRole("button", { name: kind === "start" ? /Iniciar Papas/ : /Marcar listo Papas/ }));
    await screen.findByRole("button", { name: "Reintentar" });
    const intent = command.mock.calls[0][0];
    read.mockResolvedValue([{ ...work, pendingQuantity: 0, inPreparationQuantity: 0, readyQuantity: 5 }]);
    await act(async () => Stream.sources[0].invalidate());
    expect(screen.getByText("Todo listo")).toBeVisible();
    expect(command).toHaveBeenCalledTimes(1);
    await user.click(screen.getByRole("button", { name: "Reintentar" }));
    expect(command).toHaveBeenLastCalledWith(intent);
  });

  it("keeps ordinary refresh and commands usable when the stream is disconnected", async () => {
    const view = await loaded();
    const user = userEvent.setup();
    act(() => Stream.sources[0].onerror?.());
    await user.click(screen.getByRole("button", { name: /Actualizar preparaci/ }));
    await waitFor(() => expect(read).toHaveBeenCalledTimes(2));
    vi.mocked(startPreparationQuantity).mockResolvedValue({ ...work, historyId: "h", occurredAt: "now" });
    await user.click(screen.getByRole("button", { name: /Iniciar Papas/ }));
    await waitFor(() => expect(read).toHaveBeenCalledTimes(3));
    expect(startPreparationQuantity).toHaveBeenCalledTimes(1);
    view.unmount();
  });
});
