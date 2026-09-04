import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";
import {
  discardAntiforgeryToken,
  getAntiforgeryToken,
} from "../identity/sessionClient.ts";
import { InventoryItemOperations } from "./InventoryItemOperations.tsx";
import {
  InventoryNetworkError,
  InventoryProblemError,
  recordInventoryCount,
  recordInventoryEntry,
  recordInventoryWaste,
  reconcileInventoryCount,
  recordManualInventoryExit,
  type InventoryCountObservation,
  type InventoryOperationalItem,
  type InventoryReconciliationResult,
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
    recordInventoryCount: vi.fn(),
    reconcileInventoryCount: vi.fn(),
    recordInventoryEntry: vi.fn(),
    recordManualInventoryExit: vi.fn(),
    recordInventoryWaste: vi.fn(),
  };
});

const item: InventoryOperationalItem = {
  itemId: "item-1",
  operationalName: "Harina",
  operationalUnit: "kg",
  currentRegisteredQuantity: "10",
  quantityEstablished: true,
  hasNegativeBalanceInconsistency: false,
  asOfMovementRevision: 1,
};

const count: InventoryCountObservation = {
  countObservationId: "count-1",
  itemId: item.itemId,
  observedQuantity: "7.500",
  observedMovementRevision: 1,
  observedOperationalUnit: "kg",
  observedAt: "2026-09-04T12:00:00Z",
};

const reconciled: InventoryReconciliationResult = {
  itemId: item.itemId,
  countObservationId: count.countObservationId,
  outcome: "reconciled",
  movementId: "movement-1",
  occurredAt: "2026-09-04T12:01:00Z",
  previousRegisteredQuantity: "10",
  observedQuantity: "7.5",
  difference: "-2.5",
  resultingRegisteredQuantity: "7.5",
  movementRevision: 2,
};

const keys = [
  "11111111-1111-4111-8111-111111111111",
  "22222222-2222-4222-8222-222222222222",
] as const;

function renderOperations(
  overrides: Partial<InventoryOperationalItem> = {},
  onUnauthorized = vi.fn(),
  onAuthoritativeMutation = vi.fn().mockResolvedValue(undefined),
  onStateRefresh = vi.fn().mockResolvedValue(undefined),
) {
  render(
    <InventoryItemOperations
      item={{ ...item, ...overrides }}
      onUnauthorized={onUnauthorized}
      onStateRefresh={onStateRefresh}
      onAuthoritativeMutation={onAuthoritativeMutation}
      onItemUnavailable={vi.fn()}
    />,
  );
  return { onUnauthorized, onAuthoritativeMutation, onStateRefresh };
}

async function recordCount(
  user: ReturnType<typeof userEvent.setup>,
  value = "7.500",
) {
  await user.type(
    screen.getByLabelText("Cantidad observada para Harina"),
    value,
  );
  await user.click(screen.getByRole("button", { name: "Registrar conteo" }));
}

describe("InventoryItemOperations", () => {
  beforeEach(() => {
    vi.restoreAllMocks();
    vi.mocked(discardAntiforgeryToken).mockReset();
    vi.mocked(getAntiforgeryToken).mockReset().mockResolvedValue("csrf-1");
    vi.mocked(recordInventoryCount).mockReset();
    vi.mocked(reconcileInventoryCount).mockReset();
    vi.mocked(recordInventoryEntry).mockReset();
    vi.mocked(recordManualInventoryExit).mockReset();
    vi.mocked(recordInventoryWaste).mockReset();
    vi.spyOn(crypto, "randomUUID")
      .mockReturnValueOnce(keys[0])
      .mockReturnValueOnce(keys[1]);
  });

  it("records a Count as a decimal string and exposes its observation for explicit Reconciliation", async () => {
    vi.mocked(recordInventoryCount).mockResolvedValueOnce(count);
    const user = userEvent.setup();
    const { onStateRefresh } = renderOperations();

    await recordCount(user);

    expect(recordInventoryCount).toHaveBeenCalledWith(
      "item-1",
      { observedQuantity: "7.500" },
      keys[0],
      "csrf-1",
    );
    expect(
      await screen.findByText(
        "Conteo registrado: 7.500 kg. El saldo no fue modificado.",
      ),
    ).toBeInTheDocument();
    expect(screen.getByText(/Cantidad observada:/)).toHaveTextContent(
      "7.500 kg",
    );
    expect(
      screen.getByRole("button", { name: "Reconciliar conteo" }),
    ).toBeEnabled();
    expect(onStateRefresh).toHaveBeenCalledTimes(1);
  });

  it("establishes an initial zero without inventing a previous zero or difference", async () => {
    vi.mocked(recordInventoryCount).mockResolvedValueOnce({
      ...count,
      observedQuantity: "0",
    });
    vi.mocked(reconcileInventoryCount).mockResolvedValueOnce({
      ...reconciled,
      previousRegisteredQuantity: null,
      observedQuantity: "0",
      difference: null,
      resultingRegisteredQuantity: "0",
    });
    const user = userEvent.setup();
    const { onAuthoritativeMutation } = renderOperations({
      currentRegisteredQuantity: null,
      quantityEstablished: false,
      asOfMovementRevision: 0,
    });

    await recordCount(user, "0");
    await user.click(
      await screen.findByRole("button", { name: "Reconciliar conteo" }),
    );

    expect(reconcileInventoryCount).toHaveBeenCalledWith(
      "item-1",
      { countObservationId: "count-1" },
      keys[1],
      "csrf-1",
    );
    expect(
      await screen.findByText(
        "Existencia inicial establecida en 0. No existía un saldo previo.",
      ),
    ).toBeInTheDocument();
    expect(screen.queryByText(/diferencia 0/i)).not.toBeInTheDocument();
    expect(onAuthoritativeMutation).toHaveBeenCalledWith("item-1");
  });

  it("presents an ordinary Reconciliation using only backend-provided quantities", async () => {
    vi.mocked(recordInventoryCount).mockResolvedValueOnce(count);
    vi.mocked(reconcileInventoryCount).mockResolvedValueOnce(reconciled);
    const user = userEvent.setup();
    renderOperations();

    await recordCount(user);
    await user.click(
      await screen.findByRole("button", { name: "Reconciliar conteo" }),
    );

    expect(
      await screen.findByText(
        "Reconciliación registrada: saldo previo 10, diferencia -2.5, nuevo saldo 7.5.",
      ),
    ).toBeInTheDocument();
  });

  it("presents no_discrepancy without fabricating a Movement", async () => {
    vi.mocked(recordInventoryCount).mockResolvedValueOnce({
      ...count,
      observedQuantity: "10.0",
    });
    vi.mocked(reconcileInventoryCount).mockResolvedValueOnce({
      ...reconciled,
      outcome: "no_discrepancy",
      movementId: null,
      occurredAt: null,
      previousRegisteredQuantity: "10",
      observedQuantity: "10",
      difference: "0",
      resultingRegisteredQuantity: "10",
      movementRevision: 1,
    });
    const user = userEvent.setup();
    renderOperations();

    await recordCount(user, "10.0");
    await user.click(
      await screen.findByRole("button", { name: "Reconciliar conteo" }),
    );

    expect(
      await screen.findByText(
        "Conteo reconciliado sin discrepancia. El saldo continúa en 10. No se creó un Movimiento.",
      ),
    ).toBeInTheDocument();
  });

  it("clears an invalidated Count and requires a new physical Count", async () => {
    vi.mocked(recordInventoryCount).mockResolvedValueOnce(count);
    vi.mocked(reconcileInventoryCount).mockRejectedValueOnce(
      new InventoryProblemError(409, {
        code: "inventory.reconciliation.count_invalidated",
      }),
    );
    const user = userEvent.setup();
    renderOperations();

    await recordCount(user);
    await user.click(
      await screen.findByRole("button", { name: "Reconciliar conteo" }),
    );

    expect(
      await screen.findByText(
        "El conteo quedó invalidado porque la existencia cambió. Se requiere un nuevo conteo físico antes de reconciliar.",
      ),
    ).toBeInTheDocument();
    expect(
      screen.queryByRole("button", { name: "Reconciliar conteo" }),
    ).not.toBeInTheDocument();
    expect(
      screen.getByRole("button", { name: "Registrar conteo" }),
    ).toBeEnabled();
  });

  it.each([
    {
      label: "Cantidad de entrada para Harina",
      button: "Registrar entrada",
      client: recordInventoryEntry,
      result: "12.25",
      quantity: "2.250",
    },
    {
      label: "Cantidad de salida manual para Harina",
      button: "Registrar salida manual",
      client: recordManualInventoryExit,
      result: "-3",
      quantity: "13",
    },
    {
      label: "Cantidad de merma para Harina",
      button: "Registrar merma",
      client: recordInventoryWaste,
      result: "9.875",
      quantity: "0.125",
    },
  ])(
    "records $button as its distinct intent",
    async ({ label, button, client, result, quantity }) => {
      vi.mocked(client).mockResolvedValueOnce({
        movementId: "movement-1",
        itemId: "item-1",
        nature:
          client === recordInventoryEntry
            ? "entry"
            : client === recordManualInventoryExit
              ? "manual_exit"
              : "waste",
        quantity,
        previousRegisteredQuantity: "10",
        resultingRegisteredQuantity: result,
        movementRevision: 2,
        occurredAt: "2026-09-04T12:00:00Z",
      });
      const user = userEvent.setup();
      const { onAuthoritativeMutation } = renderOperations();

      await user.type(screen.getByLabelText(label), quantity);
      await user.click(screen.getByRole("button", { name: button }));

      await waitFor(() => expect(client).toHaveBeenCalledTimes(1));
      expect(client).toHaveBeenCalledWith(
        "item-1",
        { quantity },
        keys[0],
        "csrf-1",
      );
      expect(
        await screen.findByText(new RegExp(`resultante: ${result} kg`)),
      ).toBeInTheDocument();
      expect(onAuthoritativeMutation).toHaveBeenCalledWith("item-1");
    },
  );

  it("blocks duplicate submit and retries an uncertain operation with the exact same target, body and key", async () => {
    vi.mocked(recordInventoryWaste)
      .mockRejectedValueOnce(new InventoryNetworkError())
      .mockResolvedValueOnce({
        movementId: "movement-1",
        itemId: "item-1",
        nature: "waste",
        quantity: "0.125",
        previousRegisteredQuantity: "10",
        resultingRegisteredQuantity: "9.875",
        movementRevision: 2,
        occurredAt: "2026-09-04T12:00:00Z",
      });
    const user = userEvent.setup();
    renderOperations();
    const quantity = screen.getByLabelText("Cantidad de merma para Harina");
    await user.type(quantity, "0.125");
    await user.click(screen.getByRole("button", { name: "Registrar merma" }));

    expect(
      await screen.findByText("No se pudo confirmar el resultado de la merma."),
    ).toBeInTheDocument();
    expect(quantity).toBeDisabled();
    const form = quantity.closest("form")!;
    fireEvent.submit(form);
    expect(recordInventoryWaste).toHaveBeenCalledTimes(1);
    const originalCall = vi.mocked(recordInventoryWaste).mock.calls[0];

    await user.click(
      screen.getByRole("button", { name: "Reintentar misma operación" }),
    );
    await waitFor(() => expect(recordInventoryWaste).toHaveBeenCalledTimes(2));
    expect(vi.mocked(recordInventoryWaste).mock.calls[1]).toEqual(originalCall);
    expect(crypto.randomUUID).toHaveBeenCalledTimes(1);
  });

  it("logs out and clears the active intent on 401", async () => {
    vi.mocked(recordInventoryCount).mockRejectedValueOnce(
      new InventoryProblemError(401),
    );
    const user = userEvent.setup();
    const onUnauthorized = vi.fn();
    renderOperations({}, onUnauthorized);

    await recordCount(user, "1");

    await waitFor(() => expect(onUnauthorized).toHaveBeenCalledTimes(1));
    expect(discardAntiforgeryToken).toHaveBeenCalled();
    expect(
      screen.queryByRole("button", { name: "Reintentar misma operación" }),
    ).not.toBeInTheDocument();
  });

  it("preserves the Identity and reports authorization failure on 403", async () => {
    vi.mocked(recordInventoryEntry).mockRejectedValueOnce(
      new InventoryProblemError(403),
    );
    const user = userEvent.setup();
    const onUnauthorized = vi.fn();
    renderOperations({}, onUnauthorized);

    await user.type(
      screen.getByLabelText("Cantidad de entrada para Harina"),
      "1",
    );
    await user.click(screen.getByRole("button", { name: "Registrar entrada" }));

    expect(
      await screen.findByText(
        "Esta Identity no tiene autorización para operar Inventario.",
      ),
    ).toBeInTheDocument();
    expect(onUnauthorized).not.toHaveBeenCalled();
    expect(
      screen.getByRole("button", { name: "Registrar entrada" }),
    ).toBeEnabled();
  });

  it("validates obvious decimal formats without numeric coercion or blocking negative-result operations", async () => {
    const user = userEvent.setup();
    renderOperations();

    await user.type(
      screen.getByLabelText("Cantidad de entrada para Harina"),
      "1e2",
    );
    await user.click(screen.getByRole("button", { name: "Registrar entrada" }));
    expect(
      await screen.findByText("Ingresá una cantidad positiva válida."),
    ).toBeInTheDocument();
    expect(recordInventoryEntry).not.toHaveBeenCalled();
    expect(
      screen.getByRole("button", { name: "Registrar salida manual" }),
    ).toBeEnabled();
    expect(
      screen.getByRole("button", { name: "Registrar merma" }),
    ).toBeEnabled();
  });
});
