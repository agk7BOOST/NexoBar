import { render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { useState } from "react";
import { beforeEach, describe, expect, it, vi } from "vitest";
import type { Product } from "../catalog/catalogClient.ts";
import { OrderWorkflow, type OrderTargetRequest } from "./OrderWorkflow.tsx";
import {
  OrderOperationsNetworkError,
  OrderOperationsProblemError,
  type FirstConfirmationResponse,
  type SubsequentConfirmationResponse,
} from "./orderOperationsClient.ts";

const { confirmFirstMock, confirmSubsequentMock } = vi.hoisted(() => ({
  confirmFirstMock: vi.fn(),
  confirmSubsequentMock: vi.fn(),
}));

vi.mock("./orderOperationsClient.ts", async (importOriginal) => {
  const original =
    await importOriginal<typeof import("./orderOperationsClient.ts")>();
  return {
    ...original,
    confirmFirst: confirmFirstMock,
    confirmSubsequent: confirmSubsequentMock,
  };
});

const water: Product = {
  id: "product-water",
  operationalName: "Agua",
  price: "10.00",
  isActive: true,
  isAvailable: true,
  requiresPreparation: false,
};

const soda: Product = {
  ...water,
  id: "product-soda",
  operationalName: "Soda",
  price: "12.00",
};

const firstResponse: FirstConfirmationResponse = {
  operationalReference: "order-created",
  context: "Mesa 7",
  firstIncorporation: {
    id: "incorporation-1",
    confirmedAt: "2026-08-29T14:30:00Z",
    items: [{ productId: water.id, quantity: 2, appliedPrice: "10.00" }],
  },
};

const subsequentResponse: SubsequentConfirmationResponse = {
  operationalReference: "order-active",
  incorporation: {
    id: "incorporation-2",
    ordinal: 2,
    confirmedAt: "2026-08-29T15:00:00Z",
    items: [{ productId: water.id, quantity: 2, appliedPrice: "12.00" }],
  },
};

interface HarnessProps {
  initialReference?: string | null;
  requestedTarget?: OrderTargetRequest;
  onActivate?: (reference: string) => void;
  onChanged?: (reference: string) => void;
}

function Harness({
  initialReference = null,
  requestedTarget,
  onActivate = () => undefined,
  onChanged = () => undefined,
}: HarnessProps) {
  const [activeReference, setActiveReference] = useState(initialReference);

  return (
    <OrderWorkflow
      products={[water, soda]}
      activeOperationalReference={activeReference}
      requestedTarget={requestedTarget}
      onActivateOrder={(reference) => {
        setActiveReference(reference);
        onActivate(reference);
      }}
      onStartNewOrder={() => setActiveReference(null)}
      onOrderChanged={onChanged}
    />
  );
}

async function addProduct(
  user: ReturnType<typeof userEvent.setup>,
  product: Product,
  mode: "Composición inicial" | "Nueva Composición",
  times = 1,
) {
  const button = screen.getByRole("button", {
    name: `Agregar ${product.operationalName} a ${mode}`,
  });
  for (let index = 0; index < times; index += 1) {
    await user.click(button);
  }
}

describe("OrderWorkflow - Composición y Primera Confirmación", () => {
  beforeEach(() => {
    confirmFirstMock.mockReset();
    confirmSubsequentMock.mockReset();
  });

  it("mantiene una entry por Product y cantidades enteras positivas", async () => {
    const user = userEvent.setup();
    render(<Harness />);
    await addProduct(user, water, "Composición inicial", 2);

    const composition = screen.getByRole("region", {
      name: "Composición inicial",
    });
    expect(
      within(composition).getByLabelText("Cantidad de Agua"),
    ).toHaveTextContent("2");
    expect(within(composition).getAllByText("Agua")).toHaveLength(2);

    await user.click(
      within(composition).getByRole("button", {
        name: "Disminuir cantidad de Agua",
      }),
    );
    expect(
      within(composition).getByLabelText("Cantidad de Agua"),
    ).toHaveTextContent("1");
    await user.click(
      within(composition).getByRole("button", {
        name: "Disminuir cantidad de Agua",
      }),
    );
    expect(
      within(composition).getByText("La Composición está vacía."),
    ).toBeInTheDocument();
  });

  it("aumenta y retira entradas sin producir Confirmaciones", async () => {
    const user = userEvent.setup();
    render(<Harness />);
    await addProduct(user, water, "Composición inicial");

    await user.click(
      screen.getByRole("button", { name: "Aumentar cantidad de Agua" }),
    );
    expect(screen.getByLabelText("Cantidad de Agua")).toHaveTextContent("2");
    await user.click(
      screen.getByRole("button", {
        name: "Retirar Agua de la composición",
      }),
    );

    expect(screen.getByText("La Composición está vacía.")).toBeInTheDocument();
    expect(confirmFirstMock).not.toHaveBeenCalled();
    expect(confirmSubsequentMock).not.toHaveBeenCalled();
  });

  it("requiere Contexto y Composición para habilitar la Primera Confirmación", async () => {
    const user = userEvent.setup();
    render(<Harness />);
    const confirm = screen.getByRole("button", {
      name: "Confirmar Primera Composición",
    });

    expect(confirm).toBeDisabled();
    await user.type(screen.getByLabelText("Contexto"), "   ");
    await addProduct(user, water, "Composición inicial");
    expect(confirm).toBeDisabled();
    await user.clear(screen.getByLabelText("Contexto"));
    await user.type(screen.getByLabelText("Contexto"), "Mesa 7");
    expect(confirm).toBeEnabled();
  });

  it("no permite agregar Products no disponibles", async () => {
    const unavailable = { ...water, isAvailable: false };
    const user = userEvent.setup();
    render(
      <OrderWorkflow
        products={[unavailable]}
        activeOperationalReference={null}
        onActivateOrder={() => undefined}
        onStartNewOrder={() => undefined}
        onOrderChanged={() => undefined}
      />,
    );

    const add = screen.getByRole("button", {
      name: "Agregar Agua a Composición inicial",
    });
    expect(add).toBeDisabled();
    await user.click(add);
    expect(screen.getByText("La Composición está vacía.")).toBeInTheDocument();
  });

  it("envía Primera Confirmación exacta, activa el Pedido y solicita lookup", async () => {
    confirmFirstMock.mockResolvedValueOnce(firstResponse);
    const onActivate = vi.fn();
    const onChanged = vi.fn();
    const user = userEvent.setup();
    render(<Harness onActivate={onActivate} onChanged={onChanged} />);
    await user.type(screen.getByLabelText("Contexto"), "Mesa 7");
    await addProduct(user, water, "Composición inicial", 2);

    await user.click(
      screen.getByRole("button", { name: "Confirmar Primera Composición" }),
    );
    await screen.findByRole("region", { name: "Nueva Composición" });

    const [request, key] = confirmFirstMock.mock.calls[0]!;
    expect(request).toEqual({
      context: "Mesa 7",
      items: [{ productId: water.id, quantity: 2 }],
    });
    expect(key).toMatch(
      /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i,
    );
    expect(onActivate).toHaveBeenCalledWith("order-created");
    expect(onChanged).toHaveBeenCalledWith("order-created");
    expect(screen.getByText("La Composición está vacía.")).toBeInTheDocument();
    expect(screen.queryByLabelText("Contexto")).not.toBeInTheDocument();
  });

  it("conserva y bloquea el snapshot incierto de Primera Confirmación", async () => {
    confirmFirstMock.mockRejectedValueOnce(new OrderOperationsNetworkError());
    const user = userEvent.setup();
    render(<Harness />);
    await user.type(screen.getByLabelText("Contexto"), "Mesa 7");
    await addProduct(user, water, "Composición inicial", 2);
    await user.click(
      screen.getByRole("button", { name: "Confirmar Primera Composición" }),
    );

    const uncertain = await screen.findByRole("region", {
      name: "Primera Confirmación con resultado no confirmado",
    });
    expect(uncertain).toHaveTextContent("Mesa 7");
    expect(uncertain).toHaveTextContent("Cantidad: 2");
    expect(screen.getByLabelText("Contexto")).toBeDisabled();
    expect(
      screen.getByRole("button", {
        name: "Agregar Agua a Composición inicial",
      }),
    ).toBeDisabled();
    await new Promise((resolve) => setTimeout(resolve, 50));
    expect(confirmFirstMock).toHaveBeenCalledOnce();
  });

  it("reintenta manualmente la misma Primera Confirmación y limpia al resolver", async () => {
    confirmFirstMock.mockRejectedValueOnce(new OrderOperationsNetworkError());
    const user = userEvent.setup();
    render(<Harness />);
    await user.type(screen.getByLabelText("Contexto"), "Mesa 7");
    await addProduct(user, water, "Composición inicial", 2);
    await user.click(
      screen.getByRole("button", { name: "Confirmar Primera Composición" }),
    );
    await screen.findByRole("region", {
      name: "Primera Confirmación con resultado no confirmado",
    });
    const firstCall = confirmFirstMock.mock.calls[0];

    confirmFirstMock.mockResolvedValueOnce(firstResponse);
    await user.click(
      screen.getByRole("button", {
        name: "Reintentar misma Primera Confirmación",
      }),
    );

    await screen.findByRole("heading", { name: "Nueva Composición" });
    expect(confirmFirstMock.mock.calls[1]).toEqual(firstCall);
    expect(
      screen.queryByRole("region", {
        name: "Primera Confirmación con resultado no confirmado",
      }),
    ).not.toBeInTheDocument();
  });

  it("discard de Primera Confirmación incierta conserva el borrador y genera una key nueva", async () => {
    confirmFirstMock.mockRejectedValueOnce(new OrderOperationsNetworkError());
    const user = userEvent.setup();
    render(<Harness />);
    await user.type(screen.getByLabelText("Contexto"), "Mesa 7");
    await addProduct(user, water, "Composición inicial");
    await user.click(
      screen.getByRole("button", { name: "Confirmar Primera Composición" }),
    );
    await screen.findByRole("region", {
      name: "Primera Confirmación con resultado no confirmado",
    });
    const firstKey = confirmFirstMock.mock.calls[0]?.[1];

    await user.click(
      screen.getByRole("button", { name: "Descartar intención incierta" }),
    );
    expect(screen.getByLabelText("Cantidad de Agua")).toHaveTextContent("1");
    expect(screen.getByLabelText("Contexto")).toBeEnabled();

    confirmFirstMock.mockRejectedValueOnce(
      new OrderOperationsProblemError({
        code: "order_operations.first_confirmation.product_not_current",
      }),
    );
    await user.click(
      screen.getByRole("button", { name: "Confirmar Primera Composición" }),
    );
    await screen.findByText(/ya no está vigente/);
    expect(confirmFirstMock.mock.calls[1]?.[1]).not.toBe(firstKey);
  });

  it("mapea el rechazo funcional y conserva la Composición", async () => {
    confirmFirstMock.mockRejectedValueOnce(
      new OrderOperationsProblemError({
        code: "order_operations.first_confirmation.product_unavailable",
        productId: water.id,
      }),
    );
    const user = userEvent.setup();
    render(<Harness />);
    await user.type(screen.getByLabelText("Contexto"), "Mesa 7");
    await addProduct(user, water, "Composición inicial");
    await user.click(
      screen.getByRole("button", { name: "Confirmar Primera Composición" }),
    );

    expect(await screen.findByText(/ya no está disponible/)).toHaveTextContent(
      "Agua",
    );
    expect(screen.getByLabelText("Cantidad de Agua")).toHaveTextContent("1");
  });
});

describe("OrderWorkflow - Confirmación posterior", () => {
  beforeEach(() => {
    confirmFirstMock.mockReset();
    confirmSubsequentMock.mockReset();
  });

  it("usa modo posterior, envía solo items en orden UI y refresca el mismo Pedido", async () => {
    confirmSubsequentMock.mockResolvedValueOnce(subsequentResponse);
    const onChanged = vi.fn();
    const user = userEvent.setup();
    render(<Harness initialReference="order-active" onChanged={onChanged} />);
    expect(screen.queryByLabelText("Contexto")).not.toBeInTheDocument();
    await addProduct(user, soda, "Nueva Composición");
    await addProduct(user, water, "Nueva Composición", 2);

    await user.click(
      screen.getByRole("button", { name: "Confirmar nueva Incorporación" }),
    );
    await screen.findByText("Nueva Incorporación confirmada correctamente.");

    const [reference, request, key] = confirmSubsequentMock.mock.calls[0]!;
    expect(reference).toBe("order-active");
    expect(request).toEqual({
      items: [
        { productId: soda.id, quantity: 1 },
        { productId: water.id, quantity: 2 },
      ],
    });
    expect(JSON.stringify(request)).not.toMatch(
      /context|price|name|availability|requiresPreparation/i,
    );
    expect(key).toMatch(
      /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i,
    );
    expect(onChanged).toHaveBeenCalledWith("order-active");
    expect(screen.getByText("order-active")).toBeInTheDocument();
    expect(screen.getByText("La Composición está vacía.")).toBeInTheDocument();
  });

  it("congela reference/items/key, bloquea edición/destino y reintenta igual", async () => {
    confirmSubsequentMock.mockRejectedValueOnce(
      new OrderOperationsNetworkError(),
    );
    const user = userEvent.setup();
    render(<Harness initialReference="order-active" />);
    await addProduct(user, water, "Nueva Composición", 2);
    await user.click(
      screen.getByRole("button", { name: "Confirmar nueva Incorporación" }),
    );

    const uncertain = await screen.findByRole("region", {
      name: "Confirmación posterior con resultado no confirmado",
    });
    expect(uncertain).toHaveTextContent("order-active");
    expect(uncertain).toHaveTextContent("Cantidad: 2");
    expect(
      screen.getByRole("button", {
        name: "Agregar Agua a Nueva Composición",
      }),
    ).toBeDisabled();
    const firstCall = confirmSubsequentMock.mock.calls[0];

    await user.click(
      screen.getByRole("button", { name: "Iniciar nuevo Pedido" }),
    );
    expect(
      screen.getByText(/Resolvé o descartá la Confirmación incierta/),
    ).toBeInTheDocument();
    expect(
      screen.getByRole("heading", { name: "Nueva Composición" }),
    ).toBeInTheDocument();
    await new Promise((resolve) => setTimeout(resolve, 50));
    expect(confirmSubsequentMock).toHaveBeenCalledOnce();

    confirmSubsequentMock.mockResolvedValueOnce(subsequentResponse);
    await user.click(
      screen.getByRole("button", {
        name: "Reintentar misma Confirmación posterior",
      }),
    );
    await screen.findByText("Nueva Incorporación confirmada correctamente.");
    expect(confirmSubsequentMock.mock.calls[1]).toEqual(firstCall);
  });

  it("discard de incertidumbre habilita edición y una key nueva", async () => {
    confirmSubsequentMock.mockRejectedValueOnce(
      new OrderOperationsNetworkError(),
    );
    const user = userEvent.setup();
    render(<Harness initialReference="order-active" />);
    await addProduct(user, water, "Nueva Composición");
    await user.click(
      screen.getByRole("button", { name: "Confirmar nueva Incorporación" }),
    );
    await screen.findByRole("region", {
      name: "Confirmación posterior con resultado no confirmado",
    });
    const firstKey = confirmSubsequentMock.mock.calls[0]?.[2];

    await user.click(
      screen.getByRole("button", { name: "Descartar intención incierta" }),
    );
    expect(
      screen.getByRole("button", {
        name: "Aumentar cantidad de Agua",
      }),
    ).toBeEnabled();

    confirmSubsequentMock.mockRejectedValueOnce(
      new OrderOperationsProblemError({
        code: "order_operations.first_confirmation.product_not_current",
      }),
    );
    await user.click(
      screen.getByRole("button", { name: "Confirmar nueva Incorporación" }),
    );
    await screen.findByText(/ya no está vigente/);
    expect(confirmSubsequentMock.mock.calls[1]?.[2]).not.toBe(firstKey);
  });
});

describe("OrderWorkflow - cambio de destino", () => {
  beforeEach(() => {
    confirmFirstMock.mockReset();
    confirmSubsequentMock.mockReset();
  });

  it("cambia a un Pedido solicitado cuando la Composición está vacía", async () => {
    const onActivate = vi.fn();
    const { rerender } = render(<Harness onActivate={onActivate} />);

    rerender(
      <Harness
        requestedTarget={{ operationalReference: "order-b", sequence: 1 }}
        onActivate={onActivate}
      />,
    );

    expect(
      await screen.findByRole("heading", { name: "Nueva Composición" }),
    ).toBeInTheDocument();
    expect(onActivate).toHaveBeenCalledWith("order-b");
  });

  it("no reinterpreta un borrador y exige discard explícito", async () => {
    const onActivate = vi.fn();
    const user = userEvent.setup();
    const { rerender } = render(<Harness onActivate={onActivate} />);
    await addProduct(user, water, "Composición inicial");

    rerender(
      <Harness
        requestedTarget={{ operationalReference: "order-b", sequence: 1 }}
        onActivate={onActivate}
      />,
    );

    expect(
      await screen.findByText(/no se cambiará de destino/),
    ).toBeInTheDocument();
    expect(
      screen.getByRole("heading", { name: "Composición inicial" }),
    ).toBeInTheDocument();
    expect(onActivate).not.toHaveBeenCalled();

    await user.click(
      screen.getByRole("button", { name: "Descartar Composición" }),
    );
    expect(
      await screen.findByRole("heading", { name: "Nueva Composición" }),
    ).toBeInTheDocument();
    expect(onActivate).toHaveBeenCalledWith("order-b");
    expect(screen.getByText("La Composición está vacía.")).toBeInTheDocument();
  });

  it("exige discard antes de pasar de Pedido activo a Nuevo Pedido", async () => {
    const user = userEvent.setup();
    render(<Harness initialReference="order-active" />);
    await addProduct(user, water, "Nueva Composición");
    await user.click(
      screen.getByRole("button", { name: "Iniciar nuevo Pedido" }),
    );

    expect(
      screen.getByRole("heading", { name: "Nueva Composición" }),
    ).toBeInTheDocument();
    expect(screen.getByText(/no se cambiará de destino/)).toBeInTheDocument();
    await user.click(
      screen.getByRole("button", { name: "Descartar Composición" }),
    );
    expect(
      await screen.findByRole("heading", { name: "Composición inicial" }),
    ).toBeInTheDocument();
  });
});
