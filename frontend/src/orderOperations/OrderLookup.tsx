import {
  type FormEvent,
  useCallback,
  useEffect,
  useRef,
  useState,
} from "react";
import { OrderEnding } from "./OrderEnding.tsx";
import type { Product } from "../catalog/catalogClient.ts";
import {
  getOrder,
  OrderLookupNetworkError,
  OrderOperationsProblemError,
  type OrderOperationsProblemDetails,
  type OrderResponse,
} from "./orderOperationsClient.ts";

export interface RequestedOrderLookup {
  operationalReference: string;
  sequence: number;
}

interface OrderLookupProps {
  products: Product[];
  requestedLookup?: RequestedOrderLookup;
  activeOperationalReference: string | null;
  onContinueOrder: (operationalReference: string) => void;
  onOpenDelivery?: (operationalReference: string) => void;
  onUnauthorized?: () => void;
  identityId?: string;
  onOrderState?: (order: OrderResponse) => void;
  onEndingBusy?: (reference: string, busy: boolean) => void;
}

function lookupErrorMessage(problem: OrderOperationsProblemDetails): string {
  if (problem.status === 403) {
    return "Esta Identity no tiene autorización para consultar el Pedido.";
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
  products,
  requestedLookup,
  activeOperationalReference,
  onContinueOrder,
  onOpenDelivery,
  onUnauthorized,
  identityId,
  onOrderState,
  onEndingBusy,
}: OrderLookupProps) {
  const [operationalReference, setOperationalReference] = useState("");
  const [order, setOrder] = useState<OrderResponse | null>(null);
  const [errorMessage, setErrorMessage] = useState<string | null>(null);
  const [isLoading, setIsLoading] = useState(false);
  const [endingBusy, setEndingBusy] = useState(false);
  const [liquidationTimes, setLiquidationTimes] = useState<
    Record<string, string>
  >({});
  const endingBusyRef = useRef(false);
  const sequence = useRef(0);
  useEffect(
    () => () => {
      sequence.current++;
    },
    [],
  );

  const lookup = useCallback(
    async (reference: string, preserveOrder = false): Promise<boolean> => {
      if (endingBusyRef.current && !preserveOrder) return false;
      const request = ++sequence.current;
      setIsLoading(true);
      if (!preserveOrder) setOrder(null);
      setErrorMessage(null);

      try {
        const loaded = await getOrder(reference);
        if (request !== sequence.current) return false;
        setOrder(loaded);
        onOrderState?.(loaded);
        return true;
      } catch (error) {
        if (request !== sequence.current) return false;
        if (error instanceof OrderOperationsProblemError) {
          if (error.problem.status === 401) onUnauthorized?.();
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
        if (request === sequence.current) setIsLoading(false);
      }
    },
    [onOrderState, onUnauthorized],
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

  async function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    await lookup(operationalReference);
  }

  const isDisplayedOrderActive =
    order !== null &&
    !order.isFrozen &&
    !order.isClosed &&
    order.operationalReference === activeOperationalReference;

  return (
    <section className="panel" aria-labelledby="order-lookup-title">
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
          disabled={isLoading || endingBusy}
          required
        />
        <button type="submit" disabled={isLoading || endingBusy}>
          {isLoading ? "Buscando…" : "Buscar Pedido"}
        </button>
      </form>

      {errorMessage && <p role="alert">{errorMessage}</p>}

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
              <dd>{order.context}</dd>
            </div>
          </dl>

          {!order.isFrozen &&
            !order.isClosed &&
            (isDisplayedOrderActive ? (
              <p className="active-order-indicator" role="status">
                Este Pedido está activo para una nueva Incorporación.
              </p>
            ) : (
              <button
                type="button"
                onClick={() => onContinueOrder(order.operationalReference)}
                disabled={endingBusy}
              >
                Continuar este Pedido
              </button>
            ))}

          <OrderEnding
            key={`${identityId ?? "anonymous"}:${order.operationalReference}`}
            order={order}
            canAct={identityId !== undefined && !isLoading}
            occurredAt={liquidationTimes[order.operationalReference] ?? null}
            onOccurredAt={(timestamp) =>
              setLiquidationTimes((current) => ({
                ...current,
                [order.operationalReference]: timestamp,
              }))
            }
            onRefresh={() => lookup(order.operationalReference, true)}
            onUnauthorized={() => onUnauthorized?.()}
            onBusyChange={(busy) => {
              endingBusyRef.current = busy;
              setEndingBusy(busy);
              onEndingBusy?.(order.operationalReference, busy);
            }}
          />

          {onOpenDelivery && (
            <button
              type="button"
              className="secondary-button"
              onClick={() => onOpenDelivery(order.operationalReference)}
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
            OrderOperations; no se sustituye por el precio vigente del Catálogo.
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
                      <th scope="col">Producto (nombre actual)</th>
                      <th scope="col">Cantidad</th>
                      <th scope="col">Precio aplicado histórico</th>
                      <th scope="col">Instrucción confirmada</th>
                    </tr>
                  </thead>
                  <tbody>
                    {incorporation.items.map((item) => {
                      const product = products.find(
                        (candidate) => candidate.id === item.productId,
                      );

                      return (
                        <tr
                          key={`${item.productId}:${item.instruction ?? ""}`}
                          aria-label={`${product?.operationalName ?? item.productId}, cantidad ${item.quantity}, ${item.instruction ?? "sin instrucción"}`}
                        >
                          <td>{product?.operationalName ?? item.productId}</td>
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
  );
}
