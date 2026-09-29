import { type FormEvent, useCallback, useRef, useState } from "react";
import {
  discardAntiforgeryToken,
  getAntiforgeryToken,
  SessionProblemError,
} from "../identity/sessionClient.ts";
import {
  correctInventoryUnit,
  deleteInventoryItem,
  InventoryProblemError,
  reactivateInventoryItem,
  retireInventoryItem,
  type InventoryConfigurationItem,
} from "./inventoryClient.ts";

interface InventoryConfigurationItemControlsProps {
  item: InventoryConfigurationItem;
  onUnauthorized: () => void;
  onAuthoritativeMutation: () => Promise<void>;
  onStaleState: (message: string) => void;
}

type IntentPhase = "submitting" | "uncertain";
type ConfigIntent =
  | {
      phase: IntentPhase;
      kind: "retire";
      itemId: string;
      body: { expectedCurrentIsActive: boolean };
      idempotencyKey: string;
    }
  | {
      phase: IntentPhase;
      kind: "reactivate";
      itemId: string;
      body: {
        expectedCurrentIsActive: boolean;
        newOperationalName: string | null;
      };
      idempotencyKey: string;
    }
  | {
      phase: IntentPhase;
      kind: "unit";
      itemId: string;
      body: { expectedCurrentUnit: string; newUnit: string };
      idempotencyKey: string;
    }
  | {
      phase: IntentPhase;
      kind: "delete";
      itemId: string;
      idempotencyKey: string;
    };

type NoticeKind = "success" | "functional-error" | "uncertain";

interface Notice {
  kind: NoticeKind;
  message: string;
}

function isUncertain(error: unknown): boolean {
  return (
    error instanceof InventoryProblemError &&
    (error.status === 408 || error.status >= 500)
  );
}

function intentLabel(kind: ConfigIntent["kind"]): string {
  switch (kind) {
    case "retire":
      return "retiro";
    case "reactivate":
      return "reactivación";
    case "unit":
      return "corrección de unidad";
    case "delete":
      return "eliminación definitiva";
  }
}

function knownFailureMessage(
  kind: ConfigIntent["kind"],
  error: InventoryProblemError,
): string {
  switch (error.problem.code) {
    case "inventory.item.reactivation_name_conflict":
      return "Otro elemento activo ya usa ese nombre. Elegí un nombre operacional de reemplazo para reactivar este elemento.";
    case "inventory.item.unit_correction_requires_replacement":
      return "La unidad ya no puede cambiarse porque este elemento tiene historial de movimientos. Retirá este elemento y creá otro con la unidad corregida.";
    case "inventory.item.delete_movement_history_conflict":
      return "El elemento no puede eliminarse porque tiene historial de movimientos que debe conservarse.";
    case "inventory.item.not_found":
      return "El elemento ya no está disponible. La configuración se actualizó.";
    case "inventory.item.lifecycle_state_conflict":
    case "inventory.item.lifecycle_stale":
      return "El estado del elemento cambió. La configuración se actualizó; elegí una nueva acción.";
    default:
      return `No se pudo completar la ${intentLabel(kind)}. Revisá el estado actual e intentá nuevamente.`;
  }
}

export function InventoryConfigurationItemControls({
  item,
  onUnauthorized,
  onAuthoritativeMutation,
  onStaleState,
}: InventoryConfigurationItemControlsProps) {
  const [unitObserved, setUnitObserved] = useState(item.operationalUnit);
  const [newUnit, setNewUnit] = useState("");
  const [replacementName, setReplacementName] = useState("");
  const [replacementNameRequired, setReplacementNameRequired] = useState(false);
  const [confirmDelete, setConfirmDelete] = useState(false);
  const [intent, setIntentState] = useState<ConfigIntent | null>(null);
  const intentRef = useRef<ConfigIntent | null>(null);
  const [notice, setNotice] = useState<Notice | null>(null);

  const setIntent = useCallback((next: ConfigIntent | null) => {
    intentRef.current = next;
    setIntentState(next);
  }, []);

  const executeIntent = useCallback(
    async (current: ConfigIntent) => {
      let antiforgeryToken: string;
      try {
        antiforgeryToken = await getAntiforgeryToken();
      } catch (error) {
        setIntent(null);
        if (error instanceof SessionProblemError && error.status === 401) {
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
        if (current.kind === "retire") {
          await retireInventoryItem(
            current.itemId,
            current.body.expectedCurrentIsActive,
            current.idempotencyKey,
            antiforgeryToken,
          );
        } else if (current.kind === "reactivate") {
          await reactivateInventoryItem(
            current.itemId,
            current.body.expectedCurrentIsActive,
            current.body.newOperationalName,
            current.idempotencyKey,
            antiforgeryToken,
          );
        } else if (current.kind === "unit") {
          await correctInventoryUnit(
            current.itemId,
            current.body.expectedCurrentUnit,
            current.body.newUnit,
            current.idempotencyKey,
            antiforgeryToken,
          );
        } else {
          await deleteInventoryItem(
            current.itemId,
            current.idempotencyKey,
            antiforgeryToken,
          );
        }

        setIntent(null);
        setConfirmDelete(false);
        setReplacementNameRequired(false);
        setReplacementName("");
        setNewUnit("");
        setNotice({
          kind: "success",
          message:
            current.kind === "delete"
              ? "Elemento eliminado definitivamente."
              : "Cambio aplicado correctamente.",
        });
        await onAuthoritativeMutation();
      } catch (error) {
        if (isUncertain(error)) {
          setIntent({ ...current, phase: "uncertain" });
          setNotice({
            kind: "uncertain",
            message: `No se pudo confirmar el resultado de la ${intentLabel(current.kind)}.`,
          });
          return;
        }

        if (error instanceof InventoryProblemError) {
          setIntent(null);
          if (error.status === 401) {
            discardAntiforgeryToken();
            onUnauthorized();
            return;
          }
          if (error.problem.code?.endsWith(".antiforgery_invalid") === true) {
            discardAntiforgeryToken();
          }
          if (error.status === 403) {
            setNotice({
              kind: "functional-error",
              message:
                "Tu usuario no tiene autorización para configurar Inventario.",
            });
            return;
          }
          if (
            current.kind === "reactivate" &&
            error.problem.code === "inventory.item.reactivation_name_conflict"
          ) {
            setReplacementNameRequired(true);
          }
          if (error.status === 404) {
            onStaleState(
              "El elemento ya no está disponible. La configuración se actualizó.",
            );
            setNotice({
              kind: "functional-error",
              message:
                "El elemento ya no está disponible. La configuración se actualizó.",
            });
            await onAuthoritativeMutation();
            return;
          }
          setNotice({
            kind: "functional-error",
            message: knownFailureMessage(current.kind, error),
          });
          if (error.status === 409) await onAuthoritativeMutation();
          return;
        }

        setIntent({ ...current, phase: "uncertain" });
        setNotice({
          kind: "uncertain",
          message: `No se pudo confirmar el resultado de la ${intentLabel(current.kind)}.`,
        });
      }
    },
    [onAuthoritativeMutation, onStaleState, onUnauthorized, setIntent],
  );

  function beginIntent(next: ConfigIntent) {
    if (intentRef.current !== null) return;
    setNotice(null);
    setIntent(next);
    void executeIntent(next);
  }

  function submitUnitCorrection(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const expectedCurrentUnit = unitObserved.trim();
    const requestedUnit = newUnit.trim();
    if (expectedCurrentUnit === "" || requestedUnit === "") {
      setNotice({
        kind: "functional-error",
        message: "Ingresá la Unidad observada y la nueva Unidad.",
      });
      return;
    }
    beginIntent({
      phase: "submitting",
      kind: "unit",
      itemId: item.itemId,
      body: { expectedCurrentUnit, newUnit: requestedUnit },
      idempotencyKey: crypto.randomUUID(),
    });
  }

  function submitReactivation(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const requestedName = replacementName.trim();
    if (replacementNameRequired && requestedName === "") {
      setNotice({
        kind: "functional-error",
        message: "Ingresá un nombre operacional de reemplazo.",
      });
      return;
    }
    beginIntent({
      phase: "submitting",
      kind: "reactivate",
      itemId: item.itemId,
      body: {
        expectedCurrentIsActive: false,
        newOperationalName: replacementNameRequired ? requestedName : null,
      },
      idempotencyKey: crypto.randomUUID(),
    });
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

  return (
    <div className="inventory-configuration-controls">
      <p aria-label={`Estado del ciclo de vida de ${item.operationalName}`}>
        Estado: <strong>{item.isActive ? "Activo" : "Retirado"}</strong>
      </p>
      {item.isActive && (
        <p aria-label={`Preparación para operar de ${item.operationalName}`}>
          {item.ordinaryOperationReady
            ? "Listo para movimientos"
            : "Requiere conteo y reconciliación"}
        </p>
      )}
      <p aria-label={`Unidad operacional de ${item.operationalName}`}>
        Unidad operacional: <strong>{item.operationalUnit}</strong>
      </p>

      {item.isActive ? (
        <>
          <button
            type="button"
            disabled={blocked}
            aria-label={`Retirar ${item.operationalName}`}
            onClick={() =>
              beginIntent({
                phase: "submitting",
                kind: "retire",
                itemId: item.itemId,
                body: { expectedCurrentIsActive: true },
                idempotencyKey: crypto.randomUUID(),
              })
            }
          >
            Retirar
          </button>
          <p>
            Retirar conserva el historial y quita el elemento de la operación.
            Dejará de haber una existencia actual establecida. Si se reactiva,
            será necesario realizar un nuevo conteo y reconciliación.
          </p>
        </>
      ) : (
        <form onSubmit={submitReactivation}>
          <button
            type="submit"
            disabled={blocked}
            aria-label={`Reactivar ${item.operationalName}`}
          >
            Reactivar
          </button>
          <p>
            Se reactivará como Activo y requerirá un nuevo conteo físico y
            reconciliación.
          </p>
          {replacementNameRequired && (
            <label>
              Nombre operacional de reemplazo para Reactivar
              <input
                aria-label="Nombre operacional de reemplazo para Reactivar"
                value={replacementName}
                disabled={blocked}
                onChange={(event) => setReplacementName(event.target.value)}
              />
            </label>
          )}
        </form>
      )}

      {item.unitCorrectionEligible ? (
        <form onSubmit={submitUnitCorrection}>
          <h5>Corregir Unidad</h5>
          <label>
            Unidad observada actualmente
            <input
              aria-label={`Unidad observada actualmente de ${item.operationalName}`}
              value={unitObserved}
              disabled={blocked}
              onChange={(event) => setUnitObserved(event.target.value)}
            />
          </label>
          <label>
            Nueva Unidad
            <input
              aria-label={`Nueva Unidad de ${item.operationalName}`}
              value={newUnit}
              disabled={blocked}
              onChange={(event) => setNewUnit(event.target.value)}
            />
          </label>
          <button type="submit" disabled={blocked}>
            Corregir Unidad
          </button>
          <p>
            No se convierte ninguna cantidad. La nueva Unidad cambia la
            interpretación futura y puede requerir un nuevo conteo.
          </p>
        </form>
      ) : (
        <p>
          La unidad ya no puede cambiarse porque este elemento tiene historial
          de movimientos. Para usar otra unidad: retiralo, creá un elemento
          nuevo y establecé su existencia mediante conteo y reconciliación.
        </p>
      )}

      {item.deleteEligible && (
        <div>
          <button
            type="button"
            className="secondary-button"
            disabled={blocked}
            aria-label={`Eliminar definitivamente ${item.operationalName}`}
            onClick={() => setConfirmDelete(true)}
          >
            Eliminar definitivamente
          </button>
          <p>
            Elimina permanentemente la configuración y sólo es posible sin
            historial de movimientos.
          </p>
          {confirmDelete && (
            <div
              role="alertdialog"
              aria-label="Confirmar eliminación definitiva"
            >
              <p>
                Esta acción elimina definitivamente este elemento. No se puede
                deshacer.
              </p>
              <button
                type="button"
                disabled={blocked}
                onClick={() =>
                  beginIntent({
                    phase: "submitting",
                    kind: "delete",
                    itemId: item.itemId,
                    idempotencyKey: crypto.randomUUID(),
                  })
                }
              >
                Confirmar eliminación definitiva
              </button>
              <button
                type="button"
                disabled={blocked}
                onClick={() => setConfirmDelete(false)}
              >
                Cancelar
              </button>
            </div>
          )}
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
