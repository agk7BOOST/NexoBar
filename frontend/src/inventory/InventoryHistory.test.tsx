import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { discardAntiforgeryToken } from "../identity/sessionClient.ts";
import { InventoryHistory } from "./InventoryHistory.tsx";
import {
  getInventoryMovementHistory,
  InventoryNetworkError,
  InventoryProblemError,
  type InventoryMovement,
  type InventoryMovementHistory,
  type InventoryOperationalItem,
} from "./inventoryClient.ts";

vi.mock("../identity/sessionClient.ts", async (importOriginal) => {
  const original =
    await importOriginal<typeof import("../identity/sessionClient.ts")>();
  return { ...original, discardAntiforgeryToken: vi.fn() };
});

vi.mock("./inventoryClient.ts", async (importOriginal) => {
  const original =
    await importOriginal<typeof import("./inventoryClient.ts")>();
  return { ...original, getInventoryMovementHistory: vi.fn() };
});

const item: InventoryOperationalItem = {
  itemId: "item-1",
  operationalName: "Harina",
  operationalUnit: "kg",
  currentRegisteredQuantity: "7",
  quantityEstablished: true,
  hasNegativeBalanceInconsistency: false,
  asOfMovementRevision: 6,
};

function movement(
  overrides: Partial<InventoryMovement> &
    Pick<InventoryMovement, "movementId" | "movementRevision" | "nature">,
): InventoryMovement {
  const { movementId, movementRevision, nature, ...remaining } = overrides;
  return {
    movementId,
    movementRevision,
    nature,
    quantity: "5",
    signedEffect: "+5",
    previousRegisteredQuantity: "10",
    resultingRegisteredQuantity: "15",
    occurredAt: "2026-09-04T12:00:00Z",
    actorIdentityId: `uuid-${movementId}`,
    actorOperationalName: `Actor ${movementId}`,
    reconciliation: null,
    ...remaining,
  };
}

function history(
  movements: InventoryMovement[],
  nextBeforeRevision: number | null = null,
): InventoryMovementHistory {
  return {
    itemId: item.itemId,
    operationalName: item.operationalName,
    operationalUnit: item.operationalUnit,
    movements,
    nextBeforeRevision,
  };
}

function renderHistory(onUnauthorized = vi.fn(), onItemUnavailable = vi.fn()) {
  render(
    <InventoryHistory
      item={item}
      onClose={vi.fn()}
      onUnauthorized={onUnauthorized}
      onItemUnavailable={onItemUnavailable}
    />,
  );
  return { onUnauthorized, onItemUnavailable };
}

describe("InventoryHistory", () => {
  beforeEach(() => {
    vi.mocked(discardAntiforgeryToken).mockReset();
    vi.mocked(getInventoryMovementHistory).mockReset();
  });

  it("presents distinct natures, movement effects, actor names and timestamp", async () => {
    vi.mocked(getInventoryMovementHistory).mockResolvedValueOnce(
      history([
        movement({ movementId: "entry", movementRevision: 4, nature: "entry" }),
        movement({
          movementId: "exit",
          movementRevision: 3,
          nature: "manual_exit",
          quantity: "3",
          signedEffect: "-3",
          resultingRegisteredQuantity: "7",
        }),
        movement({
          movementId: "waste",
          movementRevision: 2,
          nature: "waste",
          quantity: "3",
          signedEffect: "-3",
          resultingRegisteredQuantity: "7",
        }),
      ]),
    );
    renderHistory();

    expect(
      await screen.findByRole("heading", { name: "Entrada" }),
    ).toBeInTheDocument();
    expect(
      screen.getByRole("heading", { name: "Salida manual" }),
    ).toBeInTheDocument();
    expect(screen.getByRole("heading", { name: "Merma" })).toBeInTheDocument();
    const entry = screen
      .getByRole("heading", { name: "Entrada" })
      .closest("li")!;
    expect(entry).toHaveTextContent("Cantidad5 kg");
    expect(entry).toHaveTextContent("Efecto+5 kg");
    expect(entry).toHaveTextContent("Saldo anterior10 kg");
    expect(entry).toHaveTextContent("Saldo resultante15 kg");
    expect(entry).toHaveTextContent("Actor entry");
    expect(entry).not.toHaveTextContent("uuid-entry");
    expect(within(entry).getByRole("time")).toHaveAttribute(
      "datetime",
      "2026-09-04T12:00:00Z",
    );
  });

  it("presents initial reconciliation, including zero, without a fictitious effect", async () => {
    vi.mocked(getInventoryMovementHistory).mockResolvedValueOnce(
      history([
        movement({
          movementId: "initial-ten",
          movementRevision: 2,
          nature: "reconciliation",
          quantity: "10",
          signedEffect: null,
          previousRegisteredQuantity: null,
          resultingRegisteredQuantity: "10",
          reconciliation: {
            observedQuantity: "10",
            difference: null,
            establishedQuantity: true,
          },
        }),
        movement({
          movementId: "initial-zero",
          movementRevision: 1,
          nature: "reconciliation",
          quantity: "0",
          signedEffect: null,
          previousRegisteredQuantity: null,
          resultingRegisteredQuantity: "0",
          reconciliation: {
            observedQuantity: "0",
            difference: null,
            establishedQuantity: true,
          },
        }),
      ]),
    );
    renderHistory();

    const establishments = await screen.findAllByText(
      "Existencia establecida mediante conteo",
    );
    expect(establishments).toHaveLength(2);
    const initialTen = establishments[0]!.closest("li")!;
    expect(initialTen).toHaveTextContent("Observado10 kg");
    expect(initialTen).toHaveTextContent("Resultado10 kg");
    expect(initialTen).not.toHaveTextContent("+10");
    expect(initialTen).not.toHaveTextContent("Saldo previo");
    expect(initialTen).not.toHaveTextContent("Diferencia");

    const initialZero = establishments[1]!.closest("li")!;
    expect(initialZero).toHaveTextContent("Observado0 kg");
    expect(initialZero).toHaveTextContent("Resultado0 kg");
    expect(initialZero).not.toHaveTextContent("0 → 0");
    expect(initialZero).not.toHaveTextContent("No hubo cambios");
  });

  it("presents ordinary reconciliation with observed quantity and backend difference", async () => {
    vi.mocked(getInventoryMovementHistory).mockResolvedValueOnce(
      history([
        movement({
          movementId: "ordinary",
          movementRevision: 2,
          nature: "reconciliation",
          quantity: "7",
          signedEffect: "-3",
          previousRegisteredQuantity: "10",
          resultingRegisteredQuantity: "7",
          reconciliation: {
            observedQuantity: "7",
            difference: "-3",
            establishedQuantity: false,
          },
        }),
      ]),
    );
    renderHistory();

    const reconciliation = await screen.findByRole("heading", {
      name: "Reconciliación",
    });
    const card = reconciliation.closest("li")!;
    expect(card).toHaveTextContent("Saldo previo10 kg");
    expect(card).toHaveTextContent("Cantidad observada7 kg");
    expect(card).toHaveTextContent("Diferencia-3 kg");
    expect(card).toHaveTextContent("Nuevo saldo7 kg");
    expect(card).not.toHaveTextContent("Existencia establecida");
  });

  it("loads recent movements first and appends older cursor pages without sorting", async () => {
    vi.mocked(getInventoryMovementHistory)
      .mockResolvedValueOnce(
        history(
          [
            movement({
              movementId: "recent",
              movementRevision: 5,
              nature: "entry",
              actorOperationalName: "Actor reciente",
            }),
          ],
          5,
        ),
      )
      .mockResolvedValueOnce(
        history([
          movement({
            movementId: "older",
            movementRevision: 4,
            nature: "waste",
            actorOperationalName: "Actor anterior",
          }),
        ]),
      );
    const user = userEvent.setup();
    renderHistory();

    await screen.findByText("Actor reciente");
    expect(getInventoryMovementHistory).toHaveBeenNthCalledWith(1, "item-1", {
      limit: 20,
    });
    await user.click(
      screen.getByRole("button", { name: "Ver movimientos anteriores" }),
    );

    expect(await screen.findByText("Actor anterior")).toBeInTheDocument();
    expect(getInventoryMovementHistory).toHaveBeenNthCalledWith(2, "item-1", {
      beforeRevision: 5,
      limit: 20,
    });
    const actors = screen.getAllByText(/Actor (reciente|anterior)/);
    expect(actors.map((actor) => actor.textContent)).toEqual([
      "Actor reciente",
      "Actor anterior",
    ]);
  });

  it("refresh replaces loaded pages with the recent snapshot", async () => {
    vi.mocked(getInventoryMovementHistory)
      .mockResolvedValueOnce(
        history([
          movement({
            movementId: "old",
            movementRevision: 1,
            nature: "entry",
            actorOperationalName: "Actor viejo",
          }),
        ]),
      )
      .mockResolvedValueOnce(
        history([
          movement({
            movementId: "new",
            movementRevision: 2,
            nature: "entry",
            actorOperationalName: "Actor nuevo",
          }),
        ]),
      );
    const user = userEvent.setup();
    renderHistory();
    await screen.findByText("Actor viejo");

    await user.click(screen.getByRole("button", { name: "Actualizar" }));

    expect(await screen.findByText("Actor nuevo")).toBeInTheDocument();
    expect(screen.queryByText("Actor viejo")).not.toBeInTheDocument();
  });

  it("shows empty History without fabricating a no-discrepancy row", async () => {
    vi.mocked(getInventoryMovementHistory).mockResolvedValueOnce(history([]));
    renderHistory();

    expect(
      await screen.findByText(
        "No hay movimientos registrados para este elemento.",
      ),
    ).toBeInTheDocument();
    expect(
      screen.queryByText(/Conteo correcto|No hubo cambios/),
    ).not.toBeInTheDocument();
  });

  it("handles 401, 403 and 404 with their distinct semantics", async () => {
    vi.mocked(getInventoryMovementHistory).mockRejectedValueOnce(
      new InventoryProblemError(401),
    );
    const unauthorized = renderHistory();
    await waitFor(() =>
      expect(unauthorized.onUnauthorized).toHaveBeenCalledTimes(1),
    );
    expect(discardAntiforgeryToken).toHaveBeenCalled();

    vi.mocked(getInventoryMovementHistory).mockRejectedValueOnce(
      new InventoryProblemError(403),
    );
    const forbiddenRender = renderHistory();
    expect(
      await screen.findByText(
        "Esta Identity no tiene autorización para consultar movimientos de Inventario.",
      ),
    ).toBeInTheDocument();
    expect(forbiddenRender.onUnauthorized).not.toHaveBeenCalled();

    vi.mocked(getInventoryMovementHistory).mockRejectedValueOnce(
      new InventoryProblemError(404),
    );
    const missing = renderHistory();
    expect(
      await screen.findByText("El elemento ya no está disponible."),
    ).toBeInTheDocument();
    expect(missing.onItemUnavailable).toHaveBeenCalledTimes(1);
  });

  it("offers retry for a non-mutating read failure", async () => {
    vi.mocked(getInventoryMovementHistory)
      .mockRejectedValueOnce(new InventoryNetworkError())
      .mockResolvedValueOnce(history([]));
    const user = userEvent.setup();
    renderHistory();

    expect(
      await screen.findByText("No se pudo cargar la Historia de Movimientos."),
    ).toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: "Reintentar" }));
    expect(
      await screen.findByText(
        "No hay movimientos registrados para este elemento.",
      ),
    ).toBeInTheDocument();
  });
});
