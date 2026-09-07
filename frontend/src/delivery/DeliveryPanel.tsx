import { maximumContentCorrection } from "./contentCorrection.ts";
import { getOrder } from "../orderOperations/orderOperationsClient.ts";
import { listPreparationDestinations } from "../identity/sessionClient.ts";
import {
  listPreparationWork,
  type PreparationWork,
} from "../preparation/preparationClient.ts";
import { useCallback, useEffect, useRef, useState } from "react";
import {
  discardAntiforgeryToken,
  getAntiforgeryToken,
  SessionProblemError,
} from "../identity/sessionClient.ts";
import {
  correctDelivery,
  correctContentQuantity,
  deliverQuantity,
  DeliveryProblemError,
  getOrderDelivery,
  type OrderDelivery,
  type OrderDeliveryContent,
} from "./deliveryClient.ts";

interface DeliveryPanelProps {
  refreshSequence?: number;
  operationalReference: string | null;
  onUnauthorized: () => void;
  ordinaryMutationsBlocked?: boolean;
  onBusyChange?: (reference: string, busy: boolean) => void;
  onOrderChanged?: (reference: string) => void;
}

type DeliveryIntentPhase = "submitting" | "uncertain";

interface DeliveryIntent {
  operationalReference: string;
  phase: DeliveryIntentPhase;
  kind: "delivery" | "correction" | "contentCorrection";
  orderId: string;
  antiforgeryToken?: string;
  incorporationId: string;
  contentOrdinal: number;
  quantity: number;
  idempotencyKey: string;
}

interface ContentMessage {
  kind: "error" | "uncertain";
  text: string;
  detail?: string;
}

interface RefreshOptions {
  clearDelivery?: boolean;
  preserveMessage?: boolean;
}

const forbiddenMessage =
  "Esta Identity no tiene autorización para realizar entregas.";

function contentKey(
  target: Pick<OrderDeliveryContent, "incorporationId" | "contentOrdinal">,
): string {
  return `${target.incorporationId}:${target.contentOrdinal}`;
}

function contentDescription(item: OrderDeliveryContent): string {
  return `${item.productOperationalName}, ${item.instruction ?? "sin instrucción"}, incorporación ${item.incorporationOrdinal}`;
}

export function DeliveryPanel({
  operationalReference,
  refreshSequence,
  onUnauthorized,
  ordinaryMutationsBlocked = false,
  onOrderChanged,
  onBusyChange,
}: DeliveryPanelProps) {
  const [works, setWorks] = useState<PreparationWork[]>([]);
  const [correctionStateAvailable, setCorrectionStateAvailable] =
    useState(false);
  const [contentCorrectionInputs, setContentCorrectionInputs] = useState<
    Record<string, string>
  >({});
  const [contentCorrectionOpen, setContentCorrectionOpen] = useState<
    Record<string, boolean>
  >({});
  const [delivery, setDelivery] = useState<OrderDelivery | null>(null);
  const [quantityInputs, setQuantityInputs] = useState<Record<string, string>>(
    {},
  );
  const [correctionInputs, setCorrectionInputs] = useState<
    Record<string, string>
  >({});
  const [correctionOpen, setCorrectionOpen] = useState<Record<string, boolean>>(
    {},
  );
  const [intents, setIntents] = useState<Record<string, DeliveryIntent>>({});
  const [synchronizing, setSynchronizing] = useState<Record<string, true>>({});
  const [contentMessages, setContentMessages] = useState<
    Record<string, ContentMessage>
  >({});
  const [frozenOrders, setFrozenOrders] = useState<Record<string, true>>({});
  const mutationsBlocked =
    ordinaryMutationsBlocked ||
    (operationalReference !== null &&
      frozenOrders[operationalReference] === true);
  const [isLoading, setIsLoading] = useState(false);
  const [message, setMessage] = useState<string | null>(null);
  const requestSequence = useRef(0);
  const intentsRef = useRef<Record<string, DeliveryIntent>>({});
  const reportedBusyReferences = useRef(new Set<string>());

  useEffect(() => {
    const busy = new Set(
      Object.values(intents).map((intent) => intent.operationalReference),
    );
    if (operationalReference && Object.keys(synchronizing).length > 0)
      busy.add(operationalReference);
    for (const reference of new Set([
      ...reportedBusyReferences.current,
      ...busy,
    ]))
      onBusyChange?.(reference, busy.has(reference));
    reportedBusyReferences.current = busy;
  }, [intents, synchronizing, operationalReference, onBusyChange]);

  const cancelRequests = useCallback(() => {
    requestSequence.current++;
  }, []);

  const replaceIntents = useCallback((next: Record<string, DeliveryIntent>) => {
    intentsRef.current = next;
    setIntents(next);
  }, []);

  const setIntent = useCallback(
    (intent: DeliveryIntent) => {
      replaceIntents({
        ...intentsRef.current,
        [contentKey(intent)]: intent,
      });
    },
    [replaceIntents],
  );

  const clearIntent = useCallback(
    (key: string) => {
      const next = { ...intentsRef.current };
      delete next[key];
      replaceIntents(next);
    },
    [replaceIntents],
  );

  const setContentMessage = useCallback(
    (key: string, nextMessage: ContentMessage | null) => {
      setContentMessages((current) => {
        const next = { ...current };
        if (nextMessage === null) delete next[key];
        else next[key] = nextMessage;
        return next;
      });
    },
    [],
  );

  const setSynchronizingKey = useCallback((key: string, value: boolean) => {
    setSynchronizing((current) => {
      const next = { ...current };
      if (value) next[key] = true;
      else delete next[key];
      return next;
    });
  }, []);

  const setAuthoritativeDelivery = useCallback((loaded: OrderDelivery) => {
    setDelivery(loaded);
    setSynchronizing({});
    setQuantityInputs(() => {
      const next: Record<string, string> = {};
      for (const item of loaded.contents) {
        const key = contentKey(item);
        const intent = intentsRef.current[key];
        next[key] =
          intent !== undefined
            ? String(intent.quantity)
            : item.deliverableQuantity > 0
              ? String(item.deliverableQuantity)
              : "";
      }
      return next;
    });
  }, []);

  const handleUnauthorized = useCallback(() => {
    requestSequence.current++;
    discardAntiforgeryToken();
    replaceIntents({});
    setSynchronizing({});
    setContentMessages({});
    setDelivery(null);
    onUnauthorized();
  }, [onUnauthorized, replaceIntents]);

  const refreshDelivery = useCallback(
    async (
      reference: string,
      options: RefreshOptions = {},
    ): Promise<boolean> => {
      const sequence = ++requestSequence.current;
      setIsLoading(true);
      setCorrectionStateAvailable(false);
      if (!options.preserveMessage) setMessage(null);
      if (options.clearDelivery) setDelivery(null);

      try {
        const loaded = await getOrderDelivery(reference);
        if (sequence !== requestSequence.current) return false;
        let available = false;
        let preparation: PreparationWork[] = [];
        if (
          loaded.contents.every((item) => item.confirmedQuantity !== undefined)
        ) {
          try {
            const order = await getOrder(reference);
            if (order.isFrozen || order.isClosed)
              setFrozenOrders((current) => ({ ...current, [reference]: true }));
            available =
              !order.liquidationBlockers.includes("state_inconsistent");
            if (
              loaded.contents.some(
                (item) => item.requiresPreparationAtConfirmation,
              )
            ) {
              const destinations = await listPreparationDestinations();
              preparation = (
                await Promise.all(
                  destinations.map((destination) =>
                    listPreparationWork(
                      destination.preparationResponsibilityId,
                    ),
                  ),
                )
              ).flat();
            }
          } catch (error) {
            const status =
              (error as { status?: number }).status ??
              (error as { problem?: { status?: number } }).problem?.status;
            if (status === 401) {
              handleUnauthorized();
              return false;
            }
          }
        }
        if (sequence !== requestSequence.current) return false;
        setWorks(preparation);
        setCorrectionStateAvailable(available);
        setAuthoritativeDelivery(loaded);
        return true;
      } catch (error) {
        if (sequence !== requestSequence.current) return false;
        if (error instanceof DeliveryProblemError && error.status === 401) {
          handleUnauthorized();
          return false;
        }

        setDelivery(null);
        if (error instanceof DeliveryProblemError && error.status === 403) {
          setMessage(forbiddenMessage);
        } else if (
          error instanceof DeliveryProblemError &&
          error.status === 404
        ) {
          setMessage("Pedido no encontrado.");
        } else if (
          error instanceof DeliveryProblemError &&
          error.status >= 500
        ) {
          setMessage("No se pudo consultar Delivery por un problema técnico.");
        } else {
          setMessage("No se pudo consultar la entrega del Pedido.");
        }
        return false;
      } finally {
        if (sequence === requestSequence.current) setIsLoading(false);
      }
    },
    [handleUnauthorized, setAuthoritativeDelivery],
  );

  useEffect(() => {
    if (operationalReference === null) {
      cancelRequests();
      return;
    }

    const scheduledRefresh = window.setTimeout(() => {
      void refreshDelivery(operationalReference, { clearDelivery: true });
    }, 0);
    return () => {
      window.clearTimeout(scheduledRefresh);
      cancelRequests();
    };
  }, [cancelRequests, operationalReference, refreshDelivery, refreshSequence]);

  const executeIntent = useCallback(
    async (intent: DeliveryIntent, reference: string) => {
      const key = contentKey(intent);
      let antiforgeryToken: string;

      try {
        antiforgeryToken =
          intent.antiforgeryToken ?? (await getAntiforgeryToken());
        intent = { ...intent, antiforgeryToken };
        setIntent(intent);
      } catch (error) {
        if (error instanceof SessionProblemError && error.status === 401) {
          handleUnauthorized();
          return;
        }

        clearIntent(key);
        setContentMessage(key, {
          kind: "error",
          text: "No se pudo obtener la protección de la solicitud.",
        });
        return;
      }

      try {
        const command = {
          incorporationId: intent.incorporationId,
          contentOrdinal: intent.contentOrdinal,
          quantity: intent.quantity,
          idempotencyKey: intent.idempotencyKey,
          antiforgeryToken,
        };
        const result =
          intent.kind === "contentCorrection"
            ? await correctContentQuantity({
                ...command,
                orderId: intent.orderId,
              })
            : intent.kind === "correction"
              ? await correctDelivery({ ...command, orderId: intent.orderId })
              : await deliverQuantity(command);

        if (
          result.incorporationId !== intent.incorporationId ||
          result.contentOrdinal !== intent.contentOrdinal
        ) {
          throw new Error("Delivery returned another Content target.");
        }

        clearIntent(key);
        setContentCorrectionOpen((current) => ({ ...current, [key]: false }));
        if (intent.kind === "correction") {
          setCorrectionOpen((current) => ({ ...current, [key]: false }));
          setCorrectionInputs((current) => ({ ...current, [key]: "" }));
        }
        setContentMessage(key, null);
        setSynchronizingKey(key, true);
        const refreshed = await refreshDelivery(reference);
        onOrderChanged?.(reference);
        if (refreshed) setSynchronizingKey(key, false);
      } catch (error) {
        if (error instanceof DeliveryProblemError && error.status === 401) {
          handleUnauthorized();
          return;
        }

        if (error instanceof DeliveryProblemError) {
          if (error.status === 408 || error.status >= 500) {
            setIntent({ ...intent, phase: "uncertain" });
            setContentMessage(key, {
              kind: "uncertain",
              text:
                intent.kind === "contentCorrection"
                  ? "No se pudo confirmar el resultado de la corrección de cantidad confirmada."
                  : intent.kind === "correction"
                    ? "No se pudo confirmar el resultado de la corrección de entrega."
                    : "No se pudo confirmar el resultado de la entrega.",
              detail:
                error.status >= 500
                  ? "No se pudo procesar la entrega por un problema técnico."
                  : undefined,
            });
            return;
          }

          clearIntent(key);

          if (error.status === 409) {
            if (error.problem.code === "order_operations.order.frozen")
              setFrozenOrders((current) => ({ ...current, [reference]: true }));
            const isIdempotencyConflict =
              error.problem.code?.endsWith(".idempotency_key_conflict") ===
              true;
            setContentMessage(key, {
              kind: "error",
              text:
                error.problem.code === "order_operations.order.frozen"
                  ? "El Pedido está congelado y ya no puede modificarse."
                  : isIdempotencyConflict
                    ? "La operación no coincide con el intento original."
                    : "El estado de la entrega cambió. Se actualizó la información.",
            });
            setSynchronizingKey(key, true);
            const refreshed = await refreshDelivery(reference, {
              preserveMessage: true,
            });
            onOrderChanged?.(reference);
            if (refreshed) setSynchronizingKey(key, false);
            return;
          }

          if (error.status === 403) {
            setContentMessage(key, {
              kind: "error",
              text:
                intent.kind === "contentCorrection"
                  ? "Esta Identity no tiene autorización para corregir la cantidad confirmada."
                  : forbiddenMessage,
            });
            return;
          }

          if (error.status === 404) {
            setContentMessage(key, {
              kind: "error",
              text: "El Content de entrega ya no está disponible. Se actualizó la información.",
            });
            setSynchronizingKey(key, true);
            const refreshed = await refreshDelivery(reference, {
              preserveMessage: true,
            });
            onOrderChanged?.(reference);
            if (refreshed) setSynchronizingKey(key, false);
            return;
          }

          const antiforgeryInvalid =
            error.problem.code?.endsWith(".antiforgery_invalid") === true;
          if (antiforgeryInvalid) discardAntiforgeryToken();
          setContentMessage(key, {
            kind: "error",
            text:
              intent.kind === "contentCorrection"
                ? "No se pudo corregir la cantidad confirmada. Revisá la cantidad e intentá nuevamente."
                : error.status === 400
                  ? "No se pudo realizar la entrega. Revisá la cantidad e intentá nuevamente."
                  : "No se pudo realizar la entrega.",
          });
          return;
        }

        setIntent({ ...intent, phase: "uncertain" });
        setContentMessage(key, {
          kind: "uncertain",
          text:
            intent.kind === "contentCorrection"
              ? "No se pudo confirmar el resultado de la corrección de cantidad confirmada."
              : intent.kind === "correction"
                ? "No se pudo confirmar el resultado de la corrección de entrega."
                : "No se pudo confirmar el resultado de la entrega.",
        });
      }
    },
    [
      clearIntent,
      handleUnauthorized,
      refreshDelivery,
      setContentMessage,
      setIntent,
      setSynchronizingKey,
      onOrderChanged,
    ],
  );

  const submitNewIntent = useCallback(
    (
      item: OrderDeliveryContent,
      kind: "delivery" | "correction" | "contentCorrection" = "delivery",
    ) => {
      if (operationalReference === null || mutationsBlocked) return;
      const key = contentKey(item);
      if (
        intentsRef.current[key] !== undefined ||
        synchronizing[key] !== undefined
      ) {
        return;
      }

      const maximum =
        kind === "contentCorrection"
          ? correctionStateAvailable
            ? (maximumContentCorrection(item, works) ?? 0)
            : 0
          : kind === "correction"
            ? item.deliveredQuantity
            : item.deliverableQuantity;
      const rawQuantity =
        (kind === "contentCorrection"
          ? contentCorrectionInputs[key]
          : kind === "correction"
            ? correctionInputs[key]
            : quantityInputs[key]) ?? "";
      const quantity = Number(rawQuantity);
      if (
        !/^\d+$/.test(rawQuantity) ||
        !Number.isInteger(quantity) ||
        quantity <= 0 ||
        quantity > maximum
      ) {
        setContentMessage(key, {
          kind: "error",
          text: `Ingresá una cantidad entera entre 1 y ${maximum}.`,
        });
        return;
      }

      const intent: DeliveryIntent = {
        operationalReference,
        phase: "submitting",
        kind,
        orderId: delivery!.orderId,
        incorporationId: item.incorporationId,
        contentOrdinal: item.contentOrdinal,
        quantity,
        idempotencyKey: crypto.randomUUID(),
      };
      setMessage(null);
      setContentMessage(key, null);
      setIntent(intent);
      void executeIntent(intent, operationalReference);
    },
    [
      executeIntent,
      operationalReference,
      quantityInputs,
      correctionInputs,
      contentCorrectionInputs,
      correctionStateAvailable,
      works,
      delivery,
      setContentMessage,
      setIntent,
      synchronizing,
      mutationsBlocked,
    ],
  );

  const retryIntent = useCallback(
    (key: string) => {
      if (operationalReference === null) return;
      const currentIntent = intentsRef.current[key];
      if (currentIntent?.phase !== "uncertain") return;

      const submittingIntent: DeliveryIntent = {
        ...currentIntent,
        phase: "submitting",
      };
      setContentMessage(key, null);
      setIntent(submittingIntent);
      void executeIntent(
        submittingIntent,
        submittingIntent.operationalReference,
      );
    },
    [executeIntent, operationalReference, setContentMessage, setIntent],
  );

  return (
    <section className="panel" aria-labelledby="delivery-heading">
      <div className="section-heading">
        <h2 id="delivery-heading">Delivery</h2>
        <button
          type="button"
          className="secondary-button"
          disabled={operationalReference === null || isLoading}
          onClick={() => {
            if (operationalReference !== null) {
              void refreshDelivery(operationalReference);
            }
          }}
        >
          Actualizar
        </button>
      </div>

      {operationalReference === null && (
        <p>Buscá un Pedido y abrí su entrega desde la consulta.</p>
      )}
      {operationalReference !== null && isLoading && delivery === null && (
        <p>Cargando entrega…</p>
      )}
      {operationalReference !== null && message && (
        <p className="notice notice--functional-error" role="alert">
          {message}
        </p>
      )}
      {operationalReference !== null && delivery && (
        <div
          className="delivery-order"
          role="region"
          aria-label={`Entrega del Pedido ${delivery.operationalReference}`}
        >
          <dl className="confirmation-summary">
            <div>
              <dt>Referencia operacional</dt>
              <dd>{delivery.operationalReference}</dd>
            </div>
            <div>
              <dt>Contexto actual</dt>
              <dd>{delivery.currentContext}</dd>
            </div>
          </dl>

          {delivery.contents.length === 0 ? (
            <p>El Pedido no contiene Contents para entregar.</p>
          ) : (
            <div className="delivery-contents">
              {delivery.contents.map((item) => {
                const key = contentKey(item);
                const intent = intents[key];
                const itemMessage = contentMessages[key];
                const isBlocked =
                  mutationsBlocked ||
                  intent !== undefined ||
                  synchronizing[key] !== undefined;
                const isFullyDelivered =
                  item.totalQuantity > 0 && item.remainingQuantity === 0;
                const correctionMaximum = correctionStateAvailable
                  ? maximumContentCorrection(item, works)
                  : null;
                const requested = contentCorrectionInputs[key] ?? "";
                const validCorrection =
                  /^\d+$/.test(requested) &&
                  Number.isSafeInteger(Number(requested)) &&
                  Number(requested) > 0 &&
                  correctionMaximum !== null &&
                  Number(requested) <= correctionMaximum;
                const fieldId = `delivery-quantity-${item.incorporationId}-${item.contentOrdinal}`;
                const messageId = `delivery-message-${item.incorporationId}-${item.contentOrdinal}`;
                const description = contentDescription(item);

                return (
                  <article
                    className="delivery-content"
                    key={key}
                    aria-label={description}
                    aria-busy={
                      intent?.phase === "submitting" ||
                      synchronizing[key] !== undefined
                    }
                  >
                    <div className="delivery-content-heading">
                      <div>
                        <h3>{item.productOperationalName}</h3>
                        <p>Incorporación {item.incorporationOrdinal}</p>
                      </div>
                      {isFullyDelivered && (
                        <p className="fully-delivered" role="status">
                          Entregado
                        </p>
                      )}
                    </div>
                    <p className="confirmed-instruction">
                      {item.instruction ?? "Sin instrucción"}
                    </p>
                    <dl className="delivery-quantities">
                      <div>
                        <dt>Total</dt>
                        <dd>{item.totalQuantity}</dd>
                      </div>
                      {item.requiresPreparationAtConfirmation ? (
                        <div>
                          <dt>Ready</dt>
                          <dd>{item.readyQuantity}</dd>
                        </div>
                      ) : (
                        <div className="delivery-preparation-note">
                          <dt>Preparación</dt>
                          <dd>Preparación no requerida</dd>
                        </div>
                      )}
                      <div>
                        <dt>Delivered</dt>
                        <dd>{item.deliveredQuantity}</dd>
                      </div>
                      <div>
                        <dt>Deliverable</dt>
                        <dd>{item.deliverableQuantity}</dd>
                      </div>
                      <div>
                        <dt>Remaining</dt>
                        <dd>{item.remainingQuantity}</dd>
                      </div>
                    </dl>

                    {!isFullyDelivered && item.deliverableQuantity > 0 && (
                      <form
                        className="delivery-action"
                        noValidate
                        onSubmit={(event) => {
                          event.preventDefault();
                          submitNewIntent(item);
                        }}
                      >
                        <label htmlFor={fieldId}>
                          Cantidad a entregar — {item.productOperationalName} —{" "}
                          {item.instruction ?? "sin instrucción"} —
                          incorporación {item.incorporationOrdinal}
                        </label>
                        <div className="delivery-action-controls">
                          <input
                            id={fieldId}
                            type="number"
                            min="1"
                            max={item.deliverableQuantity}
                            step="1"
                            value={quantityInputs[key] ?? ""}
                            disabled={isBlocked}
                            aria-describedby={
                              itemMessage ? messageId : undefined
                            }
                            onChange={(event) =>
                              setQuantityInputs((current) => ({
                                ...current,
                                [key]: event.target.value,
                              }))
                            }
                          />
                          <button
                            type="submit"
                            disabled={isBlocked}
                            aria-label={`Entregar ${description}`}
                          >
                            Entregar
                          </button>
                        </div>
                      </form>
                    )}

                    <div className="content-correction">
                      <dl className="delivery-quantities">
                        <div>
                          <dt>Q · Cantidad confirmada original</dt>
                          <dd>{item.confirmedQuantity ?? "No disponible"}</dd>
                        </div>
                        <div>
                          <dt>R · Retirada por corrección</dt>
                          <dd>
                            {item.removedByCorrectionQuantity ??
                              "No disponible"}
                          </dd>
                        </div>
                        <div>
                          <dt>F · Obligación vigente</dt>
                          <dd>
                            {item.currentFulfillmentQuantity ?? "No disponible"}
                          </dd>
                        </div>
                      </dl>
                      {item.currentFulfillmentQuantity === 0 && (
                        <p>Sin obligación vigente</p>
                      )}
                      {correctionMaximum === null ? (
                        <p>
                          Corrección de cantidad confirmada: Estado no
                          disponible o inconsistente.
                        </p>
                      ) : (
                        <p>
                          Máximo corregible actualmente: {correctionMaximum}
                        </p>
                      )}
                      {!mutationsBlocked &&
                        correctionMaximum !== null &&
                        correctionMaximum > 0 && (
                          <>
                            <button
                              type="button"
                              disabled={isBlocked}
                              onClick={() =>
                                setContentCorrectionOpen((current) => ({
                                  ...current,
                                  [key]: true,
                                }))
                              }
                            >
                              Corregir cantidad confirmada
                            </button>
                            {contentCorrectionOpen[key] && (
                              <form
                                noValidate
                                onSubmit={(event) => {
                                  event.preventDefault();
                                  submitNewIntent(item, "contentCorrection");
                                }}
                              >
                                <p>
                                  La cantidad confirmada fue incorrecta. Indicá
                                  cuánto retirar de la obligación vigente.
                                </p>
                                <label
                                  htmlFor={fieldId + "-content-correction"}
                                >
                                  Cantidad a retirar (x) — {description}
                                </label>
                                <input
                                  id={fieldId + "-content-correction"}
                                  type="number"
                                  min="1"
                                  max={correctionMaximum}
                                  step="1"
                                  value={requested}
                                  disabled={isBlocked}
                                  onChange={(event) =>
                                    setContentCorrectionInputs((current) => ({
                                      ...current,
                                      [key]: event.target.value,
                                    }))
                                  }
                                />
                                <p>
                                  Cantidad solicitada (x): {requested || "—"}
                                </p>
                                <p>
                                  F resultante (prevista):{" "}
                                  {validCorrection
                                    ? item.currentFulfillmentQuantity! -
                                      Number(requested)
                                    : "—"}
                                </p>
                                <button type="submit" disabled={isBlocked}>
                                  Confirmar corrección de cantidad confirmada
                                </button>
                              </form>
                            )}
                          </>
                        )}
                    </div>

                    {!mutationsBlocked && item.deliveredQuantity > 0 && (
                      <div className="delivery-action">
                        <button
                          type="button"
                          disabled={isBlocked}
                          aria-label={`Corregir entrega ${description}`}
                          onClick={() =>
                            setCorrectionOpen((current) => ({
                              ...current,
                              [key]: true,
                            }))
                          }
                        >
                          Corregir entrega
                        </button>
                        {correctionOpen[key] && (
                          <form
                            noValidate
                            onSubmit={(event) => {
                              event.preventDefault();
                              submitNewIntent(item, "correction");
                            }}
                          >
                            <p>
                              Indicá la cantidad registrada por error que debe
                              retirarse de la entrega.
                            </p>
                            <p>
                              Actualmente entregado: {item.deliveredQuantity}
                            </p>
                            <label htmlFor={`${fieldId}-correction`}>
                              Cantidad a corregir — {description}
                            </label>
                            <input
                              id={`${fieldId}-correction`}
                              type="number"
                              min="1"
                              max={item.deliveredQuantity}
                              step="1"
                              disabled={isBlocked}
                              value={correctionInputs[key] ?? ""}
                              onChange={(event) =>
                                setCorrectionInputs((current) => ({
                                  ...current,
                                  [key]: event.target.value,
                                }))
                              }
                            />
                            <p>
                              Cantidad a corregir:{" "}
                              {correctionInputs[key] || "—"}
                            </p>
                            <p>
                              Entrega resultante (prevista):{" "}
                              {/^[0-9]+$/.test(correctionInputs[key] ?? "") &&
                              Number(correctionInputs[key]) > 0 &&
                              Number(correctionInputs[key]) <=
                                item.deliveredQuantity
                                ? item.deliveredQuantity -
                                  Number(correctionInputs[key])
                                : "—"}
                            </p>
                            <button type="submit" disabled={isBlocked}>
                              Confirmar corrección de entrega
                            </button>
                          </form>
                        )}
                      </div>
                    )}

                    {intent?.phase === "submitting" && (
                      <p role="status">
                        {intent.kind === "contentCorrection"
                          ? "Confirmando corrección de cantidad confirmada…"
                          : intent.kind === "correction"
                            ? "Confirmando corrección de entrega…"
                            : "Confirmando entrega…"}
                      </p>
                    )}
                    {synchronizing[key] !== undefined && (
                      <p role="status">Actualizando cantidades…</p>
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
                        {itemMessage.detail && <p>{itemMessage.detail}</p>}
                        {intent?.phase === "uncertain" && (
                          <button
                            type="button"
                            onClick={() => retryIntent(key)}
                          >
                            Reintentar
                          </button>
                        )}
                      </div>
                    )}
                  </article>
                );
              })}
            </div>
          )}
        </div>
      )}
    </section>
  );
}
