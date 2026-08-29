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
        },
        {
          productId: "product-not-in-current-catalog",
          quantity: 1,
          appliedPrice: "7.25",
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

describe("OrderLookup", () => {
  beforeEach(() => {
    getOrderMock.mockReset();
  });

  it("renderiza el campo etiquetado y el botón", () => {
    render(<OrderLookup products={[]} />);

    expect(screen.getByLabelText("Referencia operacional")).toBeInTheDocument();
    expect(
      screen.getByRole("button", { name: "Buscar Pedido" }),
    ).toBeInTheDocument();
  });

  it("consulta con el texto exacto y presenta el Pedido completo", async () => {
    getOrderMock.mockResolvedValueOnce(order);
    const user = userEvent.setup();
    render(<OrderLookup products={[currentProduct]} />);

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
  });

  it("usa el nombre actual solo como etiqueta, conserva appliedPrice histórico y cae a productId", async () => {
    getOrderMock.mockResolvedValueOnce(order);
    const user = userEvent.setup();
    render(<OrderLookup products={[currentProduct]} />);

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

  it("muestra Pedido no encontrado, limpia el resultado y conserva la Referencia", async () => {
    getOrderMock.mockResolvedValueOnce(order);
    const user = userEvent.setup();
    render(<OrderLookup products={[]} />);
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
    render(<OrderLookup products={[]} />);

    await search(user, "esto no es un UUID");

    expect(getOrderMock).toHaveBeenCalledWith("esto no es un UUID");
    expect(
      await screen.findByText("La Referencia operacional no es válida."),
    ).toBeInTheDocument();
  });

  it("muestra un fallo de comunicación y no conserva un resultado anterior", async () => {
    getOrderMock.mockResolvedValueOnce(order);
    const user = userEvent.setup();
    render(<OrderLookup products={[]} />);
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
    render(<OrderLookup products={[]} />);

    await user.type(
      screen.getByLabelText("Referencia operacional"),
      "pending-reference",
    );
    await user.click(screen.getByRole("button", { name: "Buscar Pedido" }));

    expect(screen.getByRole("button", { name: "Buscando…" })).toBeDisabled();
  });
});
