import { useCallback, useEffect, useRef, useState } from "react";
import { formatOperationalDate } from "../formatOperationalDate.ts";
import {
  discardAntiforgeryToken,
  getAntiforgeryToken,
} from "../identity/sessionClient.ts";
import {
  getInventoryMovementHistory,
  InventoryProblemError,
  type InventoryMovement,
  type InventoryMovementHistory,
  type InventoryMovementNature,
  type InventoryOperationalItem,
  correctInventoryMovement,
} from "./inventoryClient.ts";

interface InventoryHistoryProps {
  item: InventoryOperationalItem | null;
  refreshRevision?: number;
  onClose: () => void;
  onUnauthorized: () => void;
  onItemUnavailable: () => void;
  onAuthoritativeMutation: (itemId: string) => Promise<void>;
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

function correctionNatureLabel(nature: string): string {
  switch (nature) {
    case "Entry":
    case "entry":
      return "Entrada";
    case "ManualExit":
    case "manual_exit":
      return "Salida manual";
    case "Waste":
    case "waste":
      return "Merma";
    case "reconciliation":
      return "Reconciliación";
    default:
      return nature;
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
  onCorrect,
  busy,
}: {
  movement: InventoryMovement;
  unit: string;
  onCorrect: (movement: InventoryMovement) => void;
  busy: boolean;
}) {
  return (
    <li className="inventory-movement">
      <div className="inventory-movement-heading">
        <h5>{inventoryMovementNatureLabel(movement.nature)}</h5>
        <time dateTime={movement.occurredAt} title={movement.occurredAt}>
          {formatOperationalDate(movement.occurredAt)}
        </time>
      </div>
      <MovementDetails movement={movement} unit={unit} />
      {(movement.corrections?.length ?? 0) > 0 && (
        <section>
          <h6>Correcciones anteriores</h6>
          <ol>
            {movement.corrections?.map((c) => (
              <li key={c.sequence}>
                {correctionNatureLabel(c.previousNature)} {c.previousQuantity} →{" "}
                {correctionNatureLabel(c.correctedNature)} {c.correctedQuantity}
                ; delta aplicado {c.deltaApplied}; saldo{" "}
                {c.resultingRegisteredQuantity ?? "no establecido"};{" "}
                {c.actorOperationalName} ·{" "}
                <time dateTime={c.occurredAtUtc} title={c.occurredAtUtc}>
                  {formatOperationalDate(c.occurredAtUtc)}
                </time>
              </li>
            ))}
          </ol>
          <p>
            Significado efectivo actual:{" "}
            {movement.effectiveNature
              ? correctionNatureLabel(movement.effectiveNature)
              : "No disponible"}{" "}
            {movement.effectiveQuantity}
          </p>
        </section>
      )}
      {movement.nature !== "reconciliation" && (
        <button
          type="button"
          className="secondary-button"
          disabled={busy}
          onClick={() => onCorrect(movement)}
        >
          Corregir movimiento
        </button>
      )}
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
  onAuthoritativeMutation,
}: InventoryHistoryProps) {
  const [state, setState] = useState<HistoryState>({ status: "idle" });
  const [isLoadingOlder, setIsLoadingOlder] = useState(false);
  const [paginationError, setPaginationError] = useState(false);
  const [correction, setCorrection] = useState<InventoryMovement | null>(null);
  const [nature, setNature] = useState("Entry");
  const [quantity, setQuantity] = useState("");
  const [pending, setPending] = useState<{
    key: string;
    root: string;
    nature: string;
    quantity: string;
    revision: number;
  } | null>(null);
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

  async function saveCorrection() {
    if (!correction || !item || state.status !== "ready") return;
    const intent = pending ?? {
      key: crypto.randomUUID(),
      root: correction.movementId,
      nature,
      quantity,
      revision:
        state.history.asOfMovementRevision ?? item.asOfMovementRevision ?? 0,
    };
    setPending(intent);
    try {
      const token = await getAntiforgeryToken();
      await correctInventoryMovement(
        intent.root,
        {
          correctedNature: intent.nature,
          correctedQuantity: intent.quantity,
          expectedMovementRevision: intent.revision,
        },
        intent.key,
        token,
      );
      setPending(null);
      setCorrection(null);
      setQuantity("");
      await onAuthoritativeMutation(item.itemId);
      await loadFirstPage();
    } catch (error) {
      if (error instanceof InventoryProblemError && error.status === 401) {
        discardAntiforgeryToken();
        onUnauthorized();
        return;
      }
      if (
        error instanceof InventoryProblemError &&
        error.status !== 408 &&
        error.status < 500
      ) {
        setPending(null);
        setState({ status: "error" });
        return;
      }
      setPending(intent);
    }
  }

  function beginCorrection(movement: InventoryMovement) {
    setCorrection(movement);
    const effective = movement.effectiveNature ?? movement.nature;
    setNature(
      effective === "manual_exit"
        ? "ManualExit"
        : effective === "waste"
          ? "Waste"
          : "Entry",
    );
    setQuantity(movement.effectiveQuantity ?? movement.quantity);
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
          Tu usuario no tiene autorización para consultar movimientos de
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
              onCorrect={beginCorrection}
              busy={pending !== null}
            />
          ))}
        </ol>
      )}
      {correction && state.status === "ready" && (
        <form
          onSubmit={(event) => {
            event.preventDefault();
            void saveCorrection();
          }}
        >
          <h5>
            Corregir {correction.nature} {correction.quantity}
          </h5>
          <p>
            Significado vigente:{" "}
            {correction.effectiveNature ?? correction.nature}{" "}
            {correction.effectiveQuantity ?? correction.quantity}
          </p>
          <label>
            Naturaleza corregida
            <select
              disabled={pending !== null}
              value={nature}
              onChange={(e) => setNature(e.target.value)}
            >
              <option value="Entry">Entrada</option>
              <option value="ManualExit">Salida manual</option>
              <option value="Waste">Merma</option>
            </select>
          </label>
          <label>
            Cantidad corregida
            <input
              disabled={pending !== null}
              value={quantity}
              onChange={(e) => setQuantity(e.target.value)}
              inputMode="decimal"
              required
              pattern="[0-9]{1,16}([.][0-9]{1,12})?"
            />
          </label>
          {/^0+(?:\.0+)?$/.test(quantity) && (
            <p>Este movimiento queda sin efecto. Su historial permanece.</p>
          )}
          {pending !== null && (
            <p role="status">
              El resultado todavía no está confirmado. Reintentá esta misma
              corrección para consultar el resultado.
            </p>
          )}
          <button type="submit">
            {pending ? "Reintentar corrección" : "Guardar corrección"}
          </button>
          <button
            type="button"
            disabled={pending !== null}
            onClick={() => {
              setCorrection(null);
              if (!pending) setQuantity("");
            }}
          >
            Cerrar
          </button>
        </form>
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
