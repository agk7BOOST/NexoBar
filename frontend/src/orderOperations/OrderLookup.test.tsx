import { render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";
import type { Product } from "../catalog/catalogClient.ts";
import { OrderLookup } from "./OrderLookup.tsx";
import {
  OrderLookupNetworkError,
  OrderOperationsProblemError,
  type OrderResponse,
} from "./orderOperationsClient.ts";

const { getOrderMock } = vi.hoisted(() => ({
  getOrderMock: vi.fn(),
}));

vi.mock("./orderOperationsClient.ts", async (importOriginal) => {
  const original =
    await importOriginal<typeof import("./orderOperationsClient.ts")>();

  return { ...original, getOrder: getOrderMock };
});

const currentProduct: Product = {
  id: "product-current",
  operationalName: "Agua tónica actual",
  price: "99.99",
  isActive: true,
  isAvailable: true,
  requiresPreparation: false,
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
        },
        {
          productId: "product-not-in-current-catalog",
          quantity: 1,
          appliedPrice: "7.25",
          instruction: null,
        },
      ],
    },
  ],
};

async function search(
  user: ReturnType<typeof userEvent.setup>,
  operationalReference: string,
) {
  const input = screen.getByLabelText("Referencia operacional");
  await user.clear(input);
  await user.type(input, operationalReference);
  await user.click(screen.getByRole("button", { name: "Buscar Pedido" }));
}

function renderLookup(
  products: Product[] = [],
  options?: {
    activeOperationalReference?: string | null;
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
      onContinueOrder={options?.onContinueOrder ?? (() => undefined)}
      onOpenDelivery={options?.onOpenDelivery}
      requestedLookup={options?.requestedLookup}
    />,
  );
}

describe("OrderLookup", () => {
  beforeEach(() => {
    getOrderMock.mockReset();
  });

  it("renderiza el campo etiquetado y el botón", () => {
    renderLookup();

    expect(screen.getByLabelText("Referencia operacional")).toBeInTheDocument();
    expect(
      screen.getByRole("button", { name: "Buscar Pedido" }),
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
    expect(incorporation).toHaveTextContent("Ordinal");
    expect(incorporation).toHaveTextContent("1");
    expect(within(incorporation).getByRole("time")).toHaveAttribute(
      "datetime",
      "2026-08-29T14:30:00Z",
    );
    expect(incorporation).toHaveTextContent("2026-08-29T14:30:00Z");
    expect(incorporation).toHaveTextContent("2");
    expect(incorporation).toHaveTextContent("10.50");
    expect(incorporation).toHaveTextContent("sin hielo");
    expect(incorporation).toHaveTextContent("Sin instrucción");
  });

  it("usa el nombre actual solo como etiqueta, conserva appliedPrice histórico y cae a productId", async () => {
    getOrderMock.mockResolvedValueOnce(order);
    const user = userEvent.setup();
    renderLookup([currentProduct]);

    await search(user, "order-reference");

    const result = await screen.findByRole("region", {
      name: "Pedido consultado",
    });
    expect(result).toHaveTextContent(currentProduct.operationalName);
    expect(result).toHaveTextContent("product-not-in-current-catalog");
    expect(result).toHaveTextContent("10.50");
    expect(result).not.toHaveTextContent(currentProduct.price);
    expect(result).toHaveTextContent(
      /Catálogo actual y no constituye Historia/,
    );
    expect(result).toHaveTextContent(/condición histórica confirmada/);
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
            },
            {
              productId: currentProduct.id,
              quantity: 2,
              appliedPrice: "10.50",
              instruction: "sin hielo",
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
        name: "Agua tónica actual, cantidad 1, sin instrucción",
      }),
    ).toHaveTextContent("Sin instrucción");
    expect(
      screen.getByRole("row", {
        name: "Agua tónica actual, cantidad 2, sin hielo",
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
      await screen.findByText(
        "No se encontró un Pedido con esa Referencia operacional.",
      ),
    ).toBeInTheDocument();
    expect(
      screen.queryByRole("region", { name: "Pedido consultado" }),
    ).not.toBeInTheDocument();
    expect(screen.getByLabelText("Referencia operacional")).toHaveValue(
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
      await screen.findByText("La Referencia operacional no es válida."),
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
      screen.getByLabelText("Referencia operacional"),
      "pending-reference",
    );
    await user.click(screen.getByRole("button", { name: "Buscar Pedido" }));

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
      .getByRole("button", { name: "Continuar este Pedido" })
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
      screen.getByRole("button", { name: "Abrir entrega de este Pedido" }),
    );

    expect(getOrderMock).toHaveBeenCalledTimes(1);
    expect(onOpenDelivery).toHaveBeenCalledWith(order.operationalReference);
  });

  it("marca el Pedido activo y omite la acción redundante", async () => {
    getOrderMock.mockResolvedValueOnce(order);
    renderLookup([], {
      activeOperationalReference: order.operationalReference,
      requestedLookup: {
        operationalReference: order.operationalReference,
        sequence: 1,
      },
    });

    const result = await screen.findByRole("region", { name: "Pedido activo" });
    expect(result).toHaveTextContent(
      "Este Pedido está activo para una nueva Incorporación.",
    );
    expect(
      within(result).queryByRole("button", { name: "Continuar este Pedido" }),
    ).not.toBeInTheDocument();
  });
});
