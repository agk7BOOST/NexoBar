import { useCallback, useEffect, useRef, useState } from "react";
import { discardAntiforgeryToken } from "../identity/sessionClient.ts";
import {
  getInventoryMovementHistory,
  InventoryProblemError,
  type InventoryMovement,
  type InventoryMovementHistory,
  type InventoryMovementNature,
  type InventoryOperationalItem,
} from "./inventoryClient.ts";

interface InventoryHistoryProps {
  item: InventoryOperationalItem | null;
  refreshRevision?: number;
  onClose: () => void;
  onUnauthorized: () => void;
  onItemUnavailable: () => void;
}

type HistoryState =
  | { status: "idle" }
  | { status: "loading" }
  | { status: "ready"; history: InventoryMovementHistory }
  | { status: "forbidden" }
  | { status: "unavailable" }
  | { status: "error" };

const historyPageLimit = 20;

function inventoryMovementNatureLabel(nature: InventoryMovementNature): string {
  switch (nature) {
    case "entry":
      return "Entrada";
    case "manual_exit":
      return "Salida manual";
    case "waste":
      return "Merma";
    case "reconciliation":
      return "Reconciliación";
  }
}

function Quantity({ value, unit }: { value: string; unit: string }) {
  return (
    <>
      {value} {unit}
    </>
  );
}

function MovementDetails({
  movement,
  unit,
}: {
  movement: InventoryMovement;
  unit: string;
}) {
  const reconciliation = movement.reconciliation;
  if (reconciliation?.establishedQuantity === true) {
    return (
      <>
        <p className="inventory-establishment">
          Existencia establecida mediante conteo
        </p>
        <dl className="inventory-movement-values">
          <div>
            <dt>Observado</dt>
            <dd>
              <Quantity value={reconciliation.observedQuantity} unit={unit} />
            </dd>
          </div>
          <div>
            <dt>Resultado</dt>
            <dd>
              <Quantity
                value={movement.resultingRegisteredQuantity}
                unit={unit}
              />
            </dd>
          </div>
        </dl>
      </>
    );
  }

  if (reconciliation !== null) {
    return (
      <dl className="inventory-movement-values">
        <div>
          <dt>Saldo previo</dt>
          <dd>
            <Quantity
              value={movement.previousRegisteredQuantity ?? ""}
              unit={unit}
            />
          </dd>
        </div>
        <div>
          <dt>Cantidad observada</dt>
          <dd>
            <Quantity value={reconciliation.observedQuantity} unit={unit} />
          </dd>
        </div>
        <div>
          <dt>Diferencia</dt>
          <dd>
            <Quantity value={reconciliation.difference ?? ""} unit={unit} />
          </dd>
        </div>
        <div>
          <dt>Nuevo saldo</dt>
          <dd>
            <Quantity
              value={movement.resultingRegisteredQuantity}
              unit={unit}
            />
          </dd>
        </div>
      </dl>
    );
  }

  return (
    <dl className="inventory-movement-values">
      <div>
        <dt>Cantidad</dt>
        <dd>
          <Quantity value={movement.quantity} unit={unit} />
        </dd>
      </div>
      <div>
        <dt>Efecto</dt>
        <dd>
          <Quantity value={movement.signedEffect ?? ""} unit={unit} />
        </dd>
      </div>
      <div>
        <dt>Saldo anterior</dt>
        <dd>
          <Quantity
            value={movement.previousRegisteredQuantity ?? ""}
            unit={unit}
          />
        </dd>
      </div>
      <div>
        <dt>Saldo resultante</dt>
        <dd>
          <Quantity value={movement.resultingRegisteredQuantity} unit={unit} />
        </dd>
      </div>
    </dl>
  );
}

function MovementCard({
  movement,
  unit,
}: {
  movement: InventoryMovement;
  unit: string;
}) {
  return (
    <li className="inventory-movement">
      <div className="inventory-movement-heading">
        <h5>{inventoryMovementNatureLabel(movement.nature)}</h5>
        <time dateTime={movement.occurredAt}>{movement.occurredAt}</time>
      </div>
      <MovementDetails movement={movement} unit={unit} />
      <p className="inventory-movement-actor">
        Realizado por <strong>{movement.actorOperationalName}</strong>
      </p>
    </li>
  );
}

export function InventoryHistory({
  item,
  refreshRevision = 0,
  onClose,
  onUnauthorized,
  onItemUnavailable,
}: InventoryHistoryProps) {
  const [state, setState] = useState<HistoryState>({ status: "idle" });
  const [isLoadingOlder, setIsLoadingOlder] = useState(false);
  const [paginationError, setPaginationError] = useState(false);
  const requestSequence = useRef(0);
  const cancelRequests = useCallback(() => {
    requestSequence.current++;
  }, []);

  const handleFailure = useCallback(
    (error: unknown, sequence: number) => {
      if (sequence !== requestSequence.current) return;
      if (error instanceof InventoryProblemError) {
        if (error.status === 401) {
          discardAntiforgeryToken();
          onUnauthorized();
          return;
        }
        if (error.status === 403) {
          setState({ status: "forbidden" });
          return;
        }
        if (error.status === 404) {
          setState({ status: "unavailable" });
          onItemUnavailable();
          return;
        }
      }
      setState({ status: "error" });
    },
    [onItemUnavailable, onUnauthorized],
  );

  const loadFirstPage = useCallback(async () => {
    if (item === null) return;
    const sequence = ++requestSequence.current;
    setState({ status: "loading" });
    setPaginationError(false);
    try {
      const history = await getInventoryMovementHistory(item.itemId, {
        limit: historyPageLimit,
      });
      if (sequence === requestSequence.current) {
        setState({ status: "ready", history });
      }
    } catch (error) {
      handleFailure(error, sequence);
    }
  }, [handleFailure, item]);

  useEffect(() => {
    if (item === null) {
      cancelRequests();
      return;
    }
    const timeout = window.setTimeout(() => void loadFirstPage(), 0);
    return () => {
      window.clearTimeout(timeout);
      cancelRequests();
    };
  }, [cancelRequests, item, loadFirstPage, refreshRevision]);

  async function loadOlder(history: InventoryMovementHistory) {
    if (history.nextBeforeRevision === null || isLoadingOlder) return;
    const sequence = ++requestSequence.current;
    setIsLoadingOlder(true);
    setPaginationError(false);
    try {
      const older = await getInventoryMovementHistory(item!.itemId, {
        beforeRevision: history.nextBeforeRevision,
        limit: historyPageLimit,
      });
      if (sequence !== requestSequence.current) return;
      if (older.itemId !== history.itemId) {
        throw new Error("Inventory History returned another Item.");
      }
      setState({
        status: "ready",
        history: {
          ...older,
          movements: [...history.movements, ...older.movements],
        },
      });
    } catch (error) {
      if (sequence !== requestSequence.current) return;
      if (error instanceof InventoryProblemError && error.status !== 500) {
        handleFailure(error, sequence);
      } else {
        setPaginationError(true);
      }
    } finally {
      if (sequence === requestSequence.current) setIsLoadingOlder(false);
    }
  }

  if (item === null) return null;

  return (
    <section
      className="inventory-history"
      aria-labelledby="inventory-history-heading"
      aria-busy={state.status === "loading" || isLoadingOlder}
    >
      <div className="section-heading">
        <div>
          <h4 id="inventory-history-heading">Movimientos</h4>
          <p>{item.operationalName}</p>
        </div>
        <div className="inventory-history-actions">
          <button
            type="button"
            className="secondary-button"
            disabled={state.status === "loading"}
            onClick={() => void loadFirstPage()}
          >
            Actualizar
          </button>
          <button type="button" className="secondary-button" onClick={onClose}>
            Cerrar movimientos
          </button>
        </div>
      </div>

      {state.status === "loading" && <p role="status">Cargando movimientos…</p>}
      {state.status === "forbidden" && (
        <p className="notice notice--functional-error" role="alert">
          Esta Identity no tiene autorización para consultar movimientos de
          Inventario.
        </p>
      )}
      {state.status === "unavailable" && (
        <p className="notice notice--functional-error" role="alert">
          El elemento ya no está disponible.
        </p>
      )}
      {state.status === "error" && (
        <div className="notice notice--functional-error" role="alert">
          <p>No se pudo cargar la Historia de Movimientos.</p>
          <button type="button" onClick={() => void loadFirstPage()}>
            Reintentar
          </button>
        </div>
      )}
      {state.status === "ready" && state.history.movements.length === 0 && (
        <p>No hay movimientos registrados para este elemento.</p>
      )}
      {state.status === "ready" && state.history.movements.length > 0 && (
        <ol className="inventory-movements">
          {state.history.movements.map((movement) => (
            <MovementCard
              key={movement.movementId}
              movement={movement}
              unit={state.history.operationalUnit}
            />
          ))}
        </ol>
      )}
      {state.status === "ready" && paginationError && (
        <p className="notice notice--functional-error" role="alert">
          No se pudieron cargar los movimientos anteriores.
        </p>
      )}
      {state.status === "ready" &&
        state.history.nextBeforeRevision !== null && (
          <button
            type="button"
            className="secondary-button"
            disabled={isLoadingOlder}
            onClick={() => void loadOlder(state.history)}
          >
            {isLoadingOlder ? "Cargando…" : "Ver movimientos anteriores"}
          </button>
        )}
    </section>
  );
}
