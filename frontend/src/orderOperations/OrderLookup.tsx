import { type FormEvent, useState } from "react";
import type { Product } from "../catalog/catalogClient.ts";
import {
  getOrder,
  OrderLookupNetworkError,
  OrderOperationsProblemError,
  type OrderOperationsProblemDetails,
  type OrderResponse,
} from "./orderOperationsClient.ts";

interface OrderLookupProps {
  products: Product[];
}

function lookupErrorMessage(problem: OrderOperationsProblemDetails): string {
  if (problem.code === "order_operations.order.operational_reference_invalid") {
    return "La Referencia operacional no es válida.";
  }

  if (problem.code === "order_operations.order.not_found") {
    return "No se encontró un Pedido con esa Referencia operacional.";
  }

  return "No se pudo consultar el Pedido. Revisá la Referencia e intentá nuevamente.";
}

export function OrderLookup({ products }: OrderLookupProps) {
  const [operationalReference, setOperationalReference] = useState("");
  const [order, setOrder] = useState<OrderResponse | null>(null);
  const [errorMessage, setErrorMessage] = useState<string | null>(null);
  const [isLoading, setIsLoading] = useState(false);

  async function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setIsLoading(true);
    setOrder(null);
    setErrorMessage(null);

    try {
      setOrder(await getOrder(operationalReference));
    } catch (error) {
      if (error instanceof OrderOperationsProblemError) {
        setErrorMessage(lookupErrorMessage(error.problem));
      } else {
        setErrorMessage(
          error instanceof OrderLookupNetworkError
            ? "No se pudo consultar el Pedido por un fallo de comunicación."
            : "No se pudo completar la consulta del Pedido.",
        );
      }
    } finally {
      setIsLoading(false);
    }
  }

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
          disabled={isLoading}
          required
        />
        <button type="submit" disabled={isLoading}>
          {isLoading ? "Buscando…" : "Buscar Pedido"}
        </button>
      </form>

      {errorMessage && <p role="alert">{errorMessage}</p>}

      {order && (
        <div
          className="order-lookup-result"
          role="region"
          aria-label="Pedido consultado"
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
                    </tr>
                  </thead>
                  <tbody>
                    {incorporation.items.map((item) => {
                      const product = products.find(
                        (candidate) => candidate.id === item.productId,
                      );

                      return (
                        <tr key={item.productId}>
                          <td>{product?.operationalName ?? item.productId}</td>
                          <td>{item.quantity}</td>
                          <td>{item.appliedPrice}</td>
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
