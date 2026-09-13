import { useCallback, useEffect, useRef, useState } from "react";
import { FreshnessReadCoordinator } from "../notifications/FreshnessReadCoordinator.ts";
import { PreparationFreshnessSubscription } from "./PreparationFreshnessSubscription.tsx";
import {
  discardAntiforgeryToken,
  getAntiforgeryToken,
  listPreparationDestinations,
  SessionProblemError,
  type PreparationDestination,
} from "../identity/sessionClient.ts";
import {
  correctPreparationReady,
  correctPreparationStart,
  listPreparationWork,
  markPreparationQuantityReady,
  PreparationProblemError,
  startPreparationQuantity,
  type PreparationCommandResult,
  type PreparationWork,
} from "./preparationClient.ts";

interface PreparationPanelProps {
  refreshSequence?: number;
  onBusyOrdersChange?: (references: string[]) => void;
  onWorkChanged?: () => void;
  isOrderBlocked?: (reference: string) => boolean;
  onUnauthorized: () => void;
}

type PreparationCommandKind =
  "start" | "ready" | "correct-start" | "correct-ready";
type PreparationIntentPhase = "submitting" | "uncertain";

interface PreparationIntent {
  operationalReference: string;
  phase: PreparationIntentPhase;
  kind: PreparationCommandKind;
  workId: string;
  quantity: number;
  idempotencyKey: string;
  antiforgeryToken?: string;
}

interface QuantityInputs {
  start: string;
  ready: string;
  "correct-start": string;
  "correct-ready": string;
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
  switch (kind) {
    case "start":
      return item.pendingQuantity;
    case "ready":
    case "correct-start":
      return item.inPreparationQuantity;
    case "correct-ready":
      return Math.max(
        0,
        item.readyQuantity - (item.deliveredQuantity ?? item.readyQuantity),
      );
  }
}

function actionName(kind: PreparationCommandKind): string {
  switch (kind) {
    case "start":
      return "iniciar";
    case "ready":
      return "marcar listo";
    case "correct-start":
      return "corregir el inicio";
    case "correct-ready":
      return "corregir listo";
  }
}

function inputLabel(
  item: PreparationWork,
  kind: PreparationCommandKind,
): string {
  const action =
    kind === "start"
      ? "iniciar"
      : kind === "ready"
        ? "marcar lista"
        : kind === "correct-start"
          ? "corregir inicio"
          : "corregir listo";
  return `Cantidad a ${action} de ${item.productOperationalName}, incorporación ${item.incorporationOrdinal}, ${item.context}, ${item.instruction ?? "sin instrucción"}`;
}

function buttonLabel(
  item: PreparationWork,
  kind: PreparationCommandKind,
): string {
  const action =
    kind === "start"
      ? "Iniciar"
      : kind === "ready"
        ? "Marcar listo"
        : kind === "correct-start"
          ? "Corregir inicio"
          : "Corregir listo";
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

export function PreparationPanel({
  onUnauthorized,
  isOrderBlocked,
  refreshSequence,
  onBusyOrdersChange,
  onWorkChanged,
}: PreparationPanelProps) {
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
  const [readCoordinator] = useState(() => new FreshnessReadCoordinator());
  const mounted = useRef(true);
  const selectedIdRef = useRef("");
  useEffect(() => {
    onBusyOrdersChange?.(
      Object.values(intents).map((intent) => intent.operationalReference),
    );
  }, [intents, onBusyOrdersChange]);
  const intentsRef = useRef<Record<string, PreparationIntent>>({});

  const cancelWorkRequests = useCallback(() => {
    readCoordinator.cancel();
  }, [readCoordinator]);

  useEffect(() => {
    mounted.current = true;
    return () => {
      mounted.current = false;
      cancelWorkRequests();
    };
  }, [cancelWorkRequests]);

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
          "correct-start":
            intent?.kind === "correct-start"
              ? String(intent.quantity)
              : item.inPreparationQuantity > 0
                ? String(item.inPreparationQuantity)
                : "",
          "correct-ready":
            intent?.kind === "correct-ready"
              ? String(intent.quantity)
              : sourceQuantity(item, "correct-ready") > 0
                ? String(sourceQuantity(item, "correct-ready"))
                : "",
        };
      }
      return next;
    });
  }, []);

  const handleUnauthorized = useCallback(() => {
    cancelWorkRequests();
    mounted.current = false;
    selectedIdRef.current = "";
    setSelectedId("");
    discardAntiforgeryToken();
    replaceIntents({});
    setWorkMessages({});
    onUnauthorized();
  }, [cancelWorkRequests, onUnauthorized, replaceIntents]);

  const refreshPreparationWorkForSelectedDestination = useCallback(
    (options: RefreshOptions = {}) => {
      if (!mounted.current) return;
      setIsLoadingDestinations(true);
      setIsLoadingWork(true);
      if (!options.preserveMessage) setMessage(null);
      if (options.clearWork) setAuthoritativeWork([]);

      readCoordinator.invalidate(async (isCurrent) => {
        try {
          const loadedDestinations = await listPreparationDestinations();
          if (!isCurrent()) return;

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

          if (nextSelectedId !== selectedIdRef.current) setAuthoritativeWork([]);
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
          if (!isCurrent()) return;
          setAuthoritativeWork(loadedWork);
        } catch (error) {
          if (!isCurrent()) return;
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
          if (isCurrent()) {
            setIsLoadingDestinations(false);
            setIsLoadingWork(false);
          }
        }
      });
    },
    [handleUnauthorized, setAuthoritativeWork, readCoordinator],
  );

  const invalidateDestination = useCallback(() => {
    refreshPreparationWorkForSelectedDestination({ preserveMessage: true });
  }, [refreshPreparationWorkForSelectedDestination]);

  useEffect(() => {
    const scheduledRefresh = window.setTimeout(() => {
      void refreshPreparationWorkForSelectedDestination({ clearWork: true });
    }, 0);
    return () => {
      window.clearTimeout(scheduledRefresh);
      cancelWorkRequests();
    };
  }, [
    cancelWorkRequests,
    refreshPreparationWorkForSelectedDestination,
    refreshSequence,
  ]);

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
        "correct-start":
          result.inPreparationQuantity > 0
            ? String(result.inPreparationQuantity)
            : "",
        "correct-ready": current[result.workId]?.["correct-ready"] ?? "",
      },
    }));
  }, []);

  const executeIntent = useCallback(
    async (intent: PreparationIntent) => {
      let exactIntent = intent;
      try {
        const antiforgeryToken =
          intent.antiforgeryToken ?? (await getAntiforgeryToken());
        if (!mounted.current) return;
        exactIntent = { ...intent, antiforgeryToken };
        setIntent(exactIntent);
        const command = {
          workId: exactIntent.workId,
          quantity: exactIntent.quantity,
          idempotencyKey: exactIntent.idempotencyKey,
          antiforgeryToken,
        };
        const result =
          exactIntent.kind === "start"
            ? await startPreparationQuantity(command)
            : exactIntent.kind === "ready"
              ? await markPreparationQuantityReady(command)
              : exactIntent.kind === "correct-start"
                ? await correctPreparationStart(command)
                : await correctPreparationReady(command);

        if (!mounted.current) return;

        if (result.workId !== exactIntent.workId) {
          throw new Error("Preparation returned another Work.");
        }

        clearIntent(exactIntent.workId);
        setWorkMessage(exactIntent.workId, null);
        applyCommandResult(result);
        void refreshPreparationWorkForSelectedDestination();
        onWorkChanged?.();
      } catch (error) {
        if (!mounted.current) return;
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
            setIntent({ ...exactIntent, phase: "uncertain" });
            setWorkMessage(exactIntent.workId, {
              kind: "uncertain",
              text: "No se pudo confirmar el resultado.",
            });
            return;
          }

          clearIntent(exactIntent.workId);

          if (error.status === 409) {
            const isIdempotencyConflict =
              error.problem.code?.endsWith(".idempotency_key_conflict") ===
              true;
            setWorkMessage(exactIntent.workId, {
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
            setWorkMessage(exactIntent.workId, {
              kind: "error",
              text: forbiddenMessage,
            });
            return;
          }

          if (error.status === 404) {
            setWorkMessage(exactIntent.workId, null);
            setMessage("El trabajo ya no está disponible.");
            void refreshPreparationWorkForSelectedDestination({
              preserveMessage: true,
            });
            return;
          }

          const antiforgeryInvalid =
            error.problem.code?.endsWith(".antiforgery_invalid") === true;
          if (antiforgeryInvalid) discardAntiforgeryToken();
          setWorkMessage(exactIntent.workId, {
            kind: "error",
            text: antiforgeryInvalid
              ? "No se pudo validar la solicitud. Volvé a intentarlo."
              : "No se pudo realizar la operación de preparación.",
          });
          return;
        }

        if (error instanceof SessionProblemError) {
          clearIntent(exactIntent.workId);
          setWorkMessage(exactIntent.workId, {
            kind: "error",
            text:
              error.status === 403
                ? forbiddenMessage
                : "No se pudo obtener la protección de la solicitud.",
          });
          return;
        }

        setIntent({ ...exactIntent, phase: "uncertain" });
        setWorkMessage(exactIntent.workId, {
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
      onWorkChanged,
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
        operationalReference: item.operationalReference,
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
    const isWorkBlocked =
      intent !== undefined ||
      isOrderBlocked?.(item.operationalReference) === true;
    const fieldId = `preparation-${kind}-quantity-${item.workId}`;
    const messageId = `preparation-message-${item.workId}`;
    const isCorrection = kind === "correct-start" || kind === "correct-ready";
    if (isCorrection && isOrderBlocked?.(item.operationalReference) === true)
      return null;
    const rawQuantity = quantityInputs[item.workId]?.[kind] ?? "";
    const previewQuantity = Number(rawQuantity);
    const hasPreview =
      rawQuantity.trim() !== "" &&
      Number.isInteger(previewQuantity) &&
      previewQuantity > 0 &&
      previewQuantity <= availableQuantity;
    const actionLabel =
      kind === "start"
        ? "Iniciar"
        : kind === "ready"
          ? "Marcar listo"
          : kind === "correct-start"
            ? "Corregir inicio"
            : "Corregir listo";
    return (
      <form
        className="preparation-action"
        aria-label={`${actionLabel} ${item.productOperationalName}, incorporación ${item.incorporationOrdinal}, ${item.instruction ?? "sin instrucción"}`}
        noValidate
        onSubmit={(event) => {
          event.preventDefault();
          if (!isWorkBlocked) submitNewIntent(item, kind);
        }}
      >
        <label htmlFor={fieldId}>
          {kind === "start"
            ? "Cantidad a iniciar"
            : kind === "ready"
              ? "Cantidad a marcar lista"
              : kind === "correct-start"
                ? "Cantidad cuyo inicio corregir"
                : "Cantidad lista a corregir"}
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
                "correct-start": current[item.workId]?.["correct-start"] ?? "",
                "correct-ready": current[item.workId]?.["correct-ready"] ?? "",
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
          {actionLabel}
        </button>
        {isCorrection && hasPreview && (
          <p className="preparation-correction-preview">
            Vista previa:{" "}
            {kind === "correct-start" ? (
              <>
                En preparación {item.inPreparationQuantity} →{" "}
                {item.inPreparationQuantity - previewQuantity}; Pendiente{" "}
                {item.pendingQuantity} →{" "}
                {item.pendingQuantity + previewQuantity}
              </>
            ) : (
              <>
                Listo {item.readyQuantity} →{" "}
                {item.readyQuantity - previewQuantity}; En preparación{" "}
                {item.inPreparationQuantity} →{" "}
                {item.inPreparationQuantity + previewQuantity}
              </>
            )}
            .
          </p>
        )}
      </form>
    );
  }

  return (
    <section className="panel" aria-labelledby="preparation-heading">
      {selectedId !== "" && (
        <PreparationFreshnessSubscription
          destinationId={selectedId}
          invalidate={invalidateDestination}
        />
      )}
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
                cancelWorkRequests();
                setAuthoritativeWork([]);
                setMessage(null);
                setIsLoadingDestinations(false);
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
                      {item.totalQuantity === 0 && (
                        <p>Sin obligación vigente</p>
                      )}
                      {item.totalQuantity > 0 &&
                        item.readyQuantity === item.totalQuantity && (
                          <p className="fully-ready">Todo listo</p>
                        )}
                    </td>
                    <td>
                      <div className="preparation-actions">
                        {renderAction(item, "start")}
                        {renderAction(item, "ready")}
                        {(sourceQuantity(item, "correct-start") > 0 ||
                          sourceQuantity(item, "correct-ready") > 0) &&
                          isOrderBlocked?.(item.operationalReference) !==
                            true && (
                            <div className="preparation-corrections">
                              <p>
                                Correcciones de progreso registrado por error.
                                No son Cancellation, Content Correction,
                                Delivery Correction, OperationalIntervention ni
                                un deshacer genérico.
                              </p>
                              {renderAction(item, "correct-start")}
                              {renderAction(item, "correct-ready")}
                            </div>
                          )}
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
