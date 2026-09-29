import {
  type FormEvent,
  useCallback,
  useEffect,
  useRef,
  useState,
} from "react";
import { OrderEnding } from "./OrderEnding.tsx";
import { AppliedPriceCorrection } from "./AppliedPriceCorrection.tsx";
import { CompleteCancellation } from "./CompleteCancellation.tsx";
import { ActiveOrderFreshnessSubscription } from "../notifications/ActiveOrderFreshnessSubscription.tsx";
import { FreshnessReadCoordinator } from "../notifications/FreshnessReadCoordinator.ts";
import {
  evaluateCompleteCancellation,
  type CompleteCancellationEvaluation,
} from "./completeCancellationClient.ts";
import type { OperationalProduct } from "../catalog/catalogClient.ts";
import {
  getOrder,
  listOrderContexts,
  changeOrderContext,
  isOrderCompletelyCancelled,
  OrderLookupNetworkError,
  OrderOperationsProblemError,
  type OrderOperationsProblemDetails,
  type OrderResponse,
  type OperationalContextOption,
} from "./orderOperationsClient.ts";
import { getAntiforgeryToken } from "../identity/sessionClient.ts";
import { TerminalOrderHistoryView } from "./TerminalOrderHistoryView.tsx";

export interface RequestedOrderLookup {
  operationalReference: string;
  sequence: number;
}

interface OrderLookupProps {
  products?: OperationalProduct[];
  requestedLookup?: RequestedOrderLookup;
  activeOperationalReference: string | null;
  activeOrderId?: string | null;
  onContinueOrder: (operationalReference: string) => void;
  onOpenDelivery?: (operationalReference: string) => void;
  onUnauthorized?: () => void;
  identityId?: string;
  onOrderState?: (order: OrderResponse) => void;
  isOrderMutationBusy?: (reference: string) => boolean;
  onEndingBusy?: (reference: string, busy: boolean) => void;
  onActiveOrderRetired?: (reference: string) => void;
  canChangeOrderContext?: boolean;
  canViewTerminalHistory?: boolean;
}

function lookupErrorMessage(problem: OrderOperationsProblemDetails): string {
  if (problem.status === 403) {
    return "Tu usuario no tiene autorización para consultar el pedido.";
  }
  if (problem.code === "order_operations.order.operational_reference_invalid") {
    return "La Referencia operacional no es válida.";
  }

  if (problem.code === "order_operations.order.not_found") {
    return "No se encontró un Pedido con esa Referencia operacional.";
  }

  return "No se pudo consultar el Pedido. Revisá la Referencia e intentá nuevamente.";
}

export function OrderLookup({
  requestedLookup,
  activeOperationalReference,
  activeOrderId = null,
  onContinueOrder,
  onOpenDelivery,
  onUnauthorized,
  identityId,
  onOrderState,
  onEndingBusy,
  onActiveOrderRetired,
  isOrderMutationBusy,
  canChangeOrderContext = false,
  canViewTerminalHistory = false,
}: OrderLookupProps) {
  const [operationalReference, setOperationalReference] = useState("");
  const [order, setOrder] = useState<OrderResponse | null>(null);
  const [errorMessage, setErrorMessage] = useState<string | null>(null);
  const [closureConfirmation, setClosureConfirmation] = useState<{
    reference: string;
    closedAt: string;
  } | null>(null);
  const [cancellationConfirmation, setCancellationConfirmation] = useState<{
    reference: string;
    occurredAt: string;
    pendingCompositionDiscarded: boolean;
  } | null>(null);
  const [isLoading, setIsLoading] = useState(false);
  const [endingBusy, setEndingBusy] = useState(false);
  const [cancellationBusy, setCancellationBusy] = useState(false);
  const [priceBusy, setPriceBusy] = useState(false);
  const [evaluation, setEvaluation] =
    useState<CompleteCancellationEvaluation | null>(null);
  const [evaluationError, setEvaluationError] = useState<string | null>(null);
  const [liquidationTimes, setLiquidationTimes] = useState<
    Record<string, string>
  >({});
  const [contextOptions, setContextOptions] = useState<
    OperationalContextOption[]
  >([]);
  const [contextTarget, setContextTarget] = useState("");
  const [contextNotice, setContextNotice] = useState("");
  const [contextPending, setContextPending] = useState<{
    orderId: string;
    request: { expectedCurrentContextId: string; newContextId: string };
    key: string;
    token: string;
  } | null>(null);
  const [contextBusy, setContextBusy] = useState(false);
  useEffect(() => {
    if (canChangeOrderContext)
      void listOrderContexts()
        .then(setContextOptions)
        .catch(() => setContextOptions([]));
  }, [canChangeOrderContext]);
  const endingBusyRef = useRef(false);
  const sequence = useRef(0);
  const activeReadCoordinator = useRef(new FreshnessReadCoordinator());
  useEffect(
    () => () => {
      sequence.current++;
      activeReadCoordinator.current.cancel();
    },
    [],
  );

  const lookup = useCallback(
    async (
      reference: string,
      preserveOrder = false,
      requireEvaluation = false,
      terminalOnNotFound = false,
      isCurrent: () => boolean = () => true,
    ): Promise<boolean> => {
      if (endingBusyRef.current && !preserveOrder) return false;
      const request = ++sequence.current;
      setIsLoading(true);
      if (!preserveOrder) {
        setOrder(null);
        setClosureConfirmation(null);
        setCancellationConfirmation(null);
      }
      setErrorMessage(null);
      setEvaluation(null);
      setEvaluationError(null);

      try {
        const loaded = await getOrder(reference);
        if (request !== sequence.current || !isCurrent()) return false;
        setOrder(loaded);
        onOrderState?.(loaded);
        if (identityId !== undefined) {
          try {
            const evaluated = await evaluateCompleteCancellation(reference);
            if (request !== sequence.current || !isCurrent()) return false;
            setEvaluation(evaluated);
          } catch (error) {
            if (request !== sequence.current || !isCurrent()) return false;
            if (
              error instanceof OrderOperationsProblemError &&
              error.problem.status === 401
            )
              onUnauthorized?.();
            if (
              terminalOnNotFound &&
              error instanceof OrderOperationsProblemError &&
              error.problem.status === 404
            ) {
              setOrder(null);
              setEvaluation(null);
              setEvaluationError(null);
              onActiveOrderRetired?.(reference);
              return false;
            }
            setEvaluationError(
              error instanceof OrderOperationsProblemError &&
                error.problem.status === 403
                ? "Tu usuario no tiene autorización para evaluar la cancelación completa."
                : "No se pudo evaluar la cancelación completa. Actualizá el Pedido antes de cancelar.",
            );
            return !requireEvaluation;
          }
        }
        return true;
      } catch (error) {
        if (request !== sequence.current || !isCurrent()) return false;
        if (error instanceof OrderOperationsProblemError) {
          if (error.problem.status === 401) onUnauthorized?.();
          if (terminalOnNotFound && error.problem.status === 404) {
            setOrder(null);
            setEvaluation(null);
            setEvaluationError(null);
            onActiveOrderRetired?.(reference);
            return false;
          }
          setErrorMessage(lookupErrorMessage(error.problem));
        } else {
          setErrorMessage(
            error instanceof OrderLookupNetworkError
              ? "No se pudo consultar el Pedido por un fallo de comunicación."
              : "No se pudo completar la consulta del Pedido.",
          );
        }
        return false;
      } finally {
        if (request === sequence.current && isCurrent()) setIsLoading(false);
      }
    },
    [onOrderState, onUnauthorized, onActiveOrderRetired, identityId],
  );

  useEffect(() => {
    if (requestedLookup === undefined) {
      return;
    }

    const timeout = window.setTimeout(() => {
      void lookup(requestedLookup.operationalReference);
    }, 0);

    return () => window.clearTimeout(timeout);
  }, [lookup, requestedLookup]);

  const invalidateActiveOrder = useCallback(() => {
    // A command already performs its own authoritative read after its result.
    // An SSE read here would supersede that read and report a false failure.
    if (endingBusyRef.current) return;
    if (
      order === null ||
      order.operationalReference !== activeOperationalReference
    )
      return;
    const reference = order.operationalReference;
    activeReadCoordinator.current.invalidate(async (isCurrent) => {
      await lookup(reference, true, false, true, isCurrent);
    });
  }, [activeOperationalReference, lookup, order]);

  const activeOrderScopeId =
    order !== null && order.operationalReference === activeOperationalReference
      ? activeOrderId
      : null;

  async function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    await lookup(operationalReference);
  }

  const isDisplayedOrderActive =
    order !== null &&
    !order.isFrozen &&
    !order.isClosed &&
    !isOrderCompletelyCancelled(order) &&
    order.operationalReference === activeOperationalReference;

  async function submitContextChange(
    intent: NonNullable<typeof contextPending>,
  ) {
    setContextBusy(true);
    setContextNotice("");
    try {
      await changeOrderContext(
        intent.orderId,
        intent.request,
        intent.key,
        intent.token,
      );
      setContextPending(null);
      setContextTarget("");
      await lookup(order?.operationalReference ?? operationalReference, true);
      setContextNotice("Contexto actualizado desde el Estado del Pedido.");
    } catch (error) {
      if (error instanceof OrderOperationsProblemError) {
        const code = error.problem.code ?? "";
        setContextPending(null);
        if (code === "order.context_change.expected_context_stale")
          setContextNotice(
            "Otra persona cambió el Contexto. Se actualizó el Pedido; iniciá una nueva intención si necesitás otro cambio.",
          );
        else if (code === "order.context_change.no_change")
          setContextNotice(
            "El Pedido ya tiene ese Contexto. No se registró un cambio.",
          );
        else if (code === "order.context_change.target_context_not_found") {
          setContextNotice("El Contexto seleccionado ya no está disponible.");
          void listOrderContexts().then(setContextOptions);
        } else
          setContextNotice(
            "No se pudo cambiar el Contexto. Se actualizó el Pedido.",
          );
        await lookup(order?.operationalReference ?? operationalReference, true);
      } else {
        setContextPending(intent);
        setContextNotice(
          "Resultado no confirmado. Reintentá exactamente el mismo cambio.",
        );
      }
    } finally {
      setContextBusy(false);
    }
  }

  async function handleContextChange() {
    if (
      !order ||
      !activeOrderId ||
      !contextTarget ||
      contextTarget === order.contextId
    )
      return;
    try {
      const token = await getAntiforgeryToken();
      await submitContextChange({
        orderId: activeOrderId,
        request: {
          expectedCurrentContextId: order.contextId,
          newContextId: contextTarget,
        },
        key: crypto.randomUUID(),
        token,
      });
    } catch {
      setContextNotice("No se pudo preparar el cambio seguro de Contexto.");
    }
  }

  return (
    <>
      <section className="panel" aria-labelledby="order-lookup-title">
        <ActiveOrderFreshnessSubscription
          orderId={activeOrderScopeId}
          invalidate={invalidateActiveOrder}
        />
        <h2 id="order-lookup-title">Consultar Pedido</h2>
        <form
          className="order-lookup-form"
          onSubmit={(event) => void handleSubmit(event)}
        >
          <label htmlFor="order-operational-reference">
            Referencia operacional
          </label>
          <input
            id="order-operational-reference"
            name="operationalReference"
            value={operationalReference}
            onChange={(event) => setOperationalReference(event.target.value)}
            disabled={isLoading || endingBusy || cancellationBusy || priceBusy}
            required
          />
          <button
            type="submit"
            disabled={isLoading || endingBusy || cancellationBusy || priceBusy}
          >
            {isLoading ? "Buscando…" : "Buscar Pedido"}
          </button>
        </form>

        {errorMessage && <p role="alert">{errorMessage}</p>}
        {closureConfirmation && (
          <p role="status">
            Pedido cerrado: {closureConfirmation.reference}. Cerrado el{" "}
            <time dateTime={closureConfirmation.closedAt}>
              {closureConfirmation.closedAt}
            </time>
          </p>
        )}
        {cancellationConfirmation && (
          <p role="status">
            Pedido completamente cancelado: {cancellationConfirmation.reference}
            .
            {cancellationConfirmation.pendingCompositionDiscarded &&
              " La Composición pendiente fue descartada."}{" "}
            Cancelado el{" "}
            <time dateTime={cancellationConfirmation.occurredAt}>
              {cancellationConfirmation.occurredAt}
            </time>
            . La consulta histórica permanece disponible.
          </p>
        )}

        {order && (
          <div
            className="order-lookup-result"
            role="region"
            aria-label={
              isDisplayedOrderActive ? "Pedido activo" : "Pedido consultado"
            }
          >
            <dl className="confirmation-summary">
              <div>
                <dt>Referencia operacional</dt>
                <dd>{order.operationalReference}</dd>
              </div>
              <div>
                <dt>Contexto actual</dt>
                <dd aria-label="Contexto actual del Pedido">{order.context}</dd>
              </div>
            </dl>

            {canChangeOrderContext &&
              isDisplayedOrderActive &&
              !order.isFrozen &&
              !order.isClosed &&
              !isOrderCompletelyCancelled(order) && (
                <section aria-label="Cambiar Contexto del Pedido">
                  <p>
                    El mismo Pedido continúa; cambia únicamente su Contexto de
                    coordinación.
                  </p>
                  <label htmlFor="order-context-change-target">
                    Nuevo Contexto
                  </label>
                  <select
                    id="order-context-change-target"
                    aria-label="Contexto destino"
                    value={contextTarget}
                    onChange={(event) => setContextTarget(event.target.value)}
                    disabled={contextBusy || contextPending !== null}
                  >
                    <option value="">Seleccionar Contexto</option>
                    {contextOptions
                      .filter((option) => option.id !== order.contextId)
                      .map((option) => (
                        <option key={option.id} value={option.id}>
                          {option.operationalName}
                        </option>
                      ))}
                  </select>
                  <button
                    type="button"
                    aria-label="Cambiar contexto"
                    onClick={() => void handleContextChange()}
                    disabled={
                      contextBusy || contextPending !== null || !contextTarget
                    }
                  >
                    Cambiar contexto
                  </button>
                  {contextNotice && (
                    <p role="status" aria-label="Estado del cambio de Contexto">
                      {contextNotice}
                    </p>
                  )}
                  {contextPending && (
                    <div role="region" aria-label="Cambio de Contexto incierto">
                      <button
                        type="button"
                        disabled={contextBusy}
                        onClick={() => void submitContextChange(contextPending)}
                      >
                        Reintentar mismo cambio
                      </button>
                    </div>
                  )}
                </section>
              )}

            {!order.isFrozen &&
              !order.isClosed &&
              !isOrderCompletelyCancelled(order) &&
              !evaluation?.isTerminal &&
              (isDisplayedOrderActive ? (
                <p className="active-order-indicator" role="status">
                  Este Pedido está activo para una nueva Incorporación.
                </p>
              ) : (
                <button
                  type="button"
                  onClick={() => onContinueOrder(order.operationalReference)}
                  disabled={
                    endingBusy ||
                    cancellationBusy ||
                    priceBusy ||
                    isOrderMutationBusy?.(order.operationalReference)
                  }
                >
                  Continuar este Pedido
                </button>
              ))}

            <OrderEnding
              key={`${identityId ?? "anonymous"}:${order.operationalReference}`}
              order={order}
              canAct={
                identityId !== undefined &&
                !cancellationBusy &&
                !priceBusy &&
                !isLoading &&
                !isOrderMutationBusy?.(order.operationalReference)
              }
              occurredAt={liquidationTimes[order.operationalReference] ?? null}
              onOccurredAt={(timestamp) =>
                setLiquidationTimes((current) => ({
                  ...current,
                  [order.operationalReference]: timestamp,
                }))
              }
              onClosed={(closedAt) => {
                const reference = order.operationalReference;
                setOrder(null);
                setEvaluation(null);
                setClosureConfirmation({ reference, closedAt });
                onActiveOrderRetired?.(reference);
              }}
              onRefresh={() => lookup(order.operationalReference, true)}
              onUnauthorized={() => onUnauthorized?.()}
              onBusyChange={(busy) => {
                endingBusyRef.current = busy;
                setEndingBusy(busy);
                onEndingBusy?.(order.operationalReference, busy);
              }}
            />

            <CompleteCancellation
              key={`cancellation:${identityId ?? "anonymous"}:${order.operationalReference}`}
              evaluation={evaluation}
              evaluationError={evaluationError}
              canAct={
                identityId !== undefined &&
                !isLoading &&
                !endingBusy &&
                !priceBusy &&
                !isOrderMutationBusy?.(order.operationalReference)
              }
              onRefresh={() => lookup(order.operationalReference, true, true)}
              onCancelled={(occurredAt, pendingCompositionDiscarded) => {
                const reference = order.operationalReference;
                sequence.current++;
                activeReadCoordinator.current.cancel();
                setOrder(null);
                setEvaluation(null);
                setCancellationConfirmation({
                  reference,
                  occurredAt,
                  pendingCompositionDiscarded,
                });
                onActiveOrderRetired?.(reference);
              }}
              onUnauthorized={() => onUnauthorized?.()}
              onBusyChange={(busy) => {
                endingBusyRef.current = busy;
                setCancellationBusy(busy);
                onEndingBusy?.(order.operationalReference, busy);
              }}
            />

            {identityId !== undefined && (
              <AppliedPriceCorrection
                key={`price:${identityId}:${order.operationalReference}`}
                orderId={order.operationalReference}
                canAct={
                  !isLoading &&
                  !endingBusy &&
                  !cancellationBusy &&
                  !isOrderMutationBusy?.(order.operationalReference)
                }
                isTerminal={
                  order.isLiquidated ||
                  order.isFrozen ||
                  order.isClosed ||
                  isOrderCompletelyCancelled(order) ||
                  evaluation?.isTerminal === true
                }
                onRefresh={() => lookup(order.operationalReference, true)}
                onUnauthorized={() => onUnauthorized?.()}
                onBusyChange={(busy) => {
                  endingBusyRef.current = busy;
                  setPriceBusy(busy);
                  onEndingBusy?.(order.operationalReference, busy);
                }}
              />
            )}

            {onOpenDelivery && (
              <button
                type="button"
                className="secondary-button"
                onClick={() => onOpenDelivery(order.operationalReference)}
                disabled={priceBusy}
              >
                Abrir entrega de este Pedido
              </button>
            )}

            <p className="current-catalog-name-note">
              El nombre de Producto es una etiqueta del Catálogo actual y no
              constituye Historia del Pedido.
            </p>
            <p className="applied-price-note">
              El Precio aplicado es la condición histórica confirmada por
              OrderOperations; no se sustituye por el precio vigente del
              Catálogo.
            </p>

            {order.incorporations.map((incorporation) => (
              <article
                className="order-incorporation"
                key={incorporation.id}
                aria-label={`Incorporación ${incorporation.ordinal}`}
              >
                <h3>Incorporación {incorporation.ordinal}</h3>
                <dl className="confirmation-summary">
                  <div>
                    <dt>Ordinal</dt>
                    <dd>{incorporation.ordinal}</dd>
                  </div>
                  <div>
                    <dt>Identificador</dt>
                    <dd>{incorporation.id}</dd>
                  </div>
                  <div>
                    <dt>Momento de Confirmación</dt>
                    <dd>
                      <time dateTime={incorporation.confirmedAt}>
                        {incorporation.confirmedAt}
                      </time>
                    </dd>
                  </div>
                </dl>

                <div className="table-scroll">
                  <table>
                    <thead>
                      <tr>
                        <th scope="col">Producto (nombre confirmado)</th>
                        <th scope="col">Cantidad</th>
                        <th scope="col">Precio aplicado histórico</th>
                        <th scope="col">Instrucción confirmada</th>
                      </tr>
                    </thead>
                    <tbody>
                      {incorporation.items.map((item) => {
                        return (
                          <tr
                            key={`${item.productId}:${item.instruction ?? ""}`}
                            aria-label={`${item.productOperationalNameSnapshot ?? "Nombre histórico no disponible"}, cantidad ${item.quantity}, ${item.instruction ?? "sin instrucción"}`}
                          >
                            <td>
                              {item.productOperationalNameSnapshot ??
                                "Nombre histórico no disponible"}
                              {item.unavailableProductExceptionApplied && (
                                <span> Incorporado mediante intervención</span>
                              )}
                            </td>
                            <td>{item.quantity}</td>
                            <td>{item.appliedPrice}</td>
                            <td className="confirmed-instruction">
                              {item.instruction ?? "Sin instrucción"}
                            </td>
                          </tr>
                        );
                      })}
                    </tbody>
                  </table>
                </div>
              </article>
            ))}
          </div>
        )}
      </section>
      {canViewTerminalHistory && (
        <TerminalOrderHistoryView onUnauthorized={onUnauthorized} />
      )}
    </>
  );
}
