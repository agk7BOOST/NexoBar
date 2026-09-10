import { act, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { OperationalInterventionPanel } from "./OperationalInterventionPanel.tsx";
import { discardAntiforgeryToken } from "../identity/sessionClient.ts";
import type { InterventionTarget } from "./operationalInterventionClient.ts";

const incorporationId = "01991e32-2a00-7000-8000-000000000002";
const workId = "01991e32-2a00-7000-8000-000000000003";
const readPath = `/api/order-operations/intervention/incorporations/${incorporationId}/contents/2`;
const started = "Cancelar cantidad ya iniciada";
const ready = "Cancelar cantidad ya lista";
const fetchMock = vi.fn<typeof fetch>();
const mutation = vi.fn<typeof fetch>();
const read = vi.fn<() => Promise<Response>>();
const onUnauthorized = vi.fn();
let current: InterventionTarget;

function json(value: unknown, status = 200) {
  return new Response(JSON.stringify(value), { status, headers: { "Content-Type": status >= 400 ? "application/problem+json" : "application/json" } });
}
function deferred<T>() {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>(done => { resolve = done; });
  return { promise, resolve };
}
async function open() {
  render(<OperationalInterventionPanel onUnauthorized={onUnauthorized} />);
  fireEvent.change(screen.getByLabelText("Identificador de Incorporación"), { target: { value: incorporationId } });
  fireEvent.change(screen.getByLabelText("Ordinal de Content"), { target: { value: "2" } });
  fireEvent.click(screen.getByRole("button", { name: "Consultar para intervenir" }));
  await screen.findByText("Hamburguesa");
}
function setQuantity(isReady: boolean, value: string) {
  fireEvent.change(screen.getByLabelText(`Cantidad ya ${isReady ? "lista" : "iniciada"} a cancelar`), { target: { value } });
}
function row(label: string, isReady: boolean) {
  const table = screen.getByRole("table", { name: `Vista previa: ${isReady ? ready : started}` });
  return within(table).getByRole("rowheader", { name: label }).parentElement!;
}

beforeEach(() => {
  current = {
    orderId: "01991e32-2a00-7000-8000-000000000001", incorporationId, contentOrdinal: 2, workId,
    productId: "01991e32-2a00-7000-8000-000000000004", productOperationalName: "Hamburguesa", instruction: "Sin cebolla",
    confirmedQuantity: 12, removedByCorrectionQuantity: 1, cancelledQuantity: 1,
    fulfillmentQuantity: 10, totalQuantity: 10, pendingQuantity: 2, inPreparationQuantity: 3,
    readyQuantity: 5, deliveredQuantity: 2, isFrozen: false, intervenableInPreparationQuantity: 3, intervenableReadyQuantity: 3,
  };
  discardAntiforgeryToken();
  onUnauthorized.mockReset(); mutation.mockReset(); read.mockReset(); fetchMock.mockReset();
  read.mockImplementation(async () => json(current));
  mutation.mockImplementation(async () => json({}));
  fetchMock.mockImplementation(async (url, init) => {
    if (url === "/api/security/antiforgery") return json({ requestToken: "csrf-original" });
    if (url === readPath) return read();
    if (init?.method === "POST" && String(url).startsWith(`/api/order-operations/intervention/work/${workId}/`)) return mutation(url, init);
    throw new Error(`Unexpected request: ${String(url)}`);
  });
  vi.stubGlobal("fetch", fetchMock);
});

describe("OperationalIntervention target and actions", () => {
  it("uses just the narrow read without Preparation queues or enablements", async () => {
    await open();
    expect(fetchMock).toHaveBeenCalledExactlyOnceWith(readPath, { credentials: "same-origin" });
    expect(screen.getByText("Sin cebolla")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: started })).toBeEnabled();
    expect(screen.getByRole("button", { name: ready })).toBeEnabled();
    expect(screen.queryByRole("combobox")).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: /corregir|undo|deshacer|pending/i })).not.toBeInTheDocument();
    expect(screen.getByRole("region", { name: "Intervención operacional" })).not.toHaveTextContent(/desperdicio|descarte|recuperación|inventario/i);
    expect(screen.getByText(/Esta preparación realmente comenzó/)).toBeInTheDocument();
    expect(screen.getByText(/Esta cantidad realmente llegó a lista/)).toBeInTheDocument();
  });

  it.each([false, true])("previews exact C/source/T/F changes without changing P or D (ready=%s)", async isReady => {
    await open(); setQuantity(isReady, "3");
    const input = screen.getByLabelText(`Cantidad ya ${isReady ? "lista" : "iniciada"} a cancelar`);
    expect(input).toHaveAttribute("max", "3");
    expect(input).toHaveAttribute("min", "1");
    const expected = [
      ["Confirmada (Q)", "12", "12"], ["Cancelada (C)", "1", "4"],
      ["Obligación de cumplimiento (F)", "10", "7"], ["Total vigente (T)", "10", "7"],
      ["Pendiente (P)", "2", "2"], ["Entregada (D)", "2", "2"],
      ["En preparación (I)", "3", isReady ? "3" : "0"], ["Lista (Y)", "5", isReady ? "2" : "5"],
    ];
    for (const [label, before, after] of expected) {
      expect(within(row(label, isReady)).getAllByRole("cell").map(cell => cell.textContent)).toEqual([before, after]);
    }
    expect(mutation).not.toHaveBeenCalled();
    fireEvent.click(screen.getByRole("button", { name: isReady ? ready : started }));
    await waitFor(() => expect(mutation).toHaveBeenCalledOnce());
    expect(mutation.mock.calls[0][1]?.body).toBe('{"quantity":3}');
  });

  it.each(["0", "-1", "1.5", "4", "5", "2147483648", ""])("rejects invalid or delivered-source quantity %s for both actions", async quantity => {
    await open();
    for (const isReady of [false, true]) {
      setQuantity(isReady, quantity);
      expect(screen.getByRole("button", { name: isReady ? ready : started })).toBeDisabled();
      expect(screen.queryByRole("table", { name: `Vista previa: ${isReady ? ready : started}` })).not.toBeInTheDocument();
    }
    expect(mutation).not.toHaveBeenCalled();
  });

  it("hides actions for empty I and fully delivered Ready", async () => {
    current = { ...current, inPreparationQuantity: 0, pendingQuantity: 5, deliveredQuantity: 5, intervenableInPreparationQuantity: 0, intervenableReadyQuantity: 0 };
    await open();
    expect(screen.queryByRole("button", { name: started })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: ready })).not.toBeInTheDocument();
    expect(screen.getByText("No hay cantidad elegible para intervenir.")).toBeInTheDocument();
  });

  it("offers no intervention when Frozen even with positive buckets", async () => {
    current = { ...current, isFrozen: true, intervenableInPreparationQuantity: 0, intervenableReadyQuantity: 0 };
    await open();
    expect(screen.getByText(/Pedido congelado por Liquidación/)).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: started })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: ready })).not.toBeInTheDocument();
    expect(fetchMock).toHaveBeenCalledTimes(1);
  });

  it.each([false, true])("refreshes authoritative State without optimistic mutation (ready=%s)", async isReady => {
    await open();
    const response = deferred<Response>();
    read.mockImplementationOnce(() => response.promise);
    setQuantity(isReady, "3");
    // The durable result can be older than the State read; it must not update current buckets.
    mutation.mockResolvedValue(json({ totalQuantity: 0, pendingQuantity: 0, inPreparationQuantity: 0, readyQuantity: 0 }));
    fireEvent.click(screen.getByRole("button", { name: isReady ? ready : started }));
    await waitFor(() => expect(read).toHaveBeenCalledTimes(2));
    const summary = screen.getByLabelText("Obligación vigente");
    expect(within(summary).getByText("Total vigente (T)").nextElementSibling).toHaveTextContent("10");
    expect(within(summary).getByText("Cancelada (C)").nextElementSibling).toHaveTextContent("1");
    expect(screen.getByRole("button", { name: started })).toBeDisabled();
    expect(screen.getByRole("button", { name: ready })).toBeDisabled();
    const authoritative = { ...current, cancelledQuantity: 4, fulfillmentQuantity: 7, totalQuantity: 7,
      ...(isReady ? { readyQuantity: 2, intervenableReadyQuantity: 0 } : { inPreparationQuantity: 0, intervenableInPreparationQuantity: 0 }) };
    await act(async () => response.resolve(json(authoritative)));
    expect(screen.queryByRole("button", { name: isReady ? ready : started })).not.toBeInTheDocument();
    expect(within(summary).getByText("Total vigente (T)").nextElementSibling).toHaveTextContent("7");
    expect(within(summary).getByText("Cancelada (C)").nextElementSibling).toHaveTextContent("4");
  });

  it("requires a successful refresh before any new action after read failure", async () => {
    await open(); read.mockRejectedValueOnce(new TypeError("offline"));
    fireEvent.click(screen.getByRole("button", { name: started }));
    await screen.findByText(/No se pudo actualizar el contenido/);
    expect(screen.getByRole("button", { name: started })).toBeDisabled();
    expect(screen.getByRole("button", { name: ready })).toBeDisabled();
    fireEvent.click(screen.getByRole("button", { name: "Actualizar contenido" }));
    await waitFor(() => expect(screen.getByRole("button", { name: started })).toBeEnabled());
    expect(mutation).toHaveBeenCalledOnce();
  });

  it.each([401, 403, 404])("handles narrow read HTTP %s without granting actions", async status => {
    read.mockResolvedValue(json({ code: "denied" }, status));
    render(<OperationalInterventionPanel onUnauthorized={onUnauthorized} />);
    fireEvent.change(screen.getByLabelText("Identificador de Incorporación"), { target: { value: incorporationId } });
    fireEvent.change(screen.getByLabelText("Ordinal de Content"), { target: { value: "2" } });
    fireEvent.click(screen.getByRole("button", { name: "Consultar para intervenir" }));
    if (status === 401) await waitFor(() => expect(onUnauthorized).toHaveBeenCalledOnce());
    else await screen.findByText(status === 403 ? /no tiene autorización de Intervención operacional/ : /No se encontró ese contenido/);
    expect(screen.queryByRole("button", { name: started })).not.toBeInTheDocument();
    expect(screen.queryByText(/destino no habilitado/i)).not.toBeInTheDocument();
    expect(fetchMock).toHaveBeenCalledOnce();
  });

  it.each([401, 403])("handles command HTTP %s with session/capability conventions", async status => {
    await open(); mutation.mockResolvedValue(json({ status: 200 }, status));
    fireEvent.click(screen.getByRole("button", { name: ready }));
    if (status === 401) await waitFor(() => expect(onUnauthorized).toHaveBeenCalledOnce());
    else await screen.findByText(/no tiene autorización de Intervención operacional/);
    expect(screen.queryByRole("button", { name: ready })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Reintentar misma intervención" })).not.toBeInTheDocument();
    expect(screen.queryByText(/destino no habilitado/i)).not.toBeInTheDocument();
  });

  it.each(["quantity_exceeds_eligible", "idempotency_key_conflict", "frozen"])("refreshes on stale 409 %s", async code => {
    await open();
    mutation.mockImplementation(async () => {
      current = { ...current, isFrozen: true, intervenableInPreparationQuantity: 0, intervenableReadyQuantity: 0 };
      return json({ code: code === "frozen" ? "order_operations.order.frozen" : `order_operations.intervention.${code}` }, 409);
    });
    fireEvent.click(screen.getByRole("button", { name: ready }));
    await screen.findByText(/Pedido congelado por Liquidación/);
    expect(read).toHaveBeenCalledTimes(2);
    expect(screen.queryByRole("button", { name: ready })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Reintentar misma intervención" })).not.toBeInTheDocument();
    expect(screen.getByText(code === "frozen" ? /El Pedido está congelado/ : /El Estado cambió/)).toBeInTheDocument();
  });

  it.each(["network", "timeout", "500", "503"])("retains exact intent and blocks conflicting actions on %s", async failure => {
    await open(); setQuantity(false, "2");
    if (failure === "network") mutation.mockRejectedValueOnce(new TypeError("offline"));
    else if (failure === "timeout") mutation.mockRejectedValueOnce(new DOMException("timeout", "TimeoutError"));
    else mutation.mockResolvedValueOnce(json({}, Number(failure)));
    fireEvent.click(screen.getByRole("button", { name: started }));
    await screen.findByRole("button", { name: "Reintentar misma intervención" });
    const original = mutation.mock.calls[0];
    expect(screen.getByRole("button", { name: started })).toBeDisabled();
    expect(screen.getByRole("button", { name: ready })).toBeDisabled();
    expect(screen.getByRole("button", { name: "Consultar para intervenir" })).toBeDisabled();
    expect(screen.getByRole("button", { name: "Actualizar contenido" })).toBeDisabled();
    expect(screen.getByLabelText("Cantidad ya iniciada a cancelar")).toBeDisabled();
    expect(screen.getByLabelText("Identificador de Incorporación")).toBeDisabled();
    fireEvent.click(screen.getByRole("button", { name: ready }));
    fireEvent.submit(screen.getByRole("form", { name: "Consultar contenido para intervención" }));
    expect(mutation).toHaveBeenCalledOnce();
    expect(read).toHaveBeenCalledOnce();
    // Changing the shared cache cannot change the token retained by the uncertain intent.
    discardAntiforgeryToken();
    const response = deferred<Response>();
    mutation.mockImplementationOnce(() => response.promise);
    fireEvent.click(screen.getByRole("button", { name: "Reintentar misma intervención" }));
    expect(screen.queryByRole("button", { name: "Reintentar misma intervención" })).not.toBeInTheDocument();
    expect(mutation).toHaveBeenCalledTimes(2);
    expect(mutation.mock.calls[1]).toEqual(original);
    expect(fetchMock.mock.calls.filter(([url]) => url === "/api/security/antiforgery")).toHaveLength(1);
    await act(async () => response.resolve(json({})));
    await waitFor(() => expect(read).toHaveBeenCalledTimes(2));
    expect(screen.queryByRole("alert")).not.toBeInTheDocument();
    expect(fetchMock.mock.calls.every(([url]) => !String(url).includes("pending-composition"))).toBe(true);
  });

  it("blocks conflicting actions while acquiring CSRF and never sends after unmount", async () => {
    const token = deferred<Response>();
    fetchMock.mockImplementation(async (url) => url === readPath ? read() : token.promise);
    const view = render(<OperationalInterventionPanel onUnauthorized={onUnauthorized} />);
    fireEvent.change(screen.getByLabelText("Identificador de Incorporación"), { target: { value: incorporationId } });
    fireEvent.change(screen.getByLabelText("Ordinal de Content"), { target: { value: "2" } });
    fireEvent.click(screen.getByRole("button", { name: "Consultar para intervenir" }));
    await screen.findByText("Hamburguesa");
    fireEvent.click(screen.getByRole("button", { name: ready }));
    expect(screen.getByRole("button", { name: started })).toBeDisabled();
    expect(screen.getByRole("button", { name: ready })).toBeDisabled();
    view.unmount();
    await act(async () => token.resolve(json({ requestToken: "csrf" })));
    expect(fetchMock).toHaveBeenCalledTimes(2);
  });
});
