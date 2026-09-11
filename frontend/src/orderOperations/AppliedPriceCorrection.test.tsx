import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, expect, it, vi } from "vitest";
import { discardAntiforgeryToken } from "../identity/sessionClient.ts";
import { OrderLookup } from "./OrderLookup.tsx";
import type { OrderResponse } from "./orderOperationsClient.ts";
import type { AppliedPriceEvaluation } from "./appliedPriceCorrectionClient.ts";
import type { OrderDelivery } from "../delivery/deliveryClient.ts";

const orderId = "01991e32-2a00-7000-8000-000000000001";
const incorporationId = "01991e32-2a00-7000-8000-000000000002";
const root = `/api/order-operations/orders/${orderId}`;
const targetPath = (ordinal: number) => `${root}/incorporations/${incorporationId}/contents/${ordinal}`;
const fetchMock = vi.fn<typeof fetch>();
const mutation = vi.fn<typeof fetch>();
const unauthorized = vi.fn();
const state = vi.fn();
const busy = vi.fn();
let order: OrderResponse;
let delivery: OrderDelivery;
let evaluations: AppliedPriceEvaluation[];
let readStatus = 200;
let failOrderRefresh = false;
const json = (value: unknown, status = 200) => new Response(JSON.stringify(value), { status, headers: { "Content-Type": "application/json" } });
const problem = (status: number) => new Response(JSON.stringify({ code: "order_operations.applied_price_correction.rejected" }), { status, headers: { "Content-Type": "application/problem+json" } });
function result(ordinal = 3, price = "8") {
  return json({ orderId, incorporationId, contentOrdinal: ordinal, historyId: "history", occurredAt: "2026-09-11T12:00:00Z", previousEffectiveAppliedPrice: "10", resultingEffectiveAppliedPrice: price });
}
function row(ordinal = 3) { return within(screen.getByRole("article", { name: `Incorporación 1, contenido ${ordinal}` })); }
function price(ordinal: number, label: string) { return row(ordinal).getByText(label).nextElementSibling; }
async function open(identityId: string | null = "basic-only") {
  const user = userEvent.setup();
  render(<OrderLookup products={[]} activeOperationalReference={null} onContinueOrder={vi.fn()} identityId={identityId ?? undefined} onUnauthorized={unauthorized} onOrderState={state} onEndingBusy={busy} />);
  await user.type(screen.getByLabelText("Referencia operacional"), orderId);
  await user.click(screen.getByRole("button", { name: "Buscar Pedido" }));
  await waitFor(() => expect(screen.getByRole("button", { name: "Buscar Pedido" })).toBeEnabled());
  return user;
}
async function read(user: ReturnType<typeof userEvent.setup>) {
  await user.click(screen.getByRole("button", { name: "Consultar precios aplicados" }));
  await waitFor(() => expect(screen.getByRole("button", { name: "Consultar precios aplicados" })).toBeEnabled());
}
async function confirm(user: ReturnType<typeof userEvent.setup>, ordinal = 3, value = "8") {
  await user.click(row(ordinal).getByRole("button", { name: "Corregir precio aplicado" }));
  await user.click(row(ordinal).getByRole("button", { name: `Aplicar precio ${value}` }));
}
beforeEach(() => {
  vi.clearAllMocks(); mutation.mockReset(); discardAntiforgeryToken();
  readStatus = 200; failOrderRefresh = false;
  order = { operationalReference: orderId, context: "Mesa 1", incorporations: [{ id: incorporationId, ordinal: 1, confirmedAt: "2026-09-11T10:00:00Z", items: [
    { productId: "same-product", quantity: 2, appliedPrice: "10", instruction: "sin sal" },
    { productId: "same-product", quantity: 2, appliedPrice: "10", instruction: "con sal" },
  ] }], functionalAmount: "20", isLiquidationEligible: false, liquidationBlockers: ["pending_composition"],
    isLiquidated: false, isFrozen: false, liquidatedAmount: null, liquidationMode: null, declaredPaymentMedium: null, isClosed: false, closedAt: null, isClosureEligible: false };
  delivery = { orderId, operationalReference: orderId, currentContext: "Mesa 1", contents: [3, 7].map(contentOrdinal => ({ incorporationId, incorporationOrdinal: 1, contentOrdinal, productId: "same-product", productOperationalName: "Producto", instruction: contentOrdinal === 3 ? "sin sal" : "con sal", totalQuantity: 2, confirmedQuantity: 2, currentFulfillmentQuantity: 2, requiresPreparationAtConfirmation: false, readyQuantity: null, deliveredQuantity: contentOrdinal === 3 ? 2 : 0, deliverableQuantity: 0, remainingQuantity: 0 })) };
  evaluations = [3, 7].map(contentOrdinal => ({ orderId, incorporationId, contentOrdinal, appliedPrice: "10", effectiveAppliedPrice: "10", currentCatalogPrice: "8", isCorrectionAvailable: true, blockers: [] }));
  mutation.mockImplementation(async () => result());
  fetchMock.mockImplementation(async (input, init) => {
    const url = String(input);
    if (init?.method === "POST") return mutation(input, init);
    if (url === "/api/security/antiforgery") return json({ requestToken: "csrf-original" });
    if (url === root) return failOrderRefresh ? problem(503) : json(order);
    if (url === `${root}/delivery`) return json(delivery);
    if (url === `/api/orders/${orderId}/complete-cancellation`) return json({ orderId, isTerminal: false, isCompletelyCancelled: false, cancellationId: null, cancelledAt: null, hasEffectiveDelivery: true, hasPendingComposition: true, remainingFulfillmentQuantity: 2, requiresOperationalIntervention: false, isEligible: false, blockers: ["effective_delivery"], consequences: [] });
    const evaluation = evaluations.find(x => url === `${targetPath(x.contentOrdinal)}/applied-price-correction`);
    if (evaluation) return readStatus === 200 ? json(evaluation) : problem(readStatus);
    throw new Error(`Unexpected request ${url}`);
  });
  vi.stubGlobal("fetch", fetchMock);
});

it("distinguishes original, effective and Catalog prices and uses exact targets for the same Product", async () => {
  evaluations[0].effectiveAppliedPrice = "9";
  const user = await open(); await read(user);
  expect(price(3, "Precio confirmado original")).toHaveTextContent("10");
  expect(price(3, "Precio aplicado efectivo actual")).toHaveTextContent("9");
  expect(price(3, "Precio vigente en catálogo")).toHaveTextContent("8");
  expect(row(7).getByText("con sal")).toBeInTheDocument();
  await user.click(row(7).getByRole("button", { name: "Corregir precio aplicado" }));
  expect(row(7).getByText(/Precio aplicado actual: 10 → Precio vigente corregido: 8/)).toBeInTheDocument();
  expect(within(screen.getByRole("region", { name: "Corrección de precio aplicado" })).queryByRole("textbox")).not.toBeInTheDocument();
  expect(within(screen.getByRole("region", { name: "Corrección de precio aplicado" })).queryByRole("spinbutton")).not.toBeInTheDocument();
  mutation.mockResolvedValueOnce(result(7));
  await user.click(row(7).getByRole("button", { name: "Aplicar precio 8" }));
  await waitFor(() => expect(mutation).toHaveBeenCalledTimes(1));
  expect(mutation.mock.calls[0][0]).toBe(`${targetPath(7)}/apply-current-catalog-price`);
  expect(mutation.mock.calls[0][1]?.body).toBe("{}");
  await waitFor(() => expect(screen.getByRole("button", { name: "Consultar precios aplicados" })).toBeEnabled());
});

it.each(["8", "10"])("uses authoritative no-op availability with effective price %s without sending a command or comparing locally", async effectiveAppliedPrice => {
  evaluations = evaluations.map(x => ({ ...x, effectiveAppliedPrice, isCorrectionAvailable: false, blockers: ["no_correction_to_apply"] }));
  const user = await open(); await read(user);
  expect(row().getByText("No hay una corrección de precio pendiente para aplicar.")).toBeInTheDocument();
  expect(screen.queryByRole("button", { name: "Corregir precio aplicado" })).not.toBeInTheDocument();
  expect(mutation).not.toHaveBeenCalled();
});

it("refreshes authoritative economic state and prices, preserves Delivery and PendingComposition, and permits a later correction", async () => {
  const user = await open(); await read(user);
  const beforeDelivery = structuredClone(delivery);
  mutation.mockImplementationOnce(async () => {
    order = { ...order, functionalAmount: "16" };
    evaluations[0] = { ...evaluations[0], effectiveAppliedPrice: "8", isCorrectionAvailable: false, blockers: ["no_correction_to_apply"] };
    return result();
  });
  await confirm(user);
  await waitFor(() => expect(price(3, "Precio aplicado efectivo actual")).toHaveTextContent("8"));
  expect(price(3, "Precio confirmado original")).toHaveTextContent("10");
  expect(screen.getByText("16")).toBeInTheDocument();
  expect(row().getByText("Entregado: 2")).toBeInTheDocument();
  expect(delivery).toEqual(beforeDelivery);
  expect(state).toHaveBeenLastCalledWith(expect.objectContaining({ functionalAmount: "16", liquidationBlockers: ["pending_composition"] }));
  expect(fetchMock.mock.calls.filter(([url]) => String(url) === `${targetPath(3)}/applied-price-correction`)).toHaveLength(2);
  evaluations[0] = { ...evaluations[0], currentCatalogPrice: "9", isCorrectionAvailable: true, blockers: [] };
  await read(user);
  mutation.mockResolvedValueOnce(result(3, "9"));
  await confirm(user, 3, "9");
  await waitFor(() => expect(mutation).toHaveBeenCalledTimes(2));
  expect(mutation.mock.calls[1][1]?.headers).not.toEqual(mutation.mock.calls[0][1]?.headers);
  expect(mutation.mock.calls.every(([url]) => String(url).endsWith("apply-current-catalog-price"))).toBe(true);
  await waitFor(() => expect(screen.getByRole("button", { name: "Consultar precios aplicados" })).toBeEnabled());
});

it("does not optimistically alter effective price, amount or Delivered while a command is pending", async () => {
  let complete!: (response: Response) => void;
  mutation.mockImplementationOnce(() => new Promise(resolve => { complete = resolve; }));
  const user = await open(); await read(user); await confirm(user);
  expect(price(3, "Precio aplicado efectivo actual")).toHaveTextContent("10");
  expect(screen.getByText("20")).toBeInTheDocument();
  expect(row().getByText("Entregado: 2")).toBeInTheDocument();
  expect(screen.getByRole("button", { name: "Liquidar" })).toBeDisabled();
  complete(result());
  await waitFor(() => expect(screen.getByRole("button", { name: "Consultar precios aplicados" })).toBeEnabled());
});

it.each(["order_frozen", "order_closed", "order_completely_cancelled"])("honors authoritative terminal blocker %s", async code => {
  evaluations = evaluations.map(x => ({ ...x, isCorrectionAvailable: false, blockers: [code] }));
  const user = await open(); await read(user);
  expect(screen.queryByRole("button", { name: "Corregir precio aplicado" })).not.toBeInTheDocument();
  expect(mutation).not.toHaveBeenCalled();
});

it("retains price reads on a Frozen Order and does not offer a new correction", async () => {
  order = { ...order, isLiquidated: true, isFrozen: true, liquidatedAmount: "20", liquidationMode: "ExternalCollection" };
  const user = await open(); await read(user);
  expect(price(3, "Precio confirmado original")).toHaveTextContent("10");
  expect(screen.queryByRole("button", { name: "Corregir precio aplicado" })).not.toBeInTheDocument();
  expect(mutation).not.toHaveBeenCalled();
});

it("allows an open F=0 and D=0 Content without Preparation or other responsibility dependencies", async () => {
  delivery.contents = delivery.contents.map(x => ({ ...x, currentFulfillmentQuantity: 0, totalQuantity: 0, deliveredQuantity: 0 }));
  order.functionalAmount = "0";
  const user = await open(); await read(user); await confirm(user);
  await waitFor(() => expect(mutation).toHaveBeenCalledTimes(1));
  expect(row().getByText("Entregado: 0")).toBeInTheDocument();
  expect(screen.getByText("0")).toBeInTheDocument();
  const surface = screen.getByRole("region", { name: "Corrección de precio aplicado" });
  expect(surface).not.toHaveTextContent(/CatalogConfiguration|OperationalIntervention|PreparationEnablement/);
  await waitFor(() => expect(screen.getByRole("button", { name: "Consultar precios aplicados" })).toBeEnabled());
});

it.each(["network", "408", "503"])("preserves exact intent and token through %s uncertainty and a subsequent Catalog change", async failure => {
  mutation.mockImplementationOnce(async () => { if (failure === "network") throw new TypeError("offline"); return problem(Number(failure)); });
  const user = await open(); await read(user); await confirm(user);
  await screen.findByRole("button", { name: "Reintentar misma corrección de precio" });
  const original = mutation.mock.calls[0];
  const reads = fetchMock.mock.calls.length;
  evaluations[0].currentCatalogPrice = "9";
  discardAntiforgeryToken();
  expect(row(7).getByRole("button", { name: "Corregir precio aplicado" })).toBeDisabled();
  expect(screen.getByRole("button", { name: "Consultar precios aplicados" })).toBeDisabled();
  expect(screen.getByRole("button", { name: "Buscar Pedido" })).toBeDisabled();
  mutation.mockImplementationOnce(async () => {
    expect(fetchMock.mock.calls).toHaveLength(reads + 1);
    evaluations[0].effectiveAppliedPrice = "8";
    return result();
  });
  await user.click(screen.getByRole("button", { name: "Reintentar misma corrección de precio" }));
  await waitFor(() => expect(mutation).toHaveBeenCalledTimes(2));
  expect(mutation.mock.calls[1]).toEqual(original);
  expect(original[1]?.headers).toEqual(expect.objectContaining({ "X-NexoBar-CSRF": "csrf-original", "Idempotency-Key": expect.stringMatching(/^[\da-f]{8}-[\da-f]{4}-4[\da-f]{3}-[89ab][\da-f]{3}-[\da-f]{12}$/) }));
  expect(original[1]?.body).toBe("{}");
  await waitFor(() => expect(price(3, "Precio aplicado efectivo actual")).toHaveTextContent("8"));
  expect(price(3, "Precio vigente en catálogo")).toHaveTextContent("9");
});

it.each([401, 403, 409])("handles command HTTP %s with existing session/conflict conventions", async status => {
  mutation.mockResolvedValueOnce(problem(status));
  const user = await open(); await read(user); await confirm(user);
  if (status === 401) {
    await waitFor(() => expect(unauthorized).toHaveBeenCalledOnce());
  } else {
    await screen.findByText(status === 403 ? /Esta Identity no tiene autorización para corregir precios aplicados/ : /El Estado del Pedido cambió o la corrección fue rechazada/);
    await waitFor(() => expect(fetchMock.mock.calls.filter(([url]) => String(url) === `${targetPath(3)}/applied-price-correction`)).toHaveLength(2));
    expect(state).toHaveBeenCalledTimes(2);
    expect(unauthorized).not.toHaveBeenCalled();
  }
  expect(screen.queryByRole("button", { name: "Reintentar misma corrección de precio" })).not.toBeInTheDocument();
});

it.each([401, 403])("handles evaluation HTTP %s without offering a correction", async status => {
  readStatus = status;
  const user = await open(); await read(user);
  if (status === 401) expect(unauthorized).toHaveBeenCalled();
  else expect(screen.getByText(/Esta Identity no tiene autorización para corregir precios aplicados/)).toBeInTheDocument();
  expect(screen.queryByRole("button", { name: "Corregir precio aplicado" })).not.toBeInTheDocument();
  expect(mutation).not.toHaveBeenCalled();
});

it("keeps new commands blocked after a failed authoritative refresh and retries only reads", async () => {
  const user = await open(); await read(user);
  failOrderRefresh = true;
  await confirm(user);
  await screen.findByText(/No se pudo actualizar el Estado autoritativo/);
  expect(screen.getByRole("button", { name: "Consultar precios aplicados" })).toBeDisabled();
  failOrderRefresh = false;
  await user.click(screen.getByRole("button", { name: "Actualizar precios y Pedido" }));
  await waitFor(() => expect(screen.getByRole("button", { name: "Consultar precios aplicados" })).toBeEnabled());
  expect(mutation).toHaveBeenCalledTimes(1);
});

it("does not offer the price surface without a Session", async () => {
  await open(null);
  expect(screen.queryByRole("region", { name: "Corrección de precio aplicado" })).not.toBeInTheDocument();
});
