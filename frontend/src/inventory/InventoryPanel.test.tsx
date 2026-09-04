import {
  fireEvent,
  render,
  screen,
  waitFor,
  within,
} from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";
import {
  discardAntiforgeryToken,
  getAntiforgeryToken,
} from "../identity/sessionClient.ts";
import { InventoryPanel } from "./InventoryPanel.tsx";
import {
  createInventoryItem,
  getInventoryMovementHistory,
  InventoryNetworkError,
  InventoryProblemError,
  listInventoryConfigurationItems,
  listInventoryOperationalItems,
  recordInventoryEntry,
  recordManualInventoryExit,
  type InventoryConfigurationItem,
  type InventoryOperationalItem,
} from "./inventoryClient.ts";

vi.mock("../identity/sessionClient.ts", async (importOriginal) => {
  const original =
    await importOriginal<typeof import("../identity/sessionClient.ts")>();
  return {
    ...original,
    discardAntiforgeryToken: vi.fn(),
    getAntiforgeryToken: vi.fn(),
  };
});

vi.mock("./inventoryClient.ts", async (importOriginal) => {
  const original =
    await importOriginal<typeof import("./inventoryClient.ts")>();
  return {
    ...original,
    createInventoryItem: vi.fn(),
    getInventoryMovementHistory: vi.fn(),
    listInventoryConfigurationItems: vi.fn(),
    listInventoryOperationalItems: vi.fn(),
    recordInventoryEntry: vi.fn(),
    recordManualInventoryExit: vi.fn(),
  };
});

const configurationItem: InventoryConfigurationItem = {
  itemId: "item-1",
  operationalName: "Harina",
  operationalUnit: "kg",
};

const uninitialized: InventoryOperationalItem = {
  ...configurationItem,
  currentRegisteredQuantity: null,
  quantityEstablished: false,
  hasNegativeBalanceInconsistency: false,
  asOfMovementRevision: 0,
};

const forbidden = new InventoryProblemError(403, {
  code: "inventory.forbidden",
});

const firstKey =
  "11111111-1111-4111-8111-111111111111" as `${string}-${string}-${string}-${string}-${string}`;

function deferred<T>() {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>((resolvePromise) => {
    resolve = resolvePromise;
  });
  return { promise, resolve };
}

async function renderConfigurationOnly(onUnauthorized = vi.fn()) {
  vi.mocked(listInventoryConfigurationItems).mockResolvedValueOnce([
    configurationItem,
  ]);
  vi.mocked(listInventoryOperationalItems).mockRejectedValueOnce(forbidden);
  render(<InventoryPanel onUnauthorized={onUnauthorized} />);
  await screen.findByRole("button", { name: "Crear elemento" });
  return onUnauthorized;
}

describe("InventoryPanel", () => {
  beforeEach(() => {
    vi.mocked(discardAntiforgeryToken).mockReset();
    vi.mocked(getAntiforgeryToken).mockReset().mockResolvedValue("csrf-1");
    vi.mocked(createInventoryItem).mockReset();
    vi.mocked(getInventoryMovementHistory).mockReset();
    vi.mocked(listInventoryConfigurationItems).mockReset();
    vi.mocked(listInventoryOperationalItems).mockReset();
    vi.mocked(recordInventoryEntry).mockReset();
    vi.mocked(recordManualInventoryExit).mockReset();
  });

  it.each([
    { configuration: true, operation: false, label: "Configuration only" },
    { configuration: false, operation: true, label: "Operation only" },
    { configuration: true, operation: true, label: "both" },
    { configuration: false, operation: false, label: "neither" },
  ])(
    "keeps capability boundaries for $label",
    async ({ configuration, operation }) => {
      if (configuration) {
        vi.mocked(listInventoryConfigurationItems).mockResolvedValueOnce([
          configurationItem,
        ]);
      } else {
        vi.mocked(listInventoryConfigurationItems).mockRejectedValueOnce(
          forbidden,
        );
      }
      if (operation) {
        vi.mocked(listInventoryOperationalItems).mockResolvedValueOnce([
          uninitialized,
        ]);
      } else {
        vi.mocked(listInventoryOperationalItems).mockRejectedValueOnce(
          forbidden,
        );
      }

      render(<InventoryPanel onUnauthorized={vi.fn()} />);
      await waitFor(() => {
        expect(listInventoryConfigurationItems).toHaveBeenCalledTimes(1);
        expect(listInventoryOperationalItems).toHaveBeenCalledTimes(1);
      });

      if (configuration) {
        expect(
          await screen.findByRole("button", { name: "Crear elemento" }),
        ).toBeInTheDocument();
        expect(
          screen.getByRole("link", { name: "Configuración" }),
        ).toBeInTheDocument();
      } else {
        expect(
          await screen.findByText(
            "Esta Identity no tiene autorización para configurar Inventario.",
          ),
        ).toBeInTheDocument();
        expect(
          screen.queryByRole("button", { name: "Crear elemento" }),
        ).not.toBeInTheDocument();
      }

      if (operation) {
        expect(
          await screen.findByRole("button", {
            name: "Ver movimientos de Harina",
          }),
        ).toBeInTheDocument();
        expect(
          screen.getByRole("link", { name: "Operación" }),
        ).toBeInTheDocument();
      } else {
        expect(
          await screen.findByText(
            "Esta Identity no tiene autorización para operar Inventario.",
          ),
        ).toBeInTheDocument();
        expect(
          screen.queryByRole("button", { name: /Ver movimientos de/ }),
        ).not.toBeInTheDocument();
      }
      expect(screen.queryByRole("combobox")).not.toBeInTheDocument();
    },
  );

  it("shows the Configuration list and minimal create form without stock or Product fields", async () => {
    await renderConfigurationOnly();

    expect(screen.getByText("Harina")).toBeInTheDocument();
    expect(screen.getByText("kg")).toBeInTheDocument();
    expect(screen.getByLabelText("Nombre operacional")).toBeInTheDocument();
    expect(screen.getByLabelText("Unidad operacional")).toHaveProperty(
      "type",
      "text",
    );
    expect(
      screen.getByText(
        "La existencia se establecerá después mediante un conteo.",
      ),
    ).toBeInTheDocument();
    expect(
      screen.queryByLabelText(/stock|cantidad inicial|producto/i),
    ).not.toBeInTheDocument();
    expect(screen.queryByRole("combobox")).not.toBeInTheDocument();
  });

  it("freezes trimmed creation data and key, prevents duplicate submit, then refreshes", async () => {
    vi.spyOn(crypto, "randomUUID").mockReturnValue(firstKey);
    vi.mocked(listInventoryConfigurationItems)
      .mockResolvedValueOnce([configurationItem])
      .mockResolvedValueOnce([
        configurationItem,
        {
          itemId: "item-2",
          operationalName: "Azúcar",
          operationalUnit: "kg",
        },
      ]);
    vi.mocked(listInventoryOperationalItems).mockRejectedValueOnce(forbidden);
    const post = deferred<{
      itemId: string;
      operationalName: string;
      operationalUnit: string;
      currentRegisteredQuantity: null;
      movementRevision: number;
    }>();
    vi.mocked(createInventoryItem).mockReturnValueOnce(post.promise);
    const user = userEvent.setup();
    render(<InventoryPanel onUnauthorized={vi.fn()} />);
    await screen.findByRole("button", { name: "Crear elemento" });

    await user.type(screen.getByLabelText("Nombre operacional"), "  Azúcar  ");
    await user.type(screen.getByLabelText("Unidad operacional"), "  kg  ");
    await user.click(screen.getByRole("button", { name: "Crear elemento" }));

    expect(createInventoryItem).toHaveBeenCalledWith(
      { operationalName: "Azúcar", operationalUnit: "kg" },
      firstKey,
      "csrf-1",
    );
    const form = screen.getByLabelText("Nombre operacional").closest("form")!;
    fireEvent.submit(form);
    expect(createInventoryItem).toHaveBeenCalledTimes(1);
    expect(screen.getByLabelText("Nombre operacional")).toBeDisabled();

    post.resolve({
      itemId: "item-2",
      operationalName: "Azúcar",
      operationalUnit: "kg",
      currentRegisteredQuantity: null,
      movementRevision: 0,
    });

    expect(
      await screen.findByText("Elemento de Inventario creado correctamente."),
    ).toBeInTheDocument();
    expect(await screen.findByText("Azúcar")).toBeInTheDocument();
    expect(screen.getByLabelText("Nombre operacional")).toHaveValue("");
    expect(listInventoryConfigurationItems).toHaveBeenCalledTimes(2);
  });

  it("retains the exact uncertain intent and retries without a new key", async () => {
    vi.spyOn(crypto, "randomUUID").mockReturnValue(firstKey);
    vi.mocked(createInventoryItem)
      .mockRejectedValueOnce(new InventoryNetworkError())
      .mockResolvedValueOnce({
        itemId: "item-2",
        operationalName: "Azúcar",
        operationalUnit: "kg",
        currentRegisteredQuantity: null,
        movementRevision: 0,
      });
    vi.mocked(listInventoryConfigurationItems)
      .mockResolvedValueOnce([configurationItem])
      .mockResolvedValueOnce([configurationItem]);
    vi.mocked(listInventoryOperationalItems).mockRejectedValueOnce(forbidden);
    const user = userEvent.setup();
    render(<InventoryPanel onUnauthorized={vi.fn()} />);
    await screen.findByRole("button", { name: "Crear elemento" });
    await user.type(screen.getByLabelText("Nombre operacional"), "Azúcar");
    await user.type(screen.getByLabelText("Unidad operacional"), "kg");
    await user.click(screen.getByRole("button", { name: "Crear elemento" }));

    expect(
      await screen.findByText(
        "No se pudo confirmar si el elemento fue creado.",
      ),
    ).toBeInTheDocument();
    expect(screen.getByLabelText("Nombre operacional")).toBeDisabled();
    expect(
      screen.queryByRole("button", { name: /Descartar/ }),
    ).not.toBeInTheDocument();
    const originalCall = vi.mocked(createInventoryItem).mock.calls[0];

    await user.click(screen.getByRole("button", { name: "Reintentar" }));
    await waitFor(() => expect(createInventoryItem).toHaveBeenCalledTimes(2));
    expect(vi.mocked(createInventoryItem).mock.calls[1]).toEqual(originalCall);
    expect(crypto.randomUUID).toHaveBeenCalledTimes(1);
  });

  it("treats 409 as known, clears the intent and refreshes Configuration", async () => {
    vi.mocked(createInventoryItem).mockRejectedValueOnce(
      new InventoryProblemError(409, {
        code: "inventory.item.operational_name_conflict",
      }),
    );
    vi.mocked(listInventoryConfigurationItems)
      .mockResolvedValueOnce([configurationItem])
      .mockResolvedValueOnce([configurationItem]);
    vi.mocked(listInventoryOperationalItems).mockRejectedValueOnce(forbidden);
    const user = userEvent.setup();
    render(<InventoryPanel onUnauthorized={vi.fn()} />);
    await screen.findByRole("button", { name: "Crear elemento" });
    await user.type(screen.getByLabelText("Nombre operacional"), "Harina");
    await user.type(screen.getByLabelText("Unidad operacional"), "kg");
    await user.click(screen.getByRole("button", { name: "Crear elemento" }));

    expect(
      await screen.findByText(
        "Ya existe un elemento de Inventario con ese nombre.",
      ),
    ).toBeInTheDocument();
    await waitFor(() =>
      expect(listInventoryConfigurationItems).toHaveBeenCalledTimes(2),
    );
    expect(
      screen.getByRole("button", { name: "Crear elemento" }),
    ).toBeEnabled();
  });

  it("treats a 400 validation response as known and permits a corrected intention", async () => {
    vi.mocked(createInventoryItem).mockRejectedValueOnce(
      new InventoryProblemError(400, {
        code: "inventory.item.operational_name_invalid",
      }),
    );
    const user = userEvent.setup();
    await renderConfigurationOnly();
    await user.type(screen.getByLabelText("Nombre operacional"), "Inválido");
    await user.type(screen.getByLabelText("Unidad operacional"), "kg");
    await user.click(screen.getByRole("button", { name: "Crear elemento" }));

    expect(
      await screen.findByText("Ingresá un nombre operacional válido."),
    ).toBeInTheDocument();
    expect(
      screen.getByRole("button", { name: "Crear elemento" }),
    ).toBeEnabled();
    expect(
      screen.queryByRole("button", { name: "Reintentar" }),
    ).not.toBeInTheDocument();
  });

  it("treats a create 5xx as uncertain", async () => {
    vi.mocked(createInventoryItem).mockRejectedValueOnce(
      new InventoryProblemError(500, {
        code: "inventory.internal_error",
      }),
    );
    const user = userEvent.setup();
    await renderConfigurationOnly();
    await user.type(screen.getByLabelText("Nombre operacional"), "Azúcar");
    await user.type(screen.getByLabelText("Unidad operacional"), "kg");
    await user.click(screen.getByRole("button", { name: "Crear elemento" }));

    expect(
      await screen.findByText(
        "No se pudo confirmar si el elemento fue creado.",
      ),
    ).toBeInTheDocument();
    expect(
      screen.getByRole("button", { name: "Reintentar" }),
    ).toBeInTheDocument();
  });

  it("returns to login and clears antiforgery and create intent on 401", async () => {
    vi.mocked(createInventoryItem).mockRejectedValueOnce(
      new InventoryProblemError(401),
    );
    const onUnauthorized = vi.fn();
    const user = userEvent.setup();
    await renderConfigurationOnly(onUnauthorized);
    await user.type(screen.getByLabelText("Nombre operacional"), "Azúcar");
    await user.type(screen.getByLabelText("Unidad operacional"), "kg");
    await user.click(screen.getByRole("button", { name: "Crear elemento" }));

    await waitFor(() => expect(onUnauthorized).toHaveBeenCalledTimes(1));
    expect(discardAntiforgeryToken).toHaveBeenCalled();
    expect(
      screen.queryByRole("button", { name: "Reintentar" }),
    ).not.toBeInTheDocument();
  });

  it("returns to login once when Inventory reads report 401", async () => {
    vi.mocked(listInventoryConfigurationItems).mockRejectedValueOnce(
      new InventoryProblemError(401),
    );
    vi.mocked(listInventoryOperationalItems).mockRejectedValueOnce(
      new InventoryProblemError(401),
    );
    const onUnauthorized = vi.fn();
    render(<InventoryPanel onUnauthorized={onUnauthorized} />);

    await waitFor(() => expect(onUnauthorized).toHaveBeenCalledTimes(1));
    expect(discardAntiforgeryToken).toHaveBeenCalledTimes(1);
  });

  it("keeps the Identity on create 403 and removes Configuration actions", async () => {
    vi.mocked(createInventoryItem).mockRejectedValueOnce(
      new InventoryProblemError(403),
    );
    const onUnauthorized = vi.fn();
    const user = userEvent.setup();
    await renderConfigurationOnly(onUnauthorized);
    await user.type(screen.getByLabelText("Nombre operacional"), "Azúcar");
    await user.type(screen.getByLabelText("Unidad operacional"), "kg");
    await user.click(screen.getByRole("button", { name: "Crear elemento" }));

    expect(
      await screen.findAllByText(
        "Esta Identity no tiene autorización para configurar Inventario.",
      ),
    ).not.toHaveLength(0);
    expect(onUnauthorized).not.toHaveBeenCalled();
    expect(
      screen.queryByRole("button", { name: "Crear elemento" }),
    ).not.toBeInTheDocument();
  });

  it("presents uninitialized, established zero, positive and negative State distinctly", async () => {
    vi.mocked(listInventoryConfigurationItems).mockRejectedValueOnce(forbidden);
    vi.mocked(listInventoryOperationalItems).mockResolvedValueOnce([
      uninitialized,
      {
        ...uninitialized,
        itemId: "zero",
        operationalName: "Cero",
        currentRegisteredQuantity: "0",
        quantityEstablished: true,
        asOfMovementRevision: 1,
      },
      {
        ...uninitialized,
        itemId: "positive",
        operationalName: "Positivo",
        currentRegisteredQuantity: "10.500",
        quantityEstablished: true,
        asOfMovementRevision: 2,
      },
      {
        ...uninitialized,
        itemId: "negative",
        operationalName: "Negativo",
        currentRegisteredQuantity: "-2.250",
        quantityEstablished: true,
        hasNegativeBalanceInconsistency: true,
        asOfMovementRevision: 3,
      },
    ]);
    render(<InventoryPanel onUnauthorized={vi.fn()} />);

    const noCount = await screen.findByRole("article", { name: "Harina" });
    expect(noCount).toHaveTextContent("Existencia no establecida");
    expect(noCount).not.toHaveTextContent("0 kg");
    expect(noCount).toHaveTextContent("Requiere conteo y reconciliación.");
    expect(screen.getByRole("article", { name: "Cero" })).toHaveTextContent(
      "0 kg",
    );
    expect(screen.getByRole("article", { name: "Positivo" })).toHaveTextContent(
      "10.500 kg",
    );
    const negative = screen.getByRole("article", { name: "Negativo" });
    expect(negative).toHaveTextContent("-2.250 kg");
    expect(negative).toHaveTextContent("Inconsistencia de saldo");
    expect(negative).toHaveTextContent(
      "Realiza un conteo para verificar la existencia.",
    );
    expect(within(negative).getByRole("alert")).toBeInTheDocument();
    expect(screen.queryByText(/versión de stock/i)).not.toBeInTheDocument();
  });

  it("allows a Manual Exit to cross zero and then renders the authoritative negative warning", async () => {
    const established = {
      ...uninitialized,
      currentRegisteredQuantity: "2",
      quantityEstablished: true,
      asOfMovementRevision: 1,
    };
    const negative = {
      ...established,
      currentRegisteredQuantity: "-3",
      hasNegativeBalanceInconsistency: true,
      asOfMovementRevision: 2,
    };
    vi.mocked(listInventoryConfigurationItems).mockRejectedValueOnce(forbidden);
    vi.mocked(listInventoryOperationalItems)
      .mockResolvedValueOnce([established])
      .mockResolvedValueOnce([negative]);
    vi.mocked(recordManualInventoryExit).mockResolvedValueOnce({
      movementId: "movement-1",
      itemId: "item-1",
      nature: "manual_exit",
      quantity: "5",
      previousRegisteredQuantity: "2",
      resultingRegisteredQuantity: "-3",
      movementRevision: 2,
      occurredAt: "2026-09-04T12:00:00Z",
    });
    const user = userEvent.setup();
    render(<InventoryPanel onUnauthorized={vi.fn()} />);

    await user.type(
      await screen.findByLabelText("Cantidad de salida manual para Harina"),
      "5",
    );
    await user.click(
      screen.getByRole("button", { name: "Registrar salida manual" }),
    );

    const refreshed = await screen.findByRole("article", { name: "Harina" });
    await waitFor(() => expect(refreshed).toHaveTextContent("-3 kg"));
    expect(refreshed).toHaveTextContent("Inconsistencia de saldo");
    expect(within(refreshed).getByRole("alert")).toHaveTextContent(
      "Realiza un conteo para verificar la existencia.",
    );
  });

  it("refreshes open History after a successful Entry", async () => {
    const established = {
      ...uninitialized,
      currentRegisteredQuantity: "2",
      quantityEstablished: true,
      asOfMovementRevision: 1,
    };
    const refreshed = {
      ...established,
      currentRegisteredQuantity: "3",
      asOfMovementRevision: 2,
    };
    vi.mocked(listInventoryConfigurationItems).mockRejectedValueOnce(forbidden);
    vi.mocked(listInventoryOperationalItems)
      .mockResolvedValueOnce([established])
      .mockResolvedValueOnce([refreshed]);
    vi.mocked(getInventoryMovementHistory)
      .mockResolvedValueOnce({
        itemId: "item-1",
        operationalName: "Harina",
        operationalUnit: "kg",
        movements: [],
        nextBeforeRevision: null,
      })
      .mockResolvedValueOnce({
        itemId: "item-1",
        operationalName: "Harina",
        operationalUnit: "kg",
        movements: [
          {
            movementId: "movement-1",
            movementRevision: 2,
            nature: "entry",
            quantity: "1",
            signedEffect: "+1",
            previousRegisteredQuantity: "2",
            resultingRegisteredQuantity: "3",
            occurredAt: "2026-09-04T12:00:00Z",
            actorIdentityId: "actor-1",
            actorOperationalName: "Operador",
            reconciliation: null,
          },
        ],
        nextBeforeRevision: null,
      });
    vi.mocked(recordInventoryEntry).mockResolvedValueOnce({
      movementId: "movement-1",
      itemId: "item-1",
      nature: "entry",
      quantity: "1",
      previousRegisteredQuantity: "2",
      resultingRegisteredQuantity: "3",
      movementRevision: 2,
      occurredAt: "2026-09-04T12:00:00Z",
    });
    const user = userEvent.setup();
    render(<InventoryPanel onUnauthorized={vi.fn()} />);
    await user.click(
      await screen.findByRole("button", { name: "Ver movimientos de Harina" }),
    );
    await screen.findByText(
      "No hay movimientos registrados para este elemento.",
    );

    await user.type(
      screen.getByLabelText("Cantidad de entrada para Harina"),
      "1",
    );
    await user.click(screen.getByRole("button", { name: "Registrar entrada" }));

    expect(await screen.findByText("Operador")).toBeInTheDocument();
    expect(listInventoryOperationalItems).toHaveBeenCalledTimes(2);
    expect(getInventoryMovementHistory).toHaveBeenCalledTimes(2);
  });

  it("opens State-independent movement History only from an operational Item", async () => {
    vi.mocked(listInventoryConfigurationItems).mockRejectedValueOnce(forbidden);
    vi.mocked(listInventoryOperationalItems).mockResolvedValueOnce([
      uninitialized,
    ]);
    vi.mocked(getInventoryMovementHistory).mockResolvedValueOnce({
      itemId: configurationItem.itemId,
      operationalName: configurationItem.operationalName,
      operationalUnit: configurationItem.operationalUnit,
      movements: [],
      nextBeforeRevision: null,
    });
    const user = userEvent.setup();
    render(<InventoryPanel onUnauthorized={vi.fn()} />);
    await user.click(
      await screen.findByRole("button", {
        name: "Ver movimientos de Harina",
      }),
    );

    expect(
      await screen.findByRole("heading", { name: "Movimientos" }),
    ).toBeInTheDocument();
    expect(getInventoryMovementHistory).toHaveBeenCalledWith("item-1", {
      limit: 20,
    });
  });

  it("shows read errors with independent retry actions", async () => {
    vi.mocked(listInventoryConfigurationItems)
      .mockRejectedValueOnce(new InventoryNetworkError())
      .mockResolvedValueOnce([configurationItem]);
    vi.mocked(listInventoryOperationalItems)
      .mockRejectedValueOnce(new InventoryNetworkError())
      .mockResolvedValueOnce([uninitialized]);
    const user = userEvent.setup();
    render(<InventoryPanel onUnauthorized={vi.fn()} />);

    expect(
      await screen.findByText(
        "No se pudo cargar la configuración de Inventario.",
      ),
    ).toBeInTheDocument();
    expect(
      await screen.findByText("No se pudo cargar el estado de Inventario."),
    ).toBeInTheDocument();
    const retryButtons = screen.getAllByRole("button", { name: "Reintentar" });
    await user.click(retryButtons[0]!);
    await user.click(retryButtons[1]!);

    expect(await screen.findAllByText("Harina")).toHaveLength(2);
    expect(
      await screen.findByRole("button", { name: "Ver movimientos de Harina" }),
    ).toBeInTheDocument();
  });
});
