import { useCallback, useEffect, useRef, useState } from "react";
import {
  discardAntiforgeryToken,
  getAntiforgeryToken,
  SessionProblemError,
} from "../identity/sessionClient.ts";
import {
  deliverQuantity,
  DeliveryProblemError,
  getOrderDelivery,
  type OrderDelivery,
  type OrderDeliveryContent,
} from "./deliveryClient.ts";

interface DeliveryPanelProps {
  operationalReference: string | null;
  onUnauthorized: () => void;
}

type DeliveryIntentPhase = "submitting" | "uncertain";

interface DeliveryIntent {
  phase: DeliveryIntentPhase;
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
  onUnauthorized,
}: DeliveryPanelProps) {
  const [delivery, setDelivery] = useState<OrderDelivery | null>(null);
  const [quantityInputs, setQuantityInputs] = useState<Record<string, string>>(
    {},
  );
  const [intents, setIntents] = useState<Record<string, DeliveryIntent>>({});
  const [synchronizing, setSynchronizing] = useState<Record<string, true>>({});
  const [contentMessages, setContentMessages] = useState<
    Record<string, ContentMessage>
  >({});
  const [isLoading, setIsLoading] = useState(false);
  const [message, setMessage] = useState<string | null>(null);
  const requestSequence = useRef(0);
  const intentsRef = useRef<Record<string, DeliveryIntent>>({});

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
      if (!options.preserveMessage) setMessage(null);
      if (options.clearDelivery) setDelivery(null);

      try {
        const loaded = await getOrderDelivery(reference);
        if (sequence !== requestSequence.current) return false;
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
  }, [cancelRequests, operationalReference, refreshDelivery]);

  const executeIntent = useCallback(
    async (intent: DeliveryIntent, reference: string) => {
      const key = contentKey(intent);
      let antiforgeryToken: string;

      try {
        antiforgeryToken = await getAntiforgeryToken();
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
        const result = await deliverQuantity({
          incorporationId: intent.incorporationId,
          contentOrdinal: intent.contentOrdinal,
          quantity: intent.quantity,
          idempotencyKey: intent.idempotencyKey,
          antiforgeryToken,
        });

        if (
          result.incorporationId !== intent.incorporationId ||
          result.contentOrdinal !== intent.contentOrdinal
        ) {
          throw new Error("Delivery returned another Content target.");
        }

        clearIntent(key);
        setContentMessage(key, null);
        setSynchronizingKey(key, true);
        const refreshed = await refreshDelivery(reference);
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
              text: "No se pudo confirmar el resultado de la entrega.",
              detail:
                error.status >= 500
                  ? "No se pudo procesar la entrega por un problema técnico."
                  : undefined,
            });
            return;
          }

          clearIntent(key);

          if (error.status === 409) {
            const isIdempotencyConflict =
              error.problem.code?.endsWith(".idempotency_key_conflict") ===
              true;
            setContentMessage(key, {
              kind: "error",
              text: isIdempotencyConflict
                ? "La operación no coincide con el intento original."
                : "El estado de la entrega cambió. Se actualizó la información.",
            });
            setSynchronizingKey(key, true);
            const refreshed = await refreshDelivery(reference, {
              preserveMessage: true,
            });
            if (refreshed) setSynchronizingKey(key, false);
            return;
          }

          if (error.status === 403) {
            setContentMessage(key, {
              kind: "error",
              text: forbiddenMessage,
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
            if (refreshed) setSynchronizingKey(key, false);
            return;
          }

          const antiforgeryInvalid =
            error.problem.code?.endsWith(".antiforgery_invalid") === true;
          if (antiforgeryInvalid) discardAntiforgeryToken();
          setContentMessage(key, {
            kind: "error",
            text:
              error.status === 400
                ? "No se pudo realizar la entrega. Revisá la cantidad e intentá nuevamente."
                : "No se pudo realizar la entrega.",
          });
          return;
        }

        setIntent({ ...intent, phase: "uncertain" });
        setContentMessage(key, {
          kind: "uncertain",
          text: "No se pudo confirmar el resultado de la entrega.",
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
    ],
  );

  const submitNewIntent = useCallback(
    (item: OrderDeliveryContent) => {
      if (operationalReference === null) return;
      const key = contentKey(item);
      if (
        intentsRef.current[key] !== undefined ||
        synchronizing[key] !== undefined
      ) {
        return;
      }

      const rawQuantity = quantityInputs[key] ?? "";
      const quantity = Number(rawQuantity);
      if (
        !/^\d+$/.test(rawQuantity) ||
        !Number.isInteger(quantity) ||
        quantity <= 0 ||
        quantity > item.deliverableQuantity
      ) {
        setContentMessage(key, {
          kind: "error",
          text: `Ingresá una cantidad entera entre 1 y ${item.deliverableQuantity}.`,
        });
        return;
      }

      const intent: DeliveryIntent = {
        phase: "submitting",
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
      setContentMessage,
      setIntent,
      synchronizing,
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
      void executeIntent(submittingIntent, operationalReference);
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
                  intent !== undefined || synchronizing[key] !== undefined;
                const isFullyDelivered = item.remainingQuantity === 0;
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

                    {intent?.phase === "submitting" && (
                      <p role="status">Confirmando entrega…</p>
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
