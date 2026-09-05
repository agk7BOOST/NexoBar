import { render, screen, waitFor, within } from "@testing-library/react";
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

const {
  confirmFirstMock,
  confirmSubsequentMock,
  getPendingCompositionMock,
  startPendingCompositionMock,
  discardPendingCompositionMock,
  getAntiforgeryTokenMock,
  pendingAuthorityState,
} = vi.hoisted(() => ({
  confirmFirstMock: vi.fn(),
  confirmSubsequentMock: vi.fn(),
  getPendingCompositionMock: vi.fn(),
  startPendingCompositionMock: vi.fn(),
  discardPendingCompositionMock: vi.fn(),
  getAntiforgeryTokenMock: vi.fn(),
  pendingAuthorityState: {
    marker: null as {
      pendingCompositionId: string;
      createdAt: string;
      createdByIdentityId: string;
    } | null,
  },
}));

vi.mock("./orderOperationsClient.ts", async (importOriginal) => {
  const original =
    await importOriginal<typeof import("./orderOperationsClient.ts")>();
  return {
    ...original,
    confirmFirst: confirmFirstMock,
    confirmSubsequent: confirmSubsequentMock,
    getPendingComposition: getPendingCompositionMock,
    startPendingComposition: startPendingCompositionMock,
    discardPendingComposition: discardPendingCompositionMock,
  };
});

vi.mock("../identity/sessionClient.ts", async (importOriginal) => {
  const original =
    await importOriginal<typeof import("../identity/sessionClient.ts")>();
  return { ...original, getAntiforgeryToken: getAntiforgeryTokenMock };
});

beforeEach(() => {
  pendingAuthorityState.marker = null;
  getAntiforgeryTokenMock.mockReset();
  getAntiforgeryTokenMock.mockResolvedValue("csrf-token");
  getPendingCompositionMock.mockReset();
  getPendingCompositionMock.mockImplementation(async (orderId: string) => ({
    orderId,
    pendingComposition: pendingAuthorityState.marker,
  }));
  startPendingCompositionMock.mockReset();
  startPendingCompositionMock.mockImplementation(async () => {
    pendingAuthorityState.marker = {
      pendingCompositionId: "pending-local",
      createdAt: "2026-09-04T12:00:00Z",
      createdByIdentityId: "identity-1",
    };
    return pendingAuthorityState.marker;
  });
  discardPendingCompositionMock.mockReset();
  discardPendingCompositionMock.mockImplementation(async () => {
    pendingAuthorityState.marker = null;
  });
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

it("bloquea la Composición del Pedido congelado y permite iniciar otro Pedido", async () => {
  const onStartNewOrder = vi.fn();
  render(
    <OrderWorkflow
      products={[water]}
      activeOperationalReference="frozen-order"
      onActivateOrder={vi.fn()}
      onStartNewOrder={onStartNewOrder}
      onOrderChanged={vi.fn()}
      onUnauthorized={vi.fn()}
      ordinaryMutationsBlocked
    />,
  );
  await waitFor(() => expect(getPendingCompositionMock).toHaveBeenCalled());
  expect(
    screen.getByRole("button", { name: "Agregar Agua a Nueva Composición" }),
  ).toBeDisabled();
  expect(
    screen.getByRole("button", { name: "Confirmar nueva Incorporación" }),
  ).toBeDisabled();
  await userEvent
    .setup()
    .click(screen.getByRole("button", { name: "Iniciar nuevo Pedido" }));
  expect(onStartNewOrder).toHaveBeenCalledOnce();
});

const burger: Product = {
  ...water,
  id: "product-burger",
  operationalName: "Hamburguesa",
  price: "18.00",
  requiresPreparation: true,
};

const firstResponse: FirstConfirmationResponse = {
  operationalReference: "order-created",
  context: "Mesa 7",
  firstIncorporation: {
    id: "incorporation-1",
    confirmedAt: "2026-08-29T14:30:00Z",
    items: [
      {
        productId: water.id,
        quantity: 2,
        appliedPrice: "10.00",
        instruction: null,
      },
    ],
  },
};

const subsequentResponse: SubsequentConfirmationResponse = {
  operationalReference: "order-active",
  incorporation: {
    id: "incorporation-2",
    ordinal: 2,
    confirmedAt: "2026-08-29T15:00:00Z",
    items: [
      {
        productId: water.id,
        quantity: 2,
        appliedPrice: "12.00",
        instruction: null,
      },
    ],
  },
};

interface HarnessProps {
  initialReference?: string | null;
  requestedTarget?: OrderTargetRequest;
  onActivate?: (reference: string) => void;
  onChanged?: (reference: string) => void;
  onUnauthorized?: () => void;
}

function Harness({
  initialReference = null,
  requestedTarget,
  onActivate = () => undefined,
  onChanged = () => undefined,
  onUnauthorized = () => undefined,
}: HarnessProps) {
  const [activeReference, setActiveReference] = useState(initialReference);

  return (
    <OrderWorkflow
      products={[water, soda, burger]}
      activeOperationalReference={activeReference}
      requestedTarget={requestedTarget}
      onActivateOrder={(reference) => {
        setActiveReference(reference);
        onActivate(reference);
      }}
      onStartNewOrder={() => setActiveReference(null)}
      onOrderChanged={onChanged}
      onUnauthorized={onUnauthorized}
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

it("refresca el Pedido al iniciar y descartar Composición para actualizar elegibilidad de Liquidación", async () => {
  const onChanged = vi.fn();
  const user = userEvent.setup();
  render(<Harness initialReference="order-active" onChanged={onChanged} />);
  await waitFor(() =>
    expect(
      screen.getByRole("button", { name: "Agregar Agua a Nueva Composición" }),
    ).toBeEnabled(),
  );
  await addProduct(user, water, "Nueva Composición");
  expect(onChanged).toHaveBeenCalledWith("order-active");
  onChanged.mockClear();
  await user.click(
    screen.getByRole("button", { name: "Descartar Composición actual" }),
  );
  await waitFor(() => expect(onChanged).toHaveBeenCalledWith("order-active"));
});

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
        name: "Disminuir cantidad de Agua, línea 1",
      }),
    );
    expect(
      within(composition).getByLabelText("Cantidad de Agua"),
    ).toHaveTextContent("1");
    await user.click(
      within(composition).getByRole("button", {
        name: "Disminuir cantidad de Agua, línea 1",
      }),
    );
    expect(
      within(composition).getByText("La Composición está vacía."),
    ).toBeInTheDocument();
  });

  it("agrega normal sobre la línea sin instrucción y crea otra línea editable enfocada", async () => {
    const user = userEvent.setup();
    render(<Harness />);
    await addProduct(user, burger, "Composición inicial", 2);
    await user.click(
      screen.getByRole("button", {
        name: "Agregar otra línea de Hamburguesa a Composición inicial",
      }),
    );

    const secondInstruction = screen.getByLabelText(
      "Instrucción para Hamburguesa, línea 2",
    );
    expect(secondInstruction).toHaveFocus();
    expect(
      screen.getByRole("button", { name: "Confirmar Primera Composición" }),
    ).toBeDisabled();
    expect(screen.getByRole("alert")).toHaveTextContent(/líneas duplicadas/);

    await user.type(secondInstruction, "sin cebolla");
    await addProduct(user, burger, "Composición inicial");

    const normalLine = screen.getByRole("row", {
      name: "Hamburguesa, línea de Composición 1, sin instrucción",
    });
    const instructedLine = screen.getByRole("row", {
      name: "Hamburguesa, línea de Composición 2, sin cebolla",
    });
    expect(
      within(normalLine).getByLabelText("Cantidad de Hamburguesa"),
    ).toHaveTextContent("3");
    expect(
      within(instructedLine).getByLabelText("Cantidad de Hamburguesa"),
    ).toHaveTextContent("1");
  });

  it("opera cantidad y retiro por draftLineId sin afectar otra línea del Product", async () => {
    const user = userEvent.setup();
    render(<Harness />);
    await addProduct(user, burger, "Composición inicial");
    await user.click(
      screen.getByRole("button", {
        name: "Agregar otra línea de Hamburguesa a Composición inicial",
      }),
    );
    await user.type(
      screen.getByLabelText("Instrucción para Hamburguesa, línea 2"),
      "sin tomate",
    );
    await user.click(
      screen.getByRole("button", {
        name: "Aumentar cantidad de Hamburguesa, línea 2",
      }),
    );
    await user.click(
      screen.getByRole("button", {
        name: "Retirar Hamburguesa, línea 1, de la composición",
      }),
    );

    const remainingLine = screen.getByRole("row", {
      name: "Hamburguesa, línea de Composición 1, sin tomate",
    });
    expect(
      within(remainingLine).getByLabelText("Cantidad de Hamburguesa"),
    ).toHaveTextContent("2");
    expect(
      screen.queryByPlaceholderText("Sin instrucción", { exact: true }),
    ).toHaveValue("sin tomate");
  });

  it("permite colisión durante edición y bloquea localmente duplicados canonical", async () => {
    const user = userEvent.setup();
    render(<Harness />);
    await user.type(screen.getByLabelText("Contexto"), "Mesa 7");
    await addProduct(user, burger, "Composición inicial");
    await user.type(
      screen.getByLabelText("Instrucción para Hamburguesa, línea 1"),
      "sin cebolla",
    );
    await user.click(
      screen.getByRole("button", {
        name: "Agregar otra línea de Hamburguesa a Composición inicial",
      }),
    );
    await user.type(
      screen.getByLabelText("Instrucción para Hamburguesa, línea 2"),
      "  sin cebolla  ",
    );

    expect(screen.getByRole("alert")).toHaveTextContent(/líneas duplicadas/);
    expect(
      screen.getByRole("button", { name: "Confirmar Primera Composición" }),
    ).toBeDisabled();
    expect(confirmFirstMock).not.toHaveBeenCalled();
  });

  it("aumenta y retira entradas sin producir Confirmaciones", async () => {
    const user = userEvent.setup();
    render(<Harness />);
    await addProduct(user, water, "Composición inicial");

    await user.click(
      screen.getByRole("button", {
        name: "Aumentar cantidad de Agua, línea 1",
      }),
    );
    expect(screen.getByLabelText("Cantidad de Agua")).toHaveTextContent("2");
    await user.click(
      screen.getByRole("button", {
        name: "Retirar Agua, línea 1, de la composición",
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
        onUnauthorized={() => undefined}
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
      items: [{ productId: water.id, quantity: 2, instruction: null }],
    });
    expect(key).toMatch(
      /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i,
    );
    expect(onActivate).toHaveBeenCalledWith("order-created");
    expect(onChanged).toHaveBeenCalledWith("order-created");
    expect(screen.getByText("La Composición está vacía.")).toBeInTheDocument();
    expect(screen.queryByLabelText("Contexto")).not.toBeInTheDocument();
  });

  it("envía líneas homogéneas con instruction canonical y nunca draftLineId", async () => {
    confirmFirstMock.mockResolvedValueOnce(firstResponse);
    const user = userEvent.setup();
    render(<Harness />);
    await user.type(screen.getByLabelText("Contexto"), "Mesa 8");
    await addProduct(user, burger, "Composición inicial");
    await user.type(
      screen.getByLabelText("Instrucción para Hamburguesa, línea 1"),
      "   ",
    );
    await user.click(
      screen.getByRole("button", {
        name: "Agregar otra línea de Hamburguesa a Composición inicial",
      }),
    );
    await user.type(
      screen.getByLabelText("Instrucción para Hamburguesa, línea 2"),
      "  SIN  cebolla!  ",
    );

    await user.click(
      screen.getByRole("button", { name: "Confirmar Primera Composición" }),
    );

    const [request] = confirmFirstMock.mock.calls[0]!;
    expect(request).toEqual({
      context: "Mesa 8",
      items: [
        { productId: burger.id, quantity: 1, instruction: null },
        {
          productId: burger.id,
          quantity: 1,
          instruction: "SIN  cebolla!",
        },
      ],
    });
    expect(JSON.stringify(request)).not.toContain("draftLineId");
  });

  it.each([
    [
      "order_operations.confirmation.instruction_requires_preparation",
      /solo puede confirmarse para un Producto que requiere preparación/,
    ],
    ["order_operations.confirmation.duplicate_line", /misma instrucción/],
  ])(
    "trata %s como conflicto conocido, conserva edición y usa una key nueva",
    async (code, expectedMessage) => {
      const problem = new OrderOperationsProblemError({
        status: 409,
        code,
        productId: burger.id,
      });
      confirmFirstMock
        .mockRejectedValueOnce(problem)
        .mockRejectedValueOnce(problem);
      const user = userEvent.setup();
      render(<Harness />);
      await user.type(screen.getByLabelText("Contexto"), "Mesa 7");
      await addProduct(user, burger, "Composición inicial");
      const instruction = screen.getByLabelText(
        "Instrucción para Hamburguesa, línea 1",
      );
      await user.type(instruction, "sin cebolla");

      await user.click(
        screen.getByRole("button", { name: "Confirmar Primera Composición" }),
      );
      expect(await screen.findByText(expectedMessage)).toBeInTheDocument();
      expect(instruction).toBeEnabled();
      expect(instruction).toHaveValue("sin cebolla");
      const firstKey = confirmFirstMock.mock.calls[0]?.[1];

      await user.click(
        screen.getByRole("button", { name: "Confirmar Primera Composición" }),
      );
      await vi.waitFor(() => expect(confirmFirstMock).toHaveBeenCalledTimes(2));
      expect(confirmFirstMock.mock.calls[1]?.[1]).not.toBe(firstKey);
    },
  );

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

  it("congela instruction canonical en incertidumbre First y reintenta request/key exactos", async () => {
    confirmFirstMock.mockRejectedValueOnce(new OrderOperationsNetworkError());
    const user = userEvent.setup();
    render(<Harness />);
    await user.type(screen.getByLabelText("Contexto"), "Mesa 7");
    await addProduct(user, burger, "Composición inicial");
    const instruction = screen.getByLabelText(
      "Instrucción para Hamburguesa, línea 1",
    );
    await user.type(instruction, "  sin cebolla  ");
    await user.click(
      screen.getByRole("button", { name: "Confirmar Primera Composición" }),
    );

    const uncertain = await screen.findByRole("region", {
      name: "Primera Confirmación con resultado no confirmado",
    });
    expect(uncertain).toHaveTextContent("Instrucción: sin cebolla");
    expect(instruction).toBeDisabled();
    const firstCall = confirmFirstMock.mock.calls[0];
    expect(firstCall?.[0].items[0]?.instruction).toBe("sin cebolla");

    confirmFirstMock.mockResolvedValueOnce(firstResponse);
    await user.click(
      screen.getByRole("button", {
        name: "Reintentar misma Primera Confirmación",
      }),
    );
    await screen.findByRole("heading", { name: "Nueva Composición" });
    expect(confirmFirstMock.mock.calls[1]).toEqual(firstCall);
  });

  it("no permite abandonar una Primera Confirmación incierta ni generar otra key", async () => {
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
    const firstCall = confirmFirstMock.mock.calls[0];
    expect(
      screen.queryByRole("button", { name: "Descartar intención incierta" }),
    ).not.toBeInTheDocument();
    expect(screen.getByLabelText("Cantidad de Agua")).toHaveTextContent("1");
    expect(screen.getByLabelText("Contexto")).toBeDisabled();
    confirmFirstMock.mockResolvedValueOnce(firstResponse);
    await user.click(
      screen.getByRole("button", {
        name: "Reintentar misma Primera Confirmación",
      }),
    );
    await screen.findByRole("heading", { name: "Nueva Composición" });
    expect(confirmFirstMock.mock.calls[1]).toEqual(firstCall);
  });

  it("mantiene instruction congelada durante incertidumbre First", async () => {
    confirmFirstMock.mockRejectedValueOnce(new OrderOperationsNetworkError());
    const user = userEvent.setup();
    render(<Harness />);
    await user.type(screen.getByLabelText("Contexto"), "Mesa 7");
    await addProduct(user, burger, "Composición inicial");
    const instruction = screen.getByLabelText(
      "Instrucción para Hamburguesa, línea 1",
    );
    await user.type(instruction, "sin cebolla");
    await user.click(
      screen.getByRole("button", { name: "Confirmar Primera Composición" }),
    );
    await screen.findByRole("region", {
      name: "Primera Confirmación con resultado no confirmado",
    });
    expect(instruction).toBeDisabled();
    expect(instruction).toHaveValue("sin cebolla");
    expect(confirmFirstMock).toHaveBeenCalledOnce();
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
      pendingCompositionId: "pending-local",
      items: [
        { productId: soda.id, quantity: 1, instruction: null },
        { productId: water.id, quantity: 2, instruction: null },
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

  it("envía Subsequent exacta con múltiples líneas e instructions canonical", async () => {
    confirmSubsequentMock.mockResolvedValueOnce(subsequentResponse);
    const user = userEvent.setup();
    render(<Harness initialReference="order-active" />);
    await addProduct(user, burger, "Nueva Composición", 2);
    await user.click(
      screen.getByRole("button", {
        name: "Agregar otra línea de Hamburguesa a Nueva Composición",
      }),
    );
    await user.type(
      screen.getByLabelText("Instrucción para Hamburguesa, línea 2"),
      "  sin tomate  ",
    );

    await user.click(
      screen.getByRole("button", { name: "Confirmar nueva Incorporación" }),
    );
    await screen.findByText("Nueva Incorporación confirmada correctamente.");

    expect(confirmSubsequentMock.mock.calls[0]?.[0]).toBe("order-active");
    expect(confirmSubsequentMock.mock.calls[0]?.[1]).toEqual({
      pendingCompositionId: "pending-local",
      items: [
        { productId: burger.id, quantity: 2, instruction: null },
        { productId: burger.id, quantity: 1, instruction: "sin tomate" },
      ],
    });
    expect(
      JSON.stringify(confirmSubsequentMock.mock.calls[0]?.[1]),
    ).not.toContain("draftLineId");
  });

  it("un conflicto conocido Subsequent conserva lines editables y no deja incertidumbre", async () => {
    confirmSubsequentMock.mockRejectedValueOnce(
      new OrderOperationsProblemError({
        status: 409,
        code: "order_operations.confirmation.instruction_requires_preparation",
        productId: water.id,
      }),
    );
    const user = userEvent.setup();
    render(<Harness initialReference="order-active" />);
    await addProduct(user, water, "Nueva Composición");
    const instruction = screen.getByLabelText("Instrucción para Agua, línea 1");
    await user.type(instruction, "con hielo");
    await user.click(
      screen.getByRole("button", { name: "Confirmar nueva Incorporación" }),
    );

    expect(
      await screen.findByText(/solo puede confirmarse para un Producto/),
    ).toBeInTheDocument();
    expect(instruction).toBeEnabled();
    expect(instruction).toHaveValue("con hielo");
    expect(
      screen.queryByRole("region", {
        name: "Confirmación posterior con resultado no confirmado",
      }),
    ).not.toBeInTheDocument();
  });

  it("congela reference/items/key, bloquea edición/destino y reintenta igual", async () => {
    confirmSubsequentMock.mockRejectedValueOnce(
      new OrderOperationsNetworkError(),
    );
    const user = userEvent.setup();
    render(<Harness initialReference="order-active" />);
    await addProduct(user, burger, "Nueva Composición", 2);
    const instruction = screen.getByLabelText(
      "Instrucción para Hamburguesa, línea 1",
    );
    await user.type(instruction, "  sin cebolla  ");
    await user.click(
      screen.getByRole("button", { name: "Confirmar nueva Incorporación" }),
    );

    const uncertain = await screen.findByRole("region", {
      name: "Confirmación posterior con resultado no confirmado",
    });
    expect(uncertain).toHaveTextContent("order-active");
    expect(uncertain).toHaveTextContent("Cantidad: 2");
    expect(uncertain).toHaveTextContent("Instrucción: sin cebolla");
    expect(instruction).toBeDisabled();
    expect(
      screen.getByRole("button", {
        name: "Agregar Hamburguesa a Nueva Composición",
      }),
    ).toBeDisabled();
    const firstCall = confirmSubsequentMock.mock.calls[0];
    expect(firstCall?.[0]).toBe("order-active");
    expect(firstCall?.[1].items[0]?.instruction).toBe("sin cebolla");

    expect(
      screen.getByRole("button", { name: "Iniciar nuevo Pedido" }),
    ).toBeDisabled();
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

  it("no permite abandonar incertidumbre Subsequent y reintenta ID/key/body exactos", async () => {
    confirmSubsequentMock.mockRejectedValueOnce(
      new OrderOperationsNetworkError(),
    );
    const user = userEvent.setup();
    render(<Harness initialReference="order-active" />);
    await addProduct(user, burger, "Nueva Composición");
    const instruction = screen.getByLabelText(
      "Instrucción para Hamburguesa, línea 1",
    );
    await user.type(instruction, "sin cebolla");
    await user.click(
      screen.getByRole("button", { name: "Confirmar nueva Incorporación" }),
    );
    await screen.findByRole("region", {
      name: "Confirmación posterior con resultado no confirmado",
    });
    const firstCall = confirmSubsequentMock.mock.calls[0];
    expect(instruction).toBeDisabled();
    expect(
      screen.queryByRole("button", { name: "Descartar intención incierta" }),
    ).not.toBeInTheDocument();
    confirmSubsequentMock.mockResolvedValueOnce(subsequentResponse);
    await user.click(
      screen.getByRole("button", {
        name: "Reintentar misma Confirmación posterior",
      }),
    );
    await screen.findByText("Nueva Incorporación confirmada correctamente.");
    expect(confirmSubsequentMock.mock.calls[1]).toEqual(firstCall);
  });
});

describe("OrderWorkflow - autoridad de Composición pendiente", () => {
  beforeEach(() => {
    confirmFirstMock.mockReset();
    confirmSubsequentMock.mockReset();
  });

  it("mantiene la primera Composición local y nunca inicia un marcador servidor", async () => {
    const user = userEvent.setup();
    render(<Harness />);

    await addProduct(user, water, "Composición inicial");

    expect(startPendingCompositionMock).not.toHaveBeenCalled();
    expect(screen.getByLabelText("Cantidad de Agua")).toHaveTextContent("1");
  });

  it("no inicia al visualizar y establece autoridad antes del primer ítem posterior", async () => {
    const user = userEvent.setup();
    render(<Harness initialReference="order-active" />);
    await waitFor(() => expect(getPendingCompositionMock).toHaveBeenCalled());
    expect(startPendingCompositionMock).not.toHaveBeenCalled();

    await addProduct(user, water, "Nueva Composición");

    expect(startPendingCompositionMock).toHaveBeenCalledOnce();
    expect(startPendingCompositionMock.mock.calls[0]?.[0]).toMatchObject({
      orderId: "order-active",
      antiforgeryToken: "csrf-token",
    });
    expect(
      startPendingCompositionMock.mock.calls[0]?.[0].idempotencyKey,
    ).toMatch(/^[0-9a-f-]{36}$/i);
    expect(screen.getByText("pending-local")).toBeInTheDocument();
    expect(screen.getByLabelText("Cantidad de Agua")).toHaveTextContent("1");
  });

  it("expone un marcador remoto sin inventar líneas, bloquea otro draft y permite descarte explícito", async () => {
    pendingAuthorityState.marker = {
      pendingCompositionId: "pending-remote",
      createdAt: "2026-09-04T12:00:00Z",
      createdByIdentityId: "identity-remote",
    };
    const user = userEvent.setup();
    render(<Harness initialReference="order-active" />);

    expect(await screen.findByText("pending-remote")).toBeInTheDocument();
    expect(screen.getByText("La Composición está vacía.")).toBeInTheDocument();
    await addProduct(user, water, "Nueva Composición");
    expect(startPendingCompositionMock).not.toHaveBeenCalled();
    expect(
      await screen.findAllByText(/líneas no están disponibles/),
    ).toHaveLength(2);

    await user.click(
      screen.getByRole("button", { name: "Descartar Composición pendiente" }),
    );
    await waitFor(() =>
      expect(discardPendingCompositionMock).toHaveBeenCalledOnce(),
    );
    expect(discardPendingCompositionMock.mock.calls[0]?.[0]).toMatchObject({
      orderId: "order-active",
      pendingCompositionId: "pending-remote",
      antiforgeryToken: "csrf-token",
    });
    expect(startPendingCompositionMock).not.toHaveBeenCalled();

    await addProduct(user, water, "Nueva Composición");
    expect(startPendingCompositionMock).toHaveBeenCalledOnce();
  });

  it("reintenta un Start incierto con endpoint, acción, token y key idénticos", async () => {
    startPendingCompositionMock.mockRejectedValueOnce(
      new OrderOperationsNetworkError(),
    );
    const user = userEvent.setup();
    render(<Harness initialReference="order-active" />);
    await screen.findByText("La Composición está vacía.");

    await addProduct(user, water, "Nueva Composición");
    const uncertain = await screen.findByRole("region", {
      name: "Inicio de Composición con resultado incierto",
    });
    expect(screen.queryByLabelText("Cantidad de Agua")).not.toBeInTheDocument();
    const original = startPendingCompositionMock.mock.calls[0];

    await user.click(
      within(uncertain).getByRole("button", {
        name: "Reintentar mismo inicio",
      }),
    );
    await screen.findByLabelText("Cantidad de Agua");
    expect(startPendingCompositionMock.mock.calls[1]).toEqual(original);
  });

  it("reintenta un Discard incierto con marcador, token y key idénticos", async () => {
    pendingAuthorityState.marker = {
      pendingCompositionId: "pending-remote",
      createdAt: "2026-09-04T12:00:00Z",
      createdByIdentityId: "identity-remote",
    };
    discardPendingCompositionMock.mockRejectedValueOnce(
      new OrderOperationsNetworkError(),
    );
    const user = userEvent.setup();
    render(<Harness initialReference="order-active" />);
    await screen.findByText("pending-remote");

    await user.click(
      screen.getByRole("button", { name: "Descartar Composición pendiente" }),
    );
    const uncertain = await screen.findByRole("region", {
      name: "Descarte de Composición con resultado incierto",
    });
    const original = discardPendingCompositionMock.mock.calls[0];
    await user.click(
      within(uncertain).getByRole("button", {
        name: "Reintentar mismo descarte",
      }),
    );
    await waitFor(() =>
      expect(discardPendingCompositionMock).toHaveBeenCalledTimes(2),
    );
    expect(discardPendingCompositionMock.mock.calls[1]).toEqual(original);
  });

  it("no abandona un marcador autoritativo aunque ya no queden líneas locales", async () => {
    const user = userEvent.setup();
    render(<Harness initialReference="order-active" />);
    await addProduct(user, water, "Nueva Composición");
    await user.click(
      screen.getByRole("button", {
        name: "Retirar Agua, línea 1, de la composición",
      }),
    );
    expect(screen.getByText("La Composición está vacía.")).toBeInTheDocument();

    const newOrder = screen.getByRole("button", {
      name: "Iniciar nuevo Pedido",
    });
    await waitFor(() => expect(newOrder).toBeEnabled());
    await user.click(newOrder);
    expect(screen.getByText(/Descartala explícitamente/)).toBeInTheDocument();
    await user.click(
      screen.getByRole("button", { name: "Descartar Composición" }),
    );

    await waitFor(() =>
      expect(discardPendingCompositionMock).toHaveBeenCalledOnce(),
    );
    expect(
      await screen.findByRole("heading", { name: "Composición inicial" }),
    ).toBeInTheDocument();
  });

  it("401 aplica logout/reset y 403 conserva la Identidad con explicación", async () => {
    const onUnauthorized = vi.fn();
    getPendingCompositionMock.mockRejectedValueOnce(
      new OrderOperationsProblemError({ status: 401 }),
    );
    const first = render(
      <Harness
        initialReference="order-active"
        onUnauthorized={onUnauthorized}
      />,
    );
    await waitFor(() => expect(onUnauthorized).toHaveBeenCalledOnce());
    first.unmount();

    getPendingCompositionMock.mockRejectedValueOnce(
      new OrderOperationsProblemError({ status: 403 }),
    );
    render(
      <Harness
        initialReference="order-active"
        onUnauthorized={onUnauthorized}
      />,
    );
    expect(
      await screen.findByText(
        /no tiene autorización para operar Composiciones/,
      ),
    ).toBeInTheDocument();
    expect(onUnauthorized).toHaveBeenCalledOnce();
  });

  it("un 409 stale conserva líneas sólo para revisión y nunca cambia el ID", async () => {
    confirmSubsequentMock.mockImplementationOnce(async () => {
      pendingAuthorityState.marker = null;
      throw new OrderOperationsProblemError({
        status: 409,
        code: "order.pending_composition_stale",
      });
    });
    const user = userEvent.setup();
    render(<Harness initialReference="order-active" />);
    await addProduct(user, water, "Nueva Composición");
    await user.click(
      screen.getByRole("button", { name: "Confirmar nueva Incorporación" }),
    );

    expect(
      await screen.findByText(/no se reenviará automáticamente/),
    ).toBeInTheDocument();
    expect(screen.getByLabelText("Cantidad de Agua")).toHaveTextContent("1");
    expect(
      screen.getByRole("button", {
        name: "Aumentar cantidad de Agua, línea 1",
      }),
    ).toBeDisabled();
    expect(startPendingCompositionMock).toHaveBeenCalledOnce();
    expect(confirmSubsequentMock).toHaveBeenCalledOnce();
    expect(
      screen.getByRole("button", {
        name: "Descartar borrador local desvinculado",
      }),
    ).toBeInTheDocument();
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
