import { type FormEvent, useCallback, useRef, useState } from "react";
import {
  discardAntiforgeryToken,
  getAntiforgeryToken,
  SessionProblemError,
} from "../identity/sessionClient.ts";
import {
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

interface InventoryItemOperationsProps {
  item: InventoryOperationalItem;
  onUnauthorized: () => void;
  onStateRefresh: () => Promise<void>;
  onAuthoritativeMutation: (itemId: string) => Promise<void>;
  onItemUnavailable: () => void;
}

type IntentPhase = "submitting" | "uncertain";
type QuantityOperationKind = "count" | "entry" | "manual-exit" | "waste";

type InventoryOperationIntent =
  | {
      phase: IntentPhase;
      kind: QuantityOperationKind;
      itemId: string;
      body: { quantity: string } | { observedQuantity: string };
      idempotencyKey: string;
    }
  | {
      phase: IntentPhase;
      kind: "reconcile";
      itemId: string;
      body: { countObservationId: string };
      idempotencyKey: string;
    };

type NoticeKind = "success" | "functional-error" | "uncertain";

interface Notice {
  kind: NoticeKind;
  message: string;
}

const decimalPattern = /^(?:[0-9]{1,16})(?:\.[0-9]{1,12})?$/;

function isObservedQuantity(value: string): boolean {
  return decimalPattern.test(value);
}

function isPositiveQuantity(value: string): boolean {
  return decimalPattern.test(value) && /[1-9]/.test(value);
}

function operationLabel(kind: InventoryOperationIntent["kind"]): string {
  switch (kind) {
    case "count":
      return "conteo";
    case "reconcile":
      return "reconciliación";
    case "entry":
      return "entrada";
    case "manual-exit":
      return "salida manual";
    case "waste":
      return "merma";
  }
}

function knownFailureMessage(
  kind: InventoryOperationIntent["kind"],
  error: InventoryProblemError,
): string {
  switch (error.problem.code) {
    case "inventory.count.observed_quantity_invalid":
      return "Ingresá una cantidad observada válida, igual o mayor que cero.";
    case "inventory.movement.quantity_invalid":
      return "Ingresá una cantidad positiva válida.";
    case "inventory.quantity_not_established":
      return "La existencia debe establecerse primero mediante Conteo y Reconciliación.";
    case "inventory.movement.result_out_of_range":
      return "El saldo resultante queda fuera del rango admitido.";
    case "inventory.count.idempotency_key_conflict":
    case "inventory.reconciliation.idempotency_key_conflict":
    case "inventory.movement.idempotency_key_conflict":
      return `La identidad técnica de esta ${operationLabel(kind)} ya fue usada para otra intención.`;
    default:
      return `No se pudo completar la ${operationLabel(kind)}. Revisá los datos e intentá nuevamente.`;
  }
}

function reconciliationMessage(result: InventoryReconciliationResult): string {
  if (result.outcome === "no_discrepancy") {
    return `Conteo reconciliado sin discrepancia. El saldo continúa en ${result.resultingRegisteredQuantity}. No se creó un Movimiento.`;
  }
  if (result.previousRegisteredQuantity === null) {
    return `Existencia inicial establecida en ${result.resultingRegisteredQuantity}. No existía un saldo previo.`;
  }
  return `Reconciliación registrada: saldo previo ${result.previousRegisteredQuantity}, diferencia ${result.difference ?? ""}, nuevo saldo ${result.resultingRegisteredQuantity}.`;
}

export function InventoryItemOperations({
  item,
  onUnauthorized,
  onStateRefresh,
  onAuthoritativeMutation,
  onItemUnavailable,
}: InventoryItemOperationsProps) {
  const [countQuantity, setCountQuantity] = useState("");
  const [entryQuantity, setEntryQuantity] = useState("");
  const [exitQuantity, setExitQuantity] = useState("");
  const [wasteQuantity, setWasteQuantity] = useState("");
  const [countObservation, setCountObservation] =
    useState<InventoryCountObservation | null>(null);
  const [intent, setIntentState] = useState<InventoryOperationIntent | null>(
    null,
  );
  const intentRef = useRef<InventoryOperationIntent | null>(null);
  const [notice, setNotice] = useState<Notice | null>(null);

  const setIntent = useCallback((next: InventoryOperationIntent | null) => {
    intentRef.current = next;
    setIntentState(next);
  }, []);

  const executeIntent = useCallback(
    async (current: InventoryOperationIntent) => {
      let antiforgeryToken: string;
      try {
        antiforgeryToken = await getAntiforgeryToken();
      } catch (error) {
        setIntent(null);
        if (error instanceof SessionProblemError && error.status === 401) {
          discardAntiforgeryToken();
          onUnauthorized();
          return;
        }
        setNotice({
          kind: "functional-error",
          message: "No se pudo preparar la solicitud. Intentá nuevamente.",
        });
        return;
      }

      try {
        if (current.kind === "count") {
          const result = await recordInventoryCount(
            current.itemId,
            current.body as { observedQuantity: string },
            current.idempotencyKey,
            antiforgeryToken,
          );
          setIntent(null);
          setCountObservation(result);
          setCountQuantity("");
          setNotice({
            kind: "success",
            message: `Conteo registrado: ${result.observedQuantity} ${result.observedOperationalUnit}. El saldo no fue modificado.`,
          });
          await onStateRefresh();
          return;
        }

        if (current.kind === "reconcile") {
          const result = await reconcileInventoryCount(
            current.itemId,
            current.body,
            current.idempotencyKey,
            antiforgeryToken,
          );
          setIntent(null);
          setCountObservation(null);
          setNotice({
            kind: "success",
            message: reconciliationMessage(result),
          });
          await onAuthoritativeMutation(current.itemId);
          return;
        }

        const request = current.body as { quantity: string };
        const result =
          current.kind === "entry"
            ? await recordInventoryEntry(
                current.itemId,
                request,
                current.idempotencyKey,
                antiforgeryToken,
              )
            : current.kind === "manual-exit"
              ? await recordManualInventoryExit(
                  current.itemId,
                  request,
                  current.idempotencyKey,
                  antiforgeryToken,
                )
              : await recordInventoryWaste(
                  current.itemId,
                  request,
                  current.idempotencyKey,
                  antiforgeryToken,
                );
        setIntent(null);
        setCountObservation(null);
        if (current.kind === "entry") setEntryQuantity("");
        if (current.kind === "manual-exit") setExitQuantity("");
        if (current.kind === "waste") setWasteQuantity("");
        setNotice({
          kind: "success",
          message: `${operationLabel(current.kind)[0]!.toUpperCase()}${operationLabel(current.kind).slice(1)} registrada. Saldo autoritativo resultante: ${result.resultingRegisteredQuantity} ${item.operationalUnit}.`,
        });
        await onAuthoritativeMutation(current.itemId);
      } catch (error) {
        if (error instanceof InventoryProblemError) {
          if (error.status === 401) {
            setIntent(null);
            discardAntiforgeryToken();
            onUnauthorized();
            return;
          }
          if (error.status === 408 || error.status >= 500) {
            setIntent({ ...current, phase: "uncertain" });
            setNotice({
              kind: "uncertain",
              message: `No se pudo confirmar el resultado de la ${operationLabel(current.kind)}.`,
            });
            return;
          }

          setIntent(null);
          if (error.problem.code?.endsWith(".antiforgery_invalid") === true) {
            discardAntiforgeryToken();
          }
          if (error.status === 403) {
            setNotice({
              kind: "functional-error",
              message:
                "Esta Identity no tiene autorización para operar Inventario.",
            });
            return;
          }
          if (error.status === 404) {
            setCountObservation(null);
            setNotice({
              kind: "functional-error",
              message: "El elemento o conteo ya no está disponible.",
            });
            onItemUnavailable();
            return;
          }
          if (
            current.kind === "reconcile" &&
            (error.problem.code ===
              "inventory.reconciliation.count_invalidated" ||
              error.problem.code ===
                "inventory.reconciliation.observation_invalidated")
          ) {
            setCountObservation(null);
            setNotice({
              kind: "functional-error",
              message:
                "El conteo quedó invalidado porque la existencia cambió. Se requiere un nuevo conteo físico antes de reconciliar.",
            });
            await onAuthoritativeMutation(current.itemId);
            return;
          }

          setNotice({
            kind: "functional-error",
            message: knownFailureMessage(current.kind, error),
          });
          if (error.status === 409) {
            await onAuthoritativeMutation(current.itemId);
          }
          return;
        }

        setIntent({ ...current, phase: "uncertain" });
        setNotice({
          kind: "uncertain",
          message: `No se pudo confirmar el resultado de la ${operationLabel(current.kind)}.`,
        });
      }
    },
    [
      item.operationalUnit,
      onAuthoritativeMutation,
      onItemUnavailable,
      onStateRefresh,
      onUnauthorized,
      setIntent,
    ],
  );

  function beginQuantityIntent(
    event: FormEvent<HTMLFormElement>,
    kind: QuantityOperationKind,
    quantity: string,
  ) {
    event.preventDefault();
    if (intentRef.current !== null) return;
    const valid =
      kind === "count"
        ? isObservedQuantity(quantity)
        : isPositiveQuantity(quantity);
    if (!valid) {
      setNotice({
        kind: "functional-error",
        message:
          kind === "count"
            ? "Ingresá una cantidad observada válida, igual o mayor que cero."
            : "Ingresá una cantidad positiva válida.",
      });
      return;
    }

    const next: InventoryOperationIntent = {
      phase: "submitting",
      kind,
      itemId: item.itemId,
      body: kind === "count" ? { observedQuantity: quantity } : { quantity },
      idempotencyKey: crypto.randomUUID(),
    };
    setNotice(null);
    setIntent(next);
    void executeIntent(next);
  }

  function beginReconciliation() {
    if (intentRef.current !== null || countObservation === null) return;
    const next: InventoryOperationIntent = {
      phase: "submitting",
      kind: "reconcile",
      itemId: item.itemId,
      body: { countObservationId: countObservation.countObservationId },
      idempotencyKey: crypto.randomUUID(),
    };
    setNotice(null);
    setIntent(next);
    void executeIntent(next);
  }

  function retryUncertain() {
    const current = intentRef.current;
    if (current?.phase !== "uncertain") return;
    const submitting = { ...current, phase: "submitting" as const };
    setNotice(null);
    setIntent(submitting);
    void executeIntent(submitting);
  }

  const blocked = intent !== null;
  const quantityForms = item.quantityEstablished
    ? ([
        {
          kind: "entry" as const,
          title: "Entrada",
          label: `Cantidad de entrada para ${item.operationalName}`,
          value: entryQuantity,
          setValue: setEntryQuantity,
          button: "Registrar entrada",
        },
        {
          kind: "manual-exit" as const,
          title: "Salida manual",
          label: `Cantidad de salida manual para ${item.operationalName}`,
          value: exitQuantity,
          setValue: setExitQuantity,
          button: "Registrar salida manual",
        },
        {
          kind: "waste" as const,
          title: "Merma",
          label: `Cantidad de merma para ${item.operationalName}`,
          value: wasteQuantity,
          setValue: setWasteQuantity,
          button: "Registrar merma",
        },
      ] as const)
    : [];

  return (
    <div className="inventory-item-operations">
      <form
        className="inventory-operation-form"
        aria-label={`Conteo físico de ${item.operationalName}`}
        onSubmit={(event) => beginQuantityIntent(event, "count", countQuantity)}
      >
        <h5>Conteo físico</h5>
        <label>
          Cantidad observada para {item.operationalName}
          <input
            inputMode="decimal"
            value={countQuantity}
            disabled={blocked}
            onChange={(event) => setCountQuantity(event.target.value)}
          />
        </label>
        <button type="submit" disabled={blocked}>
          Registrar conteo
        </button>
        <p>Observar no modifica la existencia registrada.</p>
      </form>

      {countObservation !== null && (
        <section
          className="inventory-reconciliation"
          aria-label={`Reconciliar conteo de ${item.operationalName}`}
        >
          <h5>Reconciliación</h5>
          <p>
            Cantidad observada:{" "}
            <strong>
              {countObservation.observedQuantity}{" "}
              {countObservation.observedOperationalUnit}
            </strong>
          </p>
          <button
            type="button"
            disabled={blocked}
            onClick={beginReconciliation}
          >
            Reconciliar conteo
          </button>
        </section>
      )}

      {quantityForms.length > 0 && (
        <div className="inventory-everyday-operations">
          {quantityForms.map((form) => (
            <form
              className="inventory-operation-form"
              aria-label={`${form.title} de ${item.operationalName}`}
              key={form.kind}
              onSubmit={(event) =>
                beginQuantityIntent(event, form.kind, form.value)
              }
            >
              <h5>{form.title}</h5>
              <label>
                {form.label}
                <input
                  inputMode="decimal"
                  value={form.value}
                  disabled={blocked}
                  onChange={(event) => form.setValue(event.target.value)}
                />
              </label>
              <button type="submit" disabled={blocked}>
                {form.button}
              </button>
            </form>
          ))}
        </div>
      )}

      {notice !== null && (
        <div
          className={`notice notice--${notice.kind}`}
          role={notice.kind === "success" ? "status" : "alert"}
        >
          <p>{notice.message}</p>
          {intent?.phase === "uncertain" && (
            <button type="button" onClick={retryUncertain}>
              Reintentar misma operación
            </button>
          )}
        </div>
      )}
    </div>
  );
}
