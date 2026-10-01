import { render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { TerminalOrderHistoryView } from "./TerminalOrderHistoryView.tsx";
import { OrderOperationsProblemError } from "./orderOperationsClient.ts";
const { getHistory } = vi.hoisted(() => ({ getHistory: vi.fn() }));
vi.mock("./terminalOrderHistoryClient.ts", () => ({
  getTerminalOrderHistory: getHistory,
}));
const stamp = "2026-01-01T00:00:00Z";
function fixture(type = "Closure") {
  return {
    orderId: "id",
    operationalReference: "REF",
    termination: {
      type,
      id: "term",
      occurredAt: stamp,
      actorIdentityId: "actor-term",
      pendingCompositionDiscarded: false,
    },
    finalContextId: "ctx",
    finalContextOperationalName: "Final",
    contextChanges: [
      {
        sequence: 1,
        previousContextOperationalName: "A",
        newContextOperationalName: "B",
        actorIdentityId: "actor-context",
        occurredAtUtc: stamp,
      },
    ],
    incorporations: [
      {
        id: "i1",
        ordinal: 1,
        confirmedAtUtc: stamp,
        actorIdentityId: "actor-confirm",
        confirmedContextId: "ctx",
        confirmedContext: "A",
        contents: [
          {
            contentOrdinal: 1,
            productId: "p",
            productOperationalNameSnapshot: null,
            originalConfirmedQuantity: 3,
            appliedPrice: "10",
            effectiveAppliedPrice: "12",
            instruction: null,
            requiresPreparation: true,
            preparationResponsibilityId: "prep-id",
            unavailableProductExceptionApplied: true,
            priceCorrections: [
              {
                previousPrice: "10",
                resultingPrice: "12",
                actorIdentityId: "actor-price",
                occurredAtUtc: stamp,
              },
            ],
            corrections: [
              {
                type: "Correction",
                previousQuantity: 0,
                resultingQuantity: 1,
                actorIdentityId: "actor-corr",
                occurredAtUtc: stamp,
              },
            ],
            cancellations: [
              {
                type: "Cancellation",
                previousQuantity: 0,
                resultingQuantity: 1,
                actorIdentityId: "actor-cancel",
                occurredAtUtc: stamp,
              },
            ],
            removedByCorrectionQuantity: 1,
            cancelledQuantity: 1,
            deliveries: [
              {
                type: "Delivered",
                quantity: 1,
                resultingDeliveredQuantity: 1,
                actorIdentityId: "actor-delivery",
                occurredAtUtc: stamp,
              },
            ],
            deliveryCorrections: [
              {
                previousDeliveredQuantity: 1,
                resultingDeliveredQuantity: 0,
                actorIdentityId: "actor-delivery-correction",
                occurredAtUtc: stamp,
              },
            ],
            effectiveDeliveredQuantity: 0,
            preparationHistory: [
              {
                type: "PreparationQuantityStarted",
                quantity: 1,
                resultingTotalQuantity: 3,
                resultingPendingQuantity: 2,
                resultingInPreparationQuantity: 1,
                resultingReadyQuantity: 0,
                actorIdentityId: "actor-prep",
                occurredAtUtc: stamp,
              },
            ],
          },
        ],
      },
      {
        id: "i2",
        ordinal: 2,
        confirmedAtUtc: stamp,
        actorIdentityId: null,
        confirmedContextId: "ctx",
        confirmedContext: "B",
        contents: [],
      },
    ],
    liquidation:
      type === "Closure"
        ? {
            mode: "ExternalCollection",
            functionalAmount: "30",
            declaredPaymentMedium: "Tarjeta",
            occurredAt: stamp,
            actorIdentityId: "actor-pay",
          }
        : null,
    closure:
      type === "Closure"
        ? { actorIdentityId: "actor-close", occurredAtUtc: stamp }
        : null,
    completeCancellation:
      type === "CompleteCancellation"
        ? {
            actorIdentityId: "actor-cancel-all",
            occurredAtUtc: stamp,
            pendingCompositionDiscarded: true,
            consequences: [],
          }
        : null,
  };
}
beforeEach(() => getHistory.mockReset());
async function load(data: ReturnType<typeof fixture>) {
  getHistory.mockResolvedValueOnce(data);
  const user = userEvent.setup();
  render(<TerminalOrderHistoryView />);
  await user.type(
    screen.getByLabelText("Referencia del pedido finalizado"),
    "REF",
  );
  await user.click(screen.getByRole("button", { name: "Ver historial" }));
  return screen.findByRole("region", { name: "Historial del pedido" });
}
describe("TerminalOrderHistoryView", () => {
  it("renders Closure read only, separated incorporations and distinct facts", async () => {
    const result = within(await load(fixture()));
    expect(result.getByText("Solo lectura")).toBeInTheDocument();
    expect(result.getAllByText("Cierre")).toHaveLength(2);
    expect(result.getByText("Contexto final").parentElement).toHaveTextContent(
      "Final",
    );
    expect(result.getAllByRole("listitem")[0]).toHaveTextContent("A → B");
    expect(
      result.getByRole("heading", { name: "Incorporación 1" }),
    ).toBeInTheDocument();
    expect(
      result.getByRole("heading", { name: "Incorporación 2" }),
    ).toBeInTheDocument();
    expect(
      result.getByText("Nombre histórico no disponible"),
    ).toBeInTheDocument();
    expect(
      result.getByText(/Último registro: Entrega corregida de 1 a 0/),
    ).toBeInTheDocument();
    expect(result.getAllByRole("listitem")[1]).toHaveTextContent("10 → 12");
    expect(result.getByText("Corrección de contenido")).toBeInTheDocument();
    expect(result.getByText("Cancelación de contenido")).toBeInTheDocument();
    expect(result.getByText(/Inicio: 1/)).toBeInTheDocument();
    expect(
      result.getByRole("heading", { name: "Entregas" }),
    ).toBeInTheDocument();
    expect(
      result.getByRole("heading", { name: "Correcciones de entrega" }),
    ).toBeInTheDocument();
    expect(result.getByText(/actor-confirm/)).toBeInTheDocument();
    expect(result.getByText(/Tarjeta/)).toBeInTheDocument();
    expect(
      result.getByRole("heading", { name: "Liquidación" }),
    ).toBeInTheDocument();
    expect(result.getAllByRole("heading", { name: "Cierre" })).toHaveLength(1);
    expect(result.getAllByRole("button")).toHaveLength(1);
    expect(
      result.getByRole("button", { name: "Copiar referencia" }),
    ).toBeInTheDocument();
  });
  it("renders Complete Cancellation without inventing liquidation or closure", async () => {
    const result = within(await load(fixture("CompleteCancellation")));
    expect(result.getAllByText("Cancelación completa")).toHaveLength(2);
    expect(
      result.getByText("Se descartó la composición pendiente."),
    ).toBeInTheDocument();
    expect(
      result.queryByRole("heading", { name: "Liquidación" }),
    ).not.toBeInTheDocument();
    expect(
      result.queryByRole("heading", { name: "Cierre" }),
    ).not.toBeInTheDocument();
  });
  it("shows loading and leaves the request pending until it resolves", async () => {
    getHistory.mockReturnValueOnce(new Promise(() => {}));
    const user = userEvent.setup();
    render(<TerminalOrderHistoryView />);
    await user.type(
      screen.getByLabelText("Referencia del pedido finalizado"),
      "REF",
    );
    await user.click(screen.getByRole("button", { name: "Ver historial" }));
    expect(screen.getByRole("status")).toHaveTextContent("Cargando historial");
  });
  it("shows the same unavailable message for an unknown or nonterminal reference", async () => {
    getHistory.mockRejectedValueOnce(
      new OrderOperationsProblemError({
        status: 404,
        code: "order_operations.order_history.not_found",
      }),
    );
    const user = userEvent.setup();
    render(<TerminalOrderHistoryView />);
    await user.type(
      screen.getByLabelText("Referencia del pedido finalizado"),
      "REF",
    );
    await user.click(screen.getByRole("button", { name: "Ver historial" }));
    expect(await screen.findByRole("alert")).toHaveTextContent(
      "Historial no disponible para esa referencia.",
    );
  });
  it("shows malformed reference feedback", async () => {
    getHistory.mockRejectedValueOnce(
      new OrderOperationsProblemError({
        status: 400,
        code: "order_operations.order.operational_reference_invalid",
      }),
    );
    const user = userEvent.setup();
    render(<TerminalOrderHistoryView />);
    await user.type(
      screen.getByLabelText("Referencia del pedido finalizado"),
      "bad",
    );
    await user.click(screen.getByRole("button", { name: "Ver historial" }));
    expect(await screen.findByRole("alert")).toHaveTextContent(
      "La referencia del pedido no es válida.",
    );
  });
  it("handles unauthorized response without disclosing reference existence", async () => {
    const unauthorized = vi.fn();
    getHistory.mockRejectedValueOnce(
      new OrderOperationsProblemError({ status: 403 }),
    );
    const user = userEvent.setup();
    render(<TerminalOrderHistoryView onUnauthorized={unauthorized} />);
    await user.type(
      screen.getByLabelText("Referencia del pedido finalizado"),
      "REF",
    );
    await user.click(screen.getByRole("button", { name: "Ver historial" }));
    expect(await screen.findByRole("alert")).toHaveTextContent(
      "No se pudo consultar el historial con la sesión actual.",
    );
    expect(unauthorized).not.toHaveBeenCalled();
  });
  it("returns an expired session to login", async () => {
    const unauthorized = vi.fn();
    getHistory.mockRejectedValueOnce(
      new OrderOperationsProblemError({ status: 401 }),
    );
    const user = userEvent.setup();
    render(<TerminalOrderHistoryView onUnauthorized={unauthorized} />);
    await user.type(
      screen.getByLabelText("Referencia del pedido finalizado"),
      "REF",
    );
    await user.click(screen.getByRole("button", { name: "Ver historial" }));
    expect(await screen.findByRole("alert")).toHaveTextContent(
      "La sesión no está disponible.",
    );
    expect(unauthorized).toHaveBeenCalledOnce();
  });
});
