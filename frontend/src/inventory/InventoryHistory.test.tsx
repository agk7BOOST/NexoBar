import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";
import {
  discardAntiforgeryToken,
  getAntiforgeryToken,
} from "../identity/sessionClient.ts";
import { InventoryHistory } from "./InventoryHistory.tsx";
import {
  getInventoryMovementHistory,
  correctInventoryMovement,
  InventoryNetworkError,
  InventoryProblemError,
  type InventoryMovement,
  type InventoryMovementHistory,
  type InventoryOperationalItem,
} from "./inventoryClient.ts";

vi.mock("../identity/sessionClient.ts", async (importOriginal) => {
  const original =
    await importOriginal<typeof import("../identity/sessionClient.ts")>();
  return {
    ...original,
    discardAntiforgeryToken: vi.fn(),
    getAntiforgeryToken: vi.fn().mockResolvedValue("csrf"),
  };
});

vi.mock("./inventoryClient.ts", async (importOriginal) => {
  const original =
    await importOriginal<typeof import("./inventoryClient.ts")>();
  return {
    ...original,
    getInventoryMovementHistory: vi.fn(),
    correctInventoryMovement: vi.fn(),
  };
});

const item: InventoryOperationalItem = {
  itemId: "item-1",
  operationalName: "Harina",
  operationalUnit: "kg",
  currentRegisteredQuantity: "7",
  quantityEstablished: true,
  requiresReconciliation: false,
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
      onAuthoritativeMutation={vi.fn().mockResolvedValue(undefined)}
    />,
  );
  return { onUnauthorized, onItemUnavailable };
}

describe("InventoryHistory", () => {
  beforeEach(() => {
    vi.mocked(discardAntiforgeryToken).mockReset();
    vi.mocked(getAntiforgeryToken).mockResolvedValue("csrf");
    vi.mocked(getInventoryMovementHistory).mockReset();
    vi.mocked(correctInventoryMovement).mockReset();
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

  it("corrects an eligible displayed root, explains zero and refreshes authoritative state and History", async () => {
    const root = movement({
      movementId: "root",
      movementRevision: 5,
      nature: "entry",
      quantity: "10",
    });
    vi.mocked(getInventoryMovementHistory)
      .mockResolvedValueOnce(history([root]))
      .mockResolvedValueOnce(
        history([
          {
            ...root,
            effectiveQuantity: "0",
            corrections: [
              {
                sequence: 1,
                previousNature: "Entry",
                previousQuantity: "10",
                correctedNature: "Entry",
                correctedQuantity: "0",
                deltaApplied: "-10",
                resultingRegisteredQuantity: "0",
                movementRevision: 7,
                actorIdentityId: "actor",
                actorOperationalName: "Operador",
                occurredAtUtc: "2026-09-23T00:00:00Z",
              },
            ],
          },
        ]),
      );
    vi.mocked(correctInventoryMovement).mockResolvedValue({
      rootMovementId: "root",
      sequence: 1,
      previousNature: "Entry",
      previousQuantity: "10",
      correctedNature: "Entry",
      correctedQuantity: "0",
      deltaApplied: "-10",
      resultingRegisteredQuantity: "0",
      movementRevision: 7,
      replayed: false,
    });
    const user = userEvent.setup();
    renderHistory();
    await screen.findByText("Actor root");
    await user.click(
      screen.getByRole("button", { name: "Corregir movimiento" }),
    );
    const quantity = screen.getByLabelText("Cantidad corregida");
    await user.clear(quantity);
    await user.type(quantity, "0");
    expect(
      screen.getByText(
        "Este movimiento queda sin efecto. La History permanece.",
      ),
    ).toBeInTheDocument();
    await user.click(
      screen.getByRole("button", { name: "Guardar corrección" }),
    );
    await waitFor(() =>
      expect(correctInventoryMovement).toHaveBeenCalledWith(
        "root",
        {
          correctedNature: "Entry",
          correctedQuantity: "0",
          expectedMovementRevision: 6,
        },
        expect.any(String),
        "csrf",
      ),
    );
    expect(
      await screen.findByText(/Significado efectivo actual/),
    ).toBeInTheDocument();
  });

  it("keeps root, corrected intent and idempotency key on uncertain retry", async () => {
    const root = movement({
      movementId: "retry-root",
      movementRevision: 5,
      nature: "entry",
      quantity: "10",
    });
    vi.mocked(getInventoryMovementHistory)
      .mockResolvedValueOnce(history([root]))
      .mockResolvedValueOnce(history([root]));
    vi.mocked(correctInventoryMovement)
      .mockRejectedValueOnce(new InventoryNetworkError())
      .mockResolvedValueOnce({
        rootMovementId: "retry-root",
        sequence: 1,
        previousNature: "Entry",
        previousQuantity: "10",
        correctedNature: "Entry",
        correctedQuantity: "6",
        deltaApplied: "-4",
        resultingRegisteredQuantity: "11",
        movementRevision: 7,
        replayed: true,
      });
    const user = userEvent.setup();
    renderHistory();
    await screen.findByText("Actor retry-root");
    await user.click(
      screen.getByRole("button", { name: "Corregir movimiento" }),
    );
    const quantity = screen.getByLabelText("Cantidad corregida");
    await user.clear(quantity);
    await user.type(quantity, "6");
    await user.click(
      screen.getByRole("button", { name: "Guardar corrección" }),
    );
    expect(
      await screen.findByText(/resultado todavía no está confirmado/),
    ).toBeInTheDocument();
    await user.click(
      screen.getByRole("button", { name: "Reintentar corrección" }),
    );
    await waitFor(() =>
      expect(correctInventoryMovement).toHaveBeenCalledTimes(2),
    );
    expect(vi.mocked(correctInventoryMovement).mock.calls[1]).toEqual(
      vi.mocked(correctInventoryMovement).mock.calls[0],
    );
  });

  it("shows the root's effective value and previous corrections", async () => {
    const root = movement({
      movementId: "corrected-root",
      movementRevision: 5,
      nature: "entry",
      quantity: "10",
      effectiveNature: "waste",
      effectiveQuantity: "2",
      corrections: [
        {
          sequence: 1,
          previousNature: "Entry",
          previousQuantity: "10",
          correctedNature: "Entry",
          correctedQuantity: "6",
          deltaApplied: "-4",
          resultingRegisteredQuantity: "6",
          movementRevision: 6,
          actorIdentityId: "actor-1",
          actorOperationalName: "Operadora 1",
          occurredAtUtc: "2026-09-23T00:00:00Z",
        },
        {
          sequence: 2,
          previousNature: "Entry",
          previousQuantity: "6",
          correctedNature: "Waste",
          correctedQuantity: "2",
          deltaApplied: "-8",
          resultingRegisteredQuantity: "-2",
          movementRevision: 7,
          actorIdentityId: "actor-2",
          actorOperationalName: "Operadora 2",
          occurredAtUtc: "2026-09-23T00:01:00Z",
        },
      ],
    });
    vi.mocked(getInventoryMovementHistory).mockResolvedValueOnce(
      history([root]),
    );
    const user = userEvent.setup();
    renderHistory();
    const card = (await screen.findByText("Actor corrected-root")).closest(
      "li",
    )!;
    expect(card).toHaveTextContent("Entrada");
    expect(card).toHaveTextContent("Significado efectivo actual: waste 2");
    expect(card).toHaveTextContent("Operadora 1");
    expect(card).toHaveTextContent("Operadora 2");
    await user.click(
      within(card).getByRole("button", { name: "Corregir movimiento" }),
    );
    expect(screen.getByLabelText("Cantidad corregida")).toHaveValue("2");
    expect(screen.getByLabelText("Naturaleza corregida")).toHaveValue("Waste");
  });

  it("blocks malformed correction quantities in the form", async () => {
    vi.mocked(getInventoryMovementHistory).mockResolvedValueOnce(
      history([
        movement({
          movementId: "invalid-root",
          movementRevision: 5,
          nature: "entry",
        }),
      ]),
    );
    const user = userEvent.setup();
    renderHistory();
    await screen.findByText("Actor invalid-root");
    await user.click(
      screen.getByRole("button", { name: "Corregir movimiento" }),
    );
    const quantity = screen.getByLabelText("Cantidad corregida");
    await user.clear(quantity);
    await user.type(quantity, "-4");
    await user.click(
      screen.getByRole("button", { name: "Guardar corrección" }),
    );
    expect(correctInventoryMovement).not.toHaveBeenCalled();
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
