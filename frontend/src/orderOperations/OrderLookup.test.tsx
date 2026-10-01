import { act, render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";
import type { Product } from "../catalog/catalogClient.ts";
import { OrderLookup } from "./OrderLookup.tsx";
import {
  OrderLookupNetworkError,
  OrderOperationsProblemError,
  type OrderResponse,
} from "./orderOperationsClient.ts";

const {
  getOrderMock,
  listContextsMock,
  changeContextMock,
  antiforgeryMock,
  endingSendMock,
  evaluateCancellationMock,
  sse,
} = vi.hoisted(() => ({
  getOrderMock: vi.fn(),
  listContextsMock: vi.fn(),
  changeContextMock: vi.fn(),
  antiforgeryMock: vi.fn(),
  endingSendMock: vi.fn(),
  evaluateCancellationMock: vi.fn(),
  sse: { invalidate: null as null | (() => void) },
}));

vi.mock("../notifications/ActiveOrderFreshnessSubscription.tsx", () => ({
  ActiveOrderFreshnessSubscription: ({
    invalidate,
  }: {
    invalidate: () => void;
  }) => {
    sse.invalidate = invalidate;
    return null;
  },
}));
vi.mock("./orderEndingClient.ts", async (importOriginal) => ({
  ...(await importOriginal<typeof import("./orderEndingClient.ts")>()),
  sendOrderEndingIntent: endingSendMock,
}));
vi.mock("./completeCancellationClient.ts", async (importOriginal) => ({
  ...(await importOriginal<typeof import("./completeCancellationClient.ts")>()),
  evaluateCompleteCancellation: evaluateCancellationMock,
}));

vi.mock("./orderOperationsClient.ts", async (importOriginal) => {
  const original =
    await importOriginal<typeof import("./orderOperationsClient.ts")>();

  return {
    ...original,
    getOrder: getOrderMock,
    listOrderContexts: listContextsMock,
    changeOrderContext: changeContextMock,
  };
});
vi.mock("../identity/sessionClient.ts", async (importOriginal) => ({
  ...(await importOriginal<typeof import("../identity/sessionClient.ts")>()),
  getAntiforgeryToken: antiforgeryMock,
}));

const currentProduct: Product = {
  id: "product-current",
  operationalName: "Agua tónica actual",
  price: "99.99",
  isActive: true,
  isAvailable: true,
  requiresPreparation: false,
  preparationResponsibilityId: null,
};

const order: OrderResponse = {
  functionalAmount: "28.25",
  isLiquidationEligible: false,
  liquidationBlockers: ["unresolved_fulfillment"],
  isLiquidated: false,
  isFrozen: false,
  liquidatedAmount: null,
  liquidationMode: null,
  declaredPaymentMedium: null,
  isClosureEligible: false,
  isClosed: false,
  closedAt: null,
  operationalReference: "order-reference-from-response",
  context: "Mesa 7",
  contextId: "ctx-test",
  incorporations: [
    {
      id: "incorporation-1",
      ordinal: 1,
      confirmedAt: "2026-08-29T14:30:00Z",
      items: [
        {
          productId: currentProduct.id,
          quantity: 2,
          appliedPrice: "10.50",
          instruction: "sin hielo",
          productOperationalNameSnapshot: "Agua tónica al confirmar",
          unavailableProductExceptionApplied: false,
        },
        {
          productId: currentProduct.id,
          quantity: 1,
          appliedPrice: "7.25",
          instruction: null,
          productOperationalNameSnapshot: null,
          unavailableProductExceptionApplied: false,
        },
      ],
    },
  ],
};

async function search(
  user: ReturnType<typeof userEvent.setup>,
  operationalReference: string,
) {
  const input = screen.getByLabelText("Referencia del pedido");
  await user.clear(input);
  await user.type(input, operationalReference);
  await user.click(screen.getByRole("button", { name: "Buscar pedido" }));
}

function renderLookup(
  products: Product[] = [],
  options?: {
    activeOperationalReference?: string | null;
    activeOrderId?: string | null;
    identityId?: string;
    canChangeOrderContext?: boolean;
    isOrderMutationBusy?: (reference: string) => boolean;
    onContinueOrder?: (reference: string) => void;
    onOpenDelivery?: (reference: string) => void;
    requestedLookup?: {
      operationalReference: string;
      sequence: number;
    };
  },
) {
  return render(
    <OrderLookup
      products={products}
      activeOperationalReference={options?.activeOperationalReference ?? null}
      activeOrderId={options?.activeOrderId ?? null}
      identityId={options?.identityId}
      canChangeOrderContext={options?.canChangeOrderContext ?? false}
      isOrderMutationBusy={options?.isOrderMutationBusy}
      onContinueOrder={options?.onContinueOrder ?? (() => undefined)}
      onOpenDelivery={options?.onOpenDelivery}
      requestedLookup={options?.requestedLookup}
    />,
  );
}

describe("OrderLookup", () => {
  beforeEach(() => {
    getOrderMock.mockReset();
    listContextsMock.mockReset().mockResolvedValue([
      { id: "ctx-test", operationalName: "Mesa 7" },
      { id: "ctx-b", operationalName: "Mesa 8" },
    ]);
    changeContextMock.mockReset();
    antiforgeryMock.mockReset().mockResolvedValue("csrf");
    endingSendMock.mockReset();
    evaluateCancellationMock.mockReset().mockResolvedValue({
      orderId: order.operationalReference,
      isTerminal: false,
      isCompletelyCancelled: false,
      cancellationId: null,
      cancelledAt: null,
      hasEffectiveDelivery: true,
      hasPendingComposition: false,
      remainingFulfillmentQuantity: 0,
      requiresOperationalIntervention: false,
      isEligible: false,
      blockers: ["effective_delivery"],
      consequences: [],
    });
    sse.invalidate = null;
  });

  it("keeps the command refresh authoritative when SSE invalidates during Liquidation", async () => {
    const before = {
      ...order,
      isLiquidationEligible: true,
      liquidationBlockers: [],
    };
    const frozen = {
      ...before,
      isLiquidationEligible: false,
      liquidationBlockers: ["already_liquidated"],
      isLiquidated: true,
      isFrozen: true,
      liquidatedAmount: before.functionalAmount,
      liquidationMode: "Simple",
      declaredPaymentMedium: "Vale",
      isClosureEligible: true,
    };
    let confirmCommand!: (timestamp: string) => void;
    endingSendMock.mockReturnValueOnce(
      new Promise<string>((resolve) => {
        confirmCommand = resolve;
      }),
    );
    getOrderMock.mockResolvedValueOnce(before).mockResolvedValue(frozen);
    const user = userEvent.setup();
    renderLookup([], {
      activeOperationalReference: order.operationalReference,
      activeOrderId: "order-id",
      identityId: "identity-id",
    });
    await search(user, order.operationalReference);
    await screen.findByRole("region", { name: "Pedido activo" });
    await user.type(screen.getByLabelText("Medio de pago"), "Vale");
    await user.click(screen.getByRole("button", { name: "Liquidar" }));
    await waitFor(() => expect(endingSendMock).toHaveBeenCalledOnce());
    act(() => sse.invalidate?.());
    expect(getOrderMock).toHaveBeenCalledTimes(1);
    await act(async () => confirmCommand("2026-09-24T21:28:47Z"));
    await waitFor(() =>
      expect(
        screen.getByRole("button", { name: "Cerrar pedido" }),
      ).toBeEnabled(),
    );
    expect(getOrderMock).toHaveBeenCalledTimes(2);
    expect(
      screen.queryByText(/No se pudo actualizar el Pedido/),
    ).not.toBeInTheDocument();
  });

  it("renderiza el campo etiquetado y el botón", () => {
    renderLookup();

    expect(screen.getByLabelText("Referencia del pedido")).toBeInTheDocument();
    expect(
      screen.getByRole("button", { name: "Buscar pedido" }),
    ).toBeInTheDocument();
  });

  it("consulta con el texto exacto y presenta el Pedido completo", async () => {
    getOrderMock.mockResolvedValueOnce(order);
    const user = userEvent.setup();
    renderLookup([currentProduct]);

    const enteredReference = "referencia opaca no UUID";
    await search(user, enteredReference);

    expect(getOrderMock).toHaveBeenCalledWith(enteredReference);
    const result = await screen.findByRole("region", {
      name: "Pedido consultado",
    });
    const incorporation = within(result).getByRole("article", {
      name: "Incorporación 1",
    });
    expect(result).toHaveTextContent(order.operationalReference);
    expect(result).toHaveTextContent("Mesa 7");
    expect(incorporation).toHaveTextContent("Número");
    expect(incorporation).toHaveTextContent("1");
    expect(within(incorporation).getByRole("time")).toHaveAttribute(
      "datetime",
      "2026-08-29T14:30:00Z",
    );
    expect(within(incorporation).getByRole("time")).not.toHaveTextContent(
      "2026-08-29T14:30:00Z",
    );
    expect(incorporation).toHaveTextContent("2");
    expect(incorporation).toHaveTextContent("10.50");
    expect(incorporation).toHaveTextContent("sin hielo");
    expect(incorporation).toHaveTextContent("Sin instrucción");
  });

  it("usa el nombre capturado en Confirmation y no interpreta el pasado con el nombre actual", async () => {
    getOrderMock.mockResolvedValueOnce(order);
    const user = userEvent.setup();
    renderLookup([currentProduct]);

    await search(user, "order-reference");

    const result = await screen.findByRole("region", {
      name: "Pedido consultado",
    });
    expect(result).toHaveTextContent("Agua tónica al confirmar");
    expect(result).not.toHaveTextContent(currentProduct.operationalName);
    expect(result).toHaveTextContent("Nombre histórico no disponible");
    expect(result).not.toHaveTextContent(currentProduct.id);
    expect(result).toHaveTextContent("10.50");
    expect(result).not.toHaveTextContent(currentProduct.price);
  });

  it("uses the explicit legacy fallback for a null snapshot without using Catalog or ProductId", async () => {
    getOrderMock.mockResolvedValueOnce({
      ...order,
      incorporations: [
        {
          ...order.incorporations[0]!,
          items: [
            {
              ...order.incorporations[0]!.items[0]!,
              productOperationalNameSnapshot: null,
            },
          ],
        },
      ],
    });
    const user = userEvent.setup();
    renderLookup([currentProduct]);
    await search(user, order.operationalReference);

    const result = await screen.findByRole("region", {
      name: "Pedido consultado",
    });
    expect(
      within(result).getByRole("row", {
        name: "Nombre histórico no disponible, cantidad 2, sin hielo",
      }),
    ).toBeVisible();
    expect(result).not.toHaveTextContent(currentProduct.operationalName);
    expect(result).not.toHaveTextContent(currentProduct.id);
  });

  it("distingue líneas del mismo Product por cantidad e instruction", async () => {
    getOrderMock.mockResolvedValueOnce({
      ...order,
      incorporations: [
        {
          ...order.incorporations[0]!,
          items: [
            {
              productId: currentProduct.id,
              quantity: 1,
              appliedPrice: "10.50",
              instruction: null,
              productOperationalNameSnapshot: "Agua tónica al confirmar",
              unavailableProductExceptionApplied: false,
            },
            {
              productId: currentProduct.id,
              quantity: 2,
              appliedPrice: "10.50",
              instruction: "sin hielo",
              productOperationalNameSnapshot: "Agua tónica al confirmar",
              unavailableProductExceptionApplied: false,
            },
          ],
        },
      ],
    });
    const user = userEvent.setup();
    renderLookup([currentProduct]);

    await search(user, "order-reference");

    expect(
      await screen.findByRole("row", {
        name: "Agua tónica al confirmar, cantidad 1, sin instrucción",
      }),
    ).toHaveTextContent("Sin instrucción");
    expect(
      screen.getByRole("row", {
        name: "Agua tónica al confirmar, cantidad 2, sin hielo",
      }),
    ).toHaveTextContent("sin hielo");
  });

  it("muestra Pedido no encontrado, limpia el resultado y conserva la Referencia", async () => {
    getOrderMock.mockResolvedValueOnce(order);
    const user = userEvent.setup();
    renderLookup();
    await search(user, "existing-reference");
    await screen.findByRole("region", { name: "Pedido consultado" });

    getOrderMock.mockRejectedValueOnce(
      new OrderOperationsProblemError({
        status: 404,
        code: "order_operations.order.not_found",
      }),
    );
    await search(user, "missing-reference");

    expect(
      await screen.findByText("No se encontró un pedido con esa referencia."),
    ).toBeInTheDocument();
    expect(
      screen.queryByRole("region", { name: "Pedido consultado" }),
    ).not.toBeInTheDocument();
    expect(screen.getByLabelText("Referencia del pedido")).toHaveValue(
      "missing-reference",
    );
  });

  it("acepta texto no UUID y muestra el error comprensible de Referencia inválida", async () => {
    getOrderMock.mockRejectedValueOnce(
      new OrderOperationsProblemError({
        status: 400,
        code: "order_operations.order.operational_reference_invalid",
      }),
    );
    const user = userEvent.setup();
    renderLookup();

    await search(user, "esto no es un UUID");

    expect(getOrderMock).toHaveBeenCalledWith("esto no es un UUID");
    expect(
      await screen.findByText("La referencia del pedido no es válida."),
    ).toBeInTheDocument();
  });

  it("muestra un fallo de comunicación y no conserva un resultado anterior", async () => {
    getOrderMock.mockResolvedValueOnce(order);
    const user = userEvent.setup();
    renderLookup();
    await search(user, "existing-reference");
    await screen.findByRole("region", { name: "Pedido consultado" });

    getOrderMock.mockRejectedValueOnce(new OrderLookupNetworkError());
    await search(user, "network-failure-reference");

    expect(
      await screen.findByText(/fallo de comunicación/),
    ).toBeInTheDocument();
    expect(
      screen.queryByRole("region", { name: "Pedido consultado" }),
    ).not.toBeInTheDocument();
  });

  it("muestra loading y deshabilita el botón durante la búsqueda", async () => {
    getOrderMock.mockReturnValueOnce(new Promise(() => undefined));
    const user = userEvent.setup();
    renderLookup();

    await user.type(
      screen.getByLabelText("Referencia del pedido"),
      "pending-reference",
    );
    await user.click(screen.getByRole("button", { name: "Buscar pedido" }));

    expect(screen.getByRole("button", { name: "Buscando…" })).toBeDisabled();
  });

  it("un lookup manual no activa y el botón Continuar entrega la referencia", async () => {
    getOrderMock.mockResolvedValueOnce(order);
    const onContinueOrder = vi.fn();
    const user = userEvent.setup();
    renderLookup([], { onContinueOrder });

    await search(user, "manual-reference");
    const result = await screen.findByRole("region", {
      name: "Pedido consultado",
    });
    expect(onContinueOrder).not.toHaveBeenCalled();

    await within(result)
      .getByRole("button", { name: "Agregar productos a este Pedido" })
      .click();
    expect(onContinueOrder).toHaveBeenCalledWith(order.operationalReference);
  });

  it("requestedLookup consulta externamente y otro sequence refresca la misma referencia", async () => {
    getOrderMock.mockResolvedValue(order);
    const { rerender } = renderLookup([], {
      requestedLookup: {
        operationalReference: "external-reference",
        sequence: 1,
      },
    });

    await screen.findByRole("region", { name: "Pedido consultado" });
    expect(getOrderMock).toHaveBeenCalledTimes(1);
    expect(getOrderMock).toHaveBeenLastCalledWith("external-reference");

    rerender(
      <OrderLookup
        products={[]}
        requestedLookup={{
          operationalReference: "external-reference",
          sequence: 2,
        }}
        activeOperationalReference={null}
        onContinueOrder={() => undefined}
      />,
    );

    await vi.waitFor(() => expect(getOrderMock).toHaveBeenCalledTimes(2));
    expect(getOrderMock).toHaveBeenLastCalledWith("external-reference");
  });

  it("abre Delivery desde el Pedido ya consultado sin duplicar la búsqueda", async () => {
    getOrderMock.mockResolvedValueOnce(order);
    const onOpenDelivery = vi.fn();
    const user = userEvent.setup();
    renderLookup([], { onOpenDelivery });

    await search(user, "manual-reference");
    await user.click(
      screen.getByRole("button", { name: "Abrir entrega de este pedido" }),
    );

    expect(getOrderMock).toHaveBeenCalledTimes(1);
    expect(onOpenDelivery).toHaveBeenCalledWith(order.operationalReference);
  });

  it("mantiene disponible agregar productos para el Pedido activo", async () => {
    getOrderMock.mockResolvedValueOnce(order);
    const onContinueOrder = vi.fn();
    renderLookup([], {
      activeOperationalReference: order.operationalReference,
      onContinueOrder,
      requestedLookup: {
        operationalReference: order.operationalReference,
        sequence: 1,
      },
    });

    const result = await screen.findByRole("region", { name: "Pedido activo" });
    expect(result).toHaveTextContent(
      "Este Pedido está activo para agregar productos.",
    );
    within(result)
      .getByRole("button", { name: "Agregar productos a este Pedido" })
      .click();
    expect(onContinueOrder).toHaveBeenCalledWith(order.operationalReference);
  });

  it("changes A to B with exact expected Context, reloads the same Order, and hides the UUID", async () => {
    const user = userEvent.setup();
    getOrderMock.mockResolvedValueOnce(order).mockResolvedValueOnce({
      ...order,
      context: "Mesa 8",
      contextId: "ctx-b",
    });
    changeContextMock.mockResolvedValueOnce(undefined);
    renderLookup([], {
      activeOperationalReference: order.operationalReference,
      activeOrderId: "order-id",
      canChangeOrderContext: true,
    });
    await search(user, order.operationalReference);
    expect(
      await screen.findByLabelText("Contexto actual del Pedido"),
    ).toHaveTextContent("Mesa 7");
    expect(
      screen.getByLabelText("Contexto actual del Pedido"),
    ).not.toHaveTextContent("ctx-test");
    await user.selectOptions(
      screen.getByLabelText("Contexto destino"),
      "ctx-b",
    );
    await user.click(screen.getByRole("button", { name: "Cambiar contexto" }));
    await waitFor(() =>
      expect(changeContextMock).toHaveBeenCalledWith(
        "order-id",
        { expectedCurrentContextId: "ctx-test", newContextId: "ctx-b" },
        expect.any(String),
        "csrf",
      ),
    );
    expect(
      await screen.findByLabelText("Contexto actual del Pedido"),
    ).toHaveTextContent("Mesa 8");
    expect(
      screen.getByText("order-reference-from-response"),
    ).toBeInTheDocument();
    expect(screen.getByText("sin hielo")).toBeInTheDocument();
  });

  it.each([
    ["order.context_change.no_change", "ya tiene ese Contexto"],
    [
      "order.context_change.expected_context_stale",
      "Otra persona cambió el Contexto",
    ],
    ["order.context_change.target_context_not_found", "ya no está disponible"],
  ])(
    "refreshes authoritative Order after %s without automatic resubmission",
    async (code, message) => {
      const user = userEvent.setup();
      getOrderMock.mockResolvedValue(order);
      changeContextMock.mockRejectedValueOnce(
        new OrderOperationsProblemError({ status: 409, code }),
      );
      renderLookup([], {
        activeOperationalReference: order.operationalReference,
        activeOrderId: "order-id",
        canChangeOrderContext: true,
      });
      await search(user, order.operationalReference);
      await screen.findByRole("option", { name: "Mesa 8" });
      await user.selectOptions(
        screen.getByLabelText("Contexto destino"),
        "ctx-b",
      );
      await user.click(
        screen.getByRole("button", { name: "Cambiar contexto" }),
      );
      expect(
        await screen.findByLabelText("Estado del cambio de Contexto"),
      ).toHaveTextContent(message);
      expect(changeContextMock).toHaveBeenCalledTimes(1);
      expect(getOrderMock).toHaveBeenCalledTimes(2);
      if (code === "order.context_change.target_context_not_found")
        expect(listContextsMock).toHaveBeenCalledTimes(2);
    },
  );

  it.each([
    ["frozen", { isFrozen: true }],
    ["closed", { isClosed: true }],
    ["cancelled", { liquidationBlockers: ["order_completely_cancelled"] }],
  ])(
    "does not offer enabled Context Change for %s Order",
    async (_label, state) => {
      const user = userEvent.setup();
      getOrderMock.mockResolvedValue({ ...order, ...state });
      renderLookup([], {
        activeOperationalReference: order.operationalReference,
        activeOrderId: "order-id",
        canChangeOrderContext: true,
      });
      await search(user, order.operationalReference);
      expect(
        await screen.findByLabelText("Contexto actual del Pedido"),
      ).toBeVisible();
      expect(
        screen.queryByRole("button", { name: "Cambiar contexto" }),
      ).not.toBeInTheDocument();
    },
  );

  it("retries an uncertain A to B change with the exact original intent and key", async () => {
    const user = userEvent.setup();
    getOrderMock.mockResolvedValue(order);
    changeContextMock
      .mockRejectedValueOnce(new Error("uncertain"))
      .mockResolvedValueOnce(undefined);
    renderLookup([], {
      activeOperationalReference: order.operationalReference,
      activeOrderId: "order-id",
      canChangeOrderContext: true,
    });
    await search(user, order.operationalReference);
    await screen.findByRole("option", { name: "Mesa 8" });
    await user.selectOptions(
      screen.getByLabelText("Contexto destino"),
      "ctx-b",
    );
    await user.click(screen.getByRole("button", { name: "Cambiar contexto" }));
    await user.click(
      await screen.findByRole("button", { name: "Reintentar mismo cambio" }),
    );
    expect(changeContextMock.mock.calls[0]).toEqual(
      changeContextMock.mock.calls[1],
    );
  });

  it("keeps Context Change available while another Order mutation signal is busy", async () => {
    const user = userEvent.setup();
    getOrderMock.mockResolvedValue(order);
    renderLookup([], {
      activeOperationalReference: order.operationalReference,
      activeOrderId: "order-id",
      canChangeOrderContext: true,
      isOrderMutationBusy: () => true,
    });
    await search(user, order.operationalReference);
    await screen.findByRole("option", { name: "Mesa 8" });
    await user.selectOptions(
      screen.getByLabelText("Contexto destino"),
      "ctx-b",
    );
    expect(
      await screen.findByRole("button", { name: "Cambiar contexto" }),
    ).toBeEnabled();
  });

  it("renders the intervention marker only from the persisted applied fact", async () => {
    getOrderMock.mockResolvedValueOnce({
      ...order,
      incorporations: [
        {
          ...order.incorporations[0]!,
          items: [
            {
              ...order.incorporations[0]!.items[0]!,
              productOperationalNameSnapshot: null,
              unavailableProductExceptionApplied: true,
            },
            {
              ...order.incorporations[0]!.items[1]!,
              productOperationalNameSnapshot: null,
              unavailableProductExceptionApplied: false,
            },
          ],
        },
      ],
    });
    const user = userEvent.setup();
    renderLookup([currentProduct]);

    await search(user, "order-reference");
    const result = await screen.findByRole("region", {
      name: "Pedido consultado",
    });
    expect(
      within(result).getByText("Incorporado mediante intervención"),
    ).toBeInTheDocument();
    expect(
      within(result).getAllByText("Incorporado mediante intervención"),
    ).toHaveLength(1);
  });
});
