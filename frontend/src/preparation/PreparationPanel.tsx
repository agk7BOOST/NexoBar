import { useCallback, useEffect, useRef, useState } from "react";
import {
  discardAntiforgeryToken,
  getAntiforgeryToken,
  listPreparationDestinations,
  SessionProblemError,
  type PreparationDestination,
} from "../identity/sessionClient.ts";
import {
  listPreparationWork,
  markPreparationQuantityReady,
  PreparationProblemError,
  startPreparationQuantity,
  type PreparationCommandResult,
  type PreparationWork,
} from "./preparationClient.ts";

interface PreparationPanelProps {
  onUnauthorized: () => void;
}

type PreparationCommandKind = "start" | "ready";
type PreparationIntentPhase = "submitting" | "uncertain";

interface PreparationIntent {
  phase: PreparationIntentPhase;
  kind: PreparationCommandKind;
  workId: string;
  quantity: number;
  idempotencyKey: string;
}

interface QuantityInputs {
  start: string;
  ready: string;
}

interface WorkMessage {
  kind: "error" | "uncertain";
  text: string;
}

interface RefreshOptions {
  preferredDestinationId?: string;
  preserveMessage?: boolean;
  clearWork?: boolean;
}

const forbiddenMessage =
  "Esta Identity no tiene autorización para esa preparación.";

function sourceQuantity(
  item: PreparationWork,
  kind: PreparationCommandKind,
): number {
  return kind === "start" ? item.pendingQuantity : item.inPreparationQuantity;
}

function actionName(kind: PreparationCommandKind): string {
  return kind === "start" ? "iniciar" : "marcar listo";
}

function inputLabel(
  item: PreparationWork,
  kind: PreparationCommandKind,
): string {
  const action = kind === "start" ? "iniciar" : "marcar lista";
  return `Cantidad a ${action} de ${item.productOperationalName}, incorporación ${item.incorporationOrdinal}, ${item.context}, ${item.instruction ?? "sin instrucción"}`;
}

function buttonLabel(
  item: PreparationWork,
  kind: PreparationCommandKind,
): string {
  const action = kind === "start" ? "Iniciar" : "Marcar listo";
  return `${action} ${item.productOperationalName}, incorporación ${item.incorporationOrdinal}, ${item.context}, ${item.instruction ?? "sin instrucción"}`;
}

function updatedWork(
  item: PreparationWork,
  result: PreparationCommandResult,
): PreparationWork {
  return {
    ...item,
    totalQuantity: result.totalQuantity,
    pendingQuantity: result.pendingQuantity,
    inPreparationQuantity: result.inPreparationQuantity,
    readyQuantity: result.readyQuantity,
  };
}

export function PreparationPanel({ onUnauthorized }: PreparationPanelProps) {
  const [destinations, setDestinations] = useState<PreparationDestination[]>(
    [],
  );
  const [selectedId, setSelectedId] = useState("");
  const [work, setWork] = useState<PreparationWork[]>([]);
  const [quantityInputs, setQuantityInputs] = useState<
    Record<string, QuantityInputs>
  >({});
  const [intents, setIntents] = useState<Record<string, PreparationIntent>>({});
  const [workMessages, setWorkMessages] = useState<Record<string, WorkMessage>>(
    {},
  );
  const [isLoadingDestinations, setIsLoadingDestinations] = useState(true);
  const [isLoadingWork, setIsLoadingWork] = useState(false);
  const [message, setMessage] = useState<string | null>(null);
  const workRequestSequence = useRef(0);
  const selectedIdRef = useRef("");
  const intentsRef = useRef<Record<string, PreparationIntent>>({});

  const cancelWorkRequests = useCallback(() => {
    workRequestSequence.current++;
  }, []);

  const replaceIntents = useCallback(
    (next: Record<string, PreparationIntent>) => {
      intentsRef.current = next;
      setIntents(next);
    },
    [],
  );

  const setIntent = useCallback(
    (intent: PreparationIntent) => {
      replaceIntents({ ...intentsRef.current, [intent.workId]: intent });
    },
    [replaceIntents],
  );

  const clearIntent = useCallback(
    (workId: string) => {
      const next = { ...intentsRef.current };
      delete next[workId];
      replaceIntents(next);
    },
    [replaceIntents],
  );

  const setWorkMessage = useCallback(
    (workId: string, nextMessage: WorkMessage | null) => {
      setWorkMessages((current) => {
        const next = { ...current };
        if (nextMessage === null) delete next[workId];
        else next[workId] = nextMessage;
        return next;
      });
    },
    [],
  );

  const setAuthoritativeWork = useCallback((loaded: PreparationWork[]) => {
    setWork(loaded);
    setQuantityInputs(() => {
      const next: Record<string, QuantityInputs> = {};
      for (const item of loaded) {
        const intent = intentsRef.current[item.workId];
        next[item.workId] = {
          start:
            intent?.kind === "start"
              ? String(intent.quantity)
              : item.pendingQuantity > 0
                ? String(item.pendingQuantity)
                : "",
          ready:
            intent?.kind === "ready"
              ? String(intent.quantity)
              : item.inPreparationQuantity > 0
                ? String(item.inPreparationQuantity)
                : "",
        };
      }
      return next;
    });
  }, []);

  const handleUnauthorized = useCallback(() => {
    workRequestSequence.current++;
    discardAntiforgeryToken();
    replaceIntents({});
    setWorkMessages({});
    onUnauthorized();
  }, [onUnauthorized, replaceIntents]);

  const refreshPreparationWorkForSelectedDestination = useCallback(
    async (options: RefreshOptions = {}) => {
      const sequence = ++workRequestSequence.current;
      setIsLoadingDestinations(true);
      setIsLoadingWork(true);
      if (!options.preserveMessage) setMessage(null);
      if (options.clearWork) setAuthoritativeWork([]);

      try {
        const loadedDestinations = await listPreparationDestinations();
        if (sequence !== workRequestSequence.current) return;

        const preferredId =
          options.preferredDestinationId ?? selectedIdRef.current;
        const preferredStillAvailable = loadedDestinations.some(
          (destination) =>
            destination.preparationResponsibilityId === preferredId,
        );
        const nextSelectedId = preferredStillAvailable
          ? preferredId
          : loadedDestinations.length === 1
            ? loadedDestinations[0].preparationResponsibilityId
            : "";

        setDestinations(loadedDestinations);
        selectedIdRef.current = nextSelectedId;
        setSelectedId(nextSelectedId);
        setIsLoadingDestinations(false);

        if (nextSelectedId === "") {
          setAuthoritativeWork([]);
          setIsLoadingWork(false);
          return;
        }

        const loadedWork = await listPreparationWork(nextSelectedId);
        if (sequence !== workRequestSequence.current) return;
        setAuthoritativeWork(loadedWork);
      } catch (error) {
        if (sequence !== workRequestSequence.current) return;
        if (
          (error instanceof PreparationProblemError ||
            error instanceof SessionProblemError) &&
          error.status === 401
        ) {
          handleUnauthorized();
          return;
        }

        if (
          (error instanceof PreparationProblemError ||
            error instanceof SessionProblemError) &&
          error.status === 403
        ) {
          setAuthoritativeWork([]);
          setMessage(forbiddenMessage);
        } else {
          setMessage("No se pudo consultar el trabajo de preparación.");
        }
      } finally {
        if (sequence === workRequestSequence.current) {
          setIsLoadingDestinations(false);
          setIsLoadingWork(false);
        }
      }
    },
    [handleUnauthorized, setAuthoritativeWork],
  );

  useEffect(() => {
    const scheduledRefresh = window.setTimeout(() => {
      void refreshPreparationWorkForSelectedDestination({ clearWork: true });
    }, 0);
    return () => {
      window.clearTimeout(scheduledRefresh);
      cancelWorkRequests();
    };
  }, [cancelWorkRequests, refreshPreparationWorkForSelectedDestination]);

  const applyCommandResult = useCallback((result: PreparationCommandResult) => {
    setWork((current) =>
      current.map((item) =>
        item.workId === result.workId ? updatedWork(item, result) : item,
      ),
    );
    setQuantityInputs((current) => ({
      ...current,
      [result.workId]: {
        start: result.pendingQuantity > 0 ? String(result.pendingQuantity) : "",
        ready:
          result.inPreparationQuantity > 0
            ? String(result.inPreparationQuantity)
            : "",
      },
    }));
  }, []);

  const executeIntent = useCallback(
    async (intent: PreparationIntent) => {
      try {
        const antiforgeryToken = await getAntiforgeryToken();
        const result =
          intent.kind === "start"
            ? await startPreparationQuantity({
                workId: intent.workId,
                quantity: intent.quantity,
                idempotencyKey: intent.idempotencyKey,
                antiforgeryToken,
              })
            : await markPreparationQuantityReady({
                workId: intent.workId,
                quantity: intent.quantity,
                idempotencyKey: intent.idempotencyKey,
                antiforgeryToken,
              });

        if (result.workId !== intent.workId) {
          throw new Error("Preparation returned another Work.");
        }

        clearIntent(intent.workId);
        setWorkMessage(intent.workId, null);
        applyCommandResult(result);
        void refreshPreparationWorkForSelectedDestination();
      } catch (error) {
        if (
          (error instanceof PreparationProblemError ||
            error instanceof SessionProblemError) &&
          error.status === 401
        ) {
          handleUnauthorized();
          return;
        }

        if (error instanceof PreparationProblemError) {
          if (error.status === 408 || error.status >= 500) {
            setIntent({ ...intent, phase: "uncertain" });
            setWorkMessage(intent.workId, {
              kind: "uncertain",
              text: "No se pudo confirmar el resultado.",
            });
            return;
          }

          clearIntent(intent.workId);

          if (error.status === 409) {
            const isIdempotencyConflict =
              error.problem.code?.endsWith(".idempotency_key_conflict") ===
              true;
            setWorkMessage(intent.workId, {
              kind: "error",
              text: isIdempotencyConflict
                ? "La operación no coincide con el intento original."
                : "El estado de preparación cambió. Se actualizó la información.",
            });
            void refreshPreparationWorkForSelectedDestination({
              preserveMessage: true,
            });
            return;
          }

          if (error.status === 403) {
            setWorkMessage(intent.workId, {
              kind: "error",
              text: forbiddenMessage,
            });
            return;
          }

          if (error.status === 404) {
            setWorkMessage(intent.workId, null);
            setMessage("El trabajo ya no está disponible.");
            void refreshPreparationWorkForSelectedDestination({
              preserveMessage: true,
            });
            return;
          }

          const antiforgeryInvalid =
            error.problem.code?.endsWith(".antiforgery_invalid") === true;
          if (antiforgeryInvalid) discardAntiforgeryToken();
          setWorkMessage(intent.workId, {
            kind: "error",
            text: antiforgeryInvalid
              ? "No se pudo validar la solicitud. Volvé a intentarlo."
              : "No se pudo realizar la operación de preparación.",
          });
          return;
        }

        if (error instanceof SessionProblemError) {
          clearIntent(intent.workId);
          setWorkMessage(intent.workId, {
            kind: "error",
            text:
              error.status === 403
                ? forbiddenMessage
                : "No se pudo obtener la protección de la solicitud.",
          });
          return;
        }

        setIntent({ ...intent, phase: "uncertain" });
        setWorkMessage(intent.workId, {
          kind: "uncertain",
          text: "No se pudo confirmar el resultado.",
        });
      }
    },
    [
      applyCommandResult,
      clearIntent,
      handleUnauthorized,
      refreshPreparationWorkForSelectedDestination,
      setIntent,
      setWorkMessage,
    ],
  );

  const submitNewIntent = useCallback(
    (item: PreparationWork, kind: PreparationCommandKind) => {
      if (intentsRef.current[item.workId] !== undefined) return;

      const availableQuantity = sourceQuantity(item, kind);
      const rawQuantity = quantityInputs[item.workId]?.[kind] ?? "";
      const quantity = Number(rawQuantity);
      if (
        rawQuantity.trim() === "" ||
        !Number.isInteger(quantity) ||
        quantity <= 0 ||
        quantity > availableQuantity
      ) {
        setWorkMessage(item.workId, {
          kind: "error",
          text: `Ingresá una cantidad entera entre 1 y ${availableQuantity} para ${actionName(kind)}.`,
        });
        return;
      }

      const intent: PreparationIntent = {
        phase: "submitting",
        kind,
        workId: item.workId,
        quantity,
        idempotencyKey: crypto.randomUUID(),
      };
      setMessage(null);
      setWorkMessage(item.workId, null);
      setIntent(intent);
      void executeIntent(intent);
    },
    [executeIntent, quantityInputs, setIntent, setWorkMessage],
  );

  const retryIntent = useCallback(
    (workId: string) => {
      const currentIntent = intentsRef.current[workId];
      if (currentIntent?.phase !== "uncertain") return;
      const submittingIntent: PreparationIntent = {
        ...currentIntent,
        phase: "submitting",
      };
      setWorkMessage(workId, null);
      setIntent(submittingIntent);
      void executeIntent(submittingIntent);
    },
    [executeIntent, setIntent, setWorkMessage],
  );

  function renderAction(item: PreparationWork, kind: PreparationCommandKind) {
    const availableQuantity = sourceQuantity(item, kind);
    if (availableQuantity === 0) return null;

    const intent = intents[item.workId];
    const isWorkBlocked = intent !== undefined;
    const fieldId = `preparation-${kind}-quantity-${item.workId}`;
    const messageId = `preparation-message-${item.workId}`;
    return (
      <form
        className="preparation-action"
        aria-label={`${kind === "start" ? "Iniciar" : "Marcar listo"} ${item.productOperationalName}, incorporación ${item.incorporationOrdinal}, ${item.instruction ?? "sin instrucción"}`}
        noValidate
        onSubmit={(event) => {
          event.preventDefault();
          submitNewIntent(item, kind);
        }}
      >
        <label htmlFor={fieldId}>
          {kind === "start" ? "Cantidad a iniciar" : "Cantidad a marcar lista"}
        </label>
        <input
          id={fieldId}
          type="number"
          min="1"
          max={availableQuantity}
          step="1"
          value={quantityInputs[item.workId]?.[kind] ?? ""}
          disabled={isWorkBlocked}
          aria-label={inputLabel(item, kind)}
          aria-describedby={workMessages[item.workId] ? messageId : undefined}
          onChange={(event) => {
            const value = event.target.value;
            setQuantityInputs((current) => ({
              ...current,
              [item.workId]: {
                start: current[item.workId]?.start ?? "",
                ready: current[item.workId]?.ready ?? "",
                [kind]: value,
              },
            }));
          }}
        />
        <button
          type="submit"
          disabled={isWorkBlocked}
          aria-label={buttonLabel(item, kind)}
        >
          {kind === "start" ? "Iniciar" : "Marcar listo"}
        </button>
      </form>
    );
  }

  return (
    <section className="panel" aria-labelledby="preparation-heading">
      <div className="section-heading">
        <h2 id="preparation-heading">Preparación</h2>
        <button
          type="button"
          className="secondary-button"
          disabled={isLoadingDestinations || isLoadingWork}
          onClick={() =>
            void refreshPreparationWorkForSelectedDestination({
              clearWork: false,
            })
          }
        >
          Actualizar preparación
        </button>
      </div>

      {isLoadingDestinations && destinations.length === 0 && (
        <p>Cargando destinos…</p>
      )}
      {!isLoadingDestinations && destinations.length === 0 && !message && (
        <p>No hay destinos de preparación habilitados para esta Identity.</p>
      )}
      {destinations.length > 1 && (
        <label className="preparation-destination-selector">
          Destino de preparación
          <select
            value={selectedId}
            onChange={(event) => {
              const destinationId = event.target.value;
              selectedIdRef.current = destinationId;
              setSelectedId(destinationId);
              if (destinationId === "") {
                workRequestSequence.current++;
                setAuthoritativeWork([]);
                setMessage(null);
                setIsLoadingWork(false);
              } else {
                void refreshPreparationWorkForSelectedDestination({
                  preferredDestinationId: destinationId,
                  clearWork: true,
                });
              }
            }}
          >
            <option value="">Seleccionar destino</option>
            {destinations.map((destination) => (
              <option
                key={destination.preparationResponsibilityId}
                value={destination.preparationResponsibilityId}
              >
                {destination.operationalName}
              </option>
            ))}
          </select>
        </label>
      )}
      {destinations.length === 1 && (
        <p>
          Destino: <strong>{destinations[0].operationalName}</strong>
        </p>
      )}
      {message && (
        <p className="notice notice--functional-error" role="alert">
          {message}
        </p>
      )}
      {isLoadingWork && work.length === 0 && <p>Cargando trabajo…</p>}
      {!isLoadingWork && selectedId !== "" && !message && work.length === 0 && (
        <p>No hay trabajo de preparación para este destino.</p>
      )}
      {work.length > 0 && (
        <div className="table-scroll">
          <table>
            <thead>
              <tr>
                <th>Producto</th>
                <th>Contexto / referencia</th>
                <th>Instrucción</th>
                <th>Cantidades</th>
                <th>Acciones</th>
              </tr>
            </thead>
            <tbody>
              {work.map((item) => {
                const intent = intents[item.workId];
                const itemMessage = workMessages[item.workId];
                const messageId = `preparation-message-${item.workId}`;
                return (
                  <tr
                    key={item.workId}
                    aria-busy={intent?.phase === "submitting"}
                  >
                    <td>
                      <strong>{item.productOperationalName}</strong>
                      <br />
                      Incorporación {item.incorporationOrdinal}
                    </td>
                    <td>
                      {item.context}
                      <br />
                      <span className="technical-reference">
                        {item.operationalReference}
                      </span>
                    </td>
                    <td className="confirmed-instruction">
                      {item.instruction ?? "Sin instrucción"}
                    </td>
                    <td>
                      <dl className="preparation-quantities">
                        <div>
                          <dt>Total</dt>
                          <dd>{item.totalQuantity}</dd>
                        </div>
                        <div>
                          <dt>Pendiente</dt>
                          <dd>{item.pendingQuantity}</dd>
                        </div>
                        <div>
                          <dt>En preparación</dt>
                          <dd>{item.inPreparationQuantity}</dd>
                        </div>
                        <div>
                          <dt>Listo</dt>
                          <dd>{item.readyQuantity}</dd>
                        </div>
                      </dl>
                      {item.readyQuantity === item.totalQuantity && (
                        <p className="fully-ready">Todo listo</p>
                      )}
                    </td>
                    <td>
                      <div className="preparation-actions">
                        {renderAction(item, "start")}
                        {renderAction(item, "ready")}
                        {intent?.phase === "submitting" && (
                          <p role="status">Confirmando operación…</p>
                        )}
                        {itemMessage && (
                          <div
                            id={messageId}
                            className={`notice ${
                              itemMessage.kind === "uncertain"
                                ? "notice--uncertain"
                                : "notice--functional-error"
                            }`}
                            role="alert"
                          >
                            <p>{itemMessage.text}</p>
                            {intent?.phase === "uncertain" && (
                              <button
                                type="button"
                                onClick={() => retryIntent(item.workId)}
                              >
                                Reintentar
                              </button>
                            )}
                          </div>
                        )}
                      </div>
                    </td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        </div>
      )}
    </section>
  );
}
