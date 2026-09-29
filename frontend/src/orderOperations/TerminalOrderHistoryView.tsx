import { useState, type FormEvent } from "react";
import {
  getTerminalOrderHistory,
  type TerminalOrderHistory,
  type TerminalHistoryContent,
} from "./terminalOrderHistoryClient.ts";
import {
  OrderLookupNetworkError,
  OrderOperationsProblemError,
} from "./orderOperationsClient.ts";
import { formatOperationalDate } from "../formatOperationalDate.ts";

function actor(id: string | null) {
  return (
    <span className="technical-reference">Actor: {id ?? "No disponible"}</span>
  );
}
function when(value: string) {
  return (
    <time dateTime={value} title={value}>
      {formatOperationalDate(value)}
    </time>
  );
}
function unavailable(error: unknown) {
  if (error instanceof OrderOperationsProblemError) {
    if (error.problem.status === 401)
      return "La sesión no está disponible. Iniciá sesión nuevamente.";
    if (error.problem.status === 403)
      return "No se pudo consultar el historial con la sesión actual.";
    if (error.problem.status === 404)
      return "Historial no disponible para esa referencia.";
    if (error.problem.code?.includes("operational_reference_invalid"))
      return "La Referencia operacional no es válida.";
  }
  if (error instanceof OrderLookupNetworkError)
    return "No se pudo consultar el historial por un fallo de comunicación.";
  return "No se pudo leer el historial. Intentá nuevamente.";
}
function Content({ content: c }: { content: TerminalHistoryContent }) {
  return (
    <article className="terminal-history-content">
      <h5>Contenido {c.contentOrdinal}</h5>
      <p>
        <strong>
          {c.productOperationalNameSnapshot ?? "Nombre histórico no disponible"}
        </strong>{" "}
        · Cantidad confirmada: {c.originalConfirmedQuantity}
      </p>
      <p>
        Precio confirmado: {c.appliedPrice} · Precio histórico efectivo:{" "}
        {c.effectiveAppliedPrice}
      </p>
      <p>
        Instrucción: {c.instruction ?? "Sin instrucción"} · Preparación:{" "}
        {c.requiresPreparation ? "Requerida" : "No requerida"}
      </p>
      {c.preparationResponsibilityId && (
        <p className="technical-reference">
          Identificador del destino de preparación:{" "}
          {c.preparationResponsibilityId}
        </p>
      )}
      {c.unavailableProductExceptionApplied && (
        <p>Incorporado mediante excepción por producto no disponible</p>
      )}
      {c.priceCorrections.length > 0 && (
        <section>
          <h6>Correcciones de precio</h6>
          <ol>
            {c.priceCorrections.map((x, i) => (
              <li key={i}>
                {x.previousPrice} → {x.resultingPrice};{" "}
                {actor(x.actorIdentityId)} · {when(x.occurredAtUtc)}
              </li>
            ))}
          </ol>
        </section>
      )}
      {c.corrections.length > 0 && (
        <section>
          <h6>Corrección de contenido</h6>
          <ol>
            {c.corrections.map((x, i) => (
              <li key={i}>
                {x.previousQuantity} → {x.resultingQuantity};{" "}
                {actor(x.actorIdentityId)} · {when(x.occurredAtUtc)}
              </li>
            ))}
          </ol>
        </section>
      )}
      {c.cancellations.length > 0 && (
        <section>
          <h6>Cancelación de contenido</h6>
          <ol>
            {c.cancellations.map((x, i) => (
              <li key={i}>
                {x.previousQuantity} → {x.resultingQuantity};{" "}
                {actor(x.actorIdentityId)} · {when(x.occurredAtUtc)}
              </li>
            ))}
          </ol>
        </section>
      )}
      {c.preparationHistory.length > 0 && (
        <section>
          <h6>Historial de preparación</h6>
          <ol>
            {c.preparationHistory.map((x, i) => (
              <li key={i}>
                {(
                  {
                    PreparationQuantityStarted: "Inicio",
                    PreparationQuantityReady: "Listo",
                    PreparationCorrection: "Corrección",
                    PreparationIntervention: "Intervención",
                  } as Record<string, string>
                )[x.type] ?? x.type}
                : {x.quantity} · Pendiente {x.resultingPendingQuantity}, en
                preparación {x.resultingInPreparationQuantity}, listo{" "}
                {x.resultingReadyQuantity}; {actor(x.actorIdentityId)} ·{" "}
                {when(x.occurredAtUtc)}
              </li>
            ))}
          </ol>
        </section>
      )}
      {c.deliveries.length > 0 && (
        <section>
          <h6>Entregas</h6>
          <ol>
            {c.deliveries.map((x, i) => (
              <li key={i}>
                {x.type === "QuantityDelivered" ? "Entrega registrada" : x.type}
                : {x.quantity} · acumulado {x.resultingDeliveredQuantity};{" "}
                {actor(x.actorIdentityId)} · {when(x.occurredAtUtc)}
              </li>
            ))}
          </ol>
        </section>
      )}
      {c.deliveryCorrections.length > 0 && (
        <section>
          <h6>Correcciones de entrega</h6>
          <ol>
            {c.deliveryCorrections.map((x, i) => (
              <li key={i}>
                {x.previousDeliveredQuantity} → {x.resultingDeliveredQuantity};{" "}
                {actor(x.actorIdentityId)} · {when(x.occurredAtUtc)}
              </li>
            ))}
          </ol>
        </section>
      )}
    </article>
  );
}
export function TerminalOrderHistoryView({
  onUnauthorized,
}: {
  onUnauthorized?: () => void;
}) {
  const [reference, setReference] = useState("");
  const [history, setHistory] = useState<TerminalOrderHistory | null>(null);
  const [error, setError] = useState("");
  const [loading, setLoading] = useState(false);
  async function submit(e: FormEvent) {
    e.preventDefault();
    setHistory(null);
    setError("");
    setLoading(true);
    try {
      setHistory(await getTerminalOrderHistory(reference));
    } catch (err) {
      if (
        err instanceof OrderOperationsProblemError &&
        err.problem.status === 401
      )
        onUnauthorized?.();
      setError(unavailable(err));
    } finally {
      setLoading(false);
    }
  }
  return (
    <section className="panel" aria-labelledby="terminal-history-lookup-title">
      <h2 id="terminal-history-lookup-title">Consultar historial terminal</h2>
      <form className="order-lookup-form" onSubmit={(e) => void submit(e)}>
        <label htmlFor="terminal-history-reference">
          Referencia operacional exacta
        </label>
        <input
          id="terminal-history-reference"
          value={reference}
          onChange={(e) => setReference(e.target.value)}
          required
          disabled={loading}
        />
        <button type="submit" disabled={loading}>
          {loading ? "Buscando…" : "Ver historial"}
        </button>
      </form>
      {loading && <p role="status">Cargando historial…</p>}
      {error && <p role="alert">{error}</p>}
      {history && <TerminalHistoryResult history={history} />}
    </section>
  );
}
function TerminalHistoryResult({
  history: h,
}: {
  history: TerminalOrderHistory;
}) {
  const cancellation = h.termination.type === "CompleteCancellation";
  return (
    <section className="order-lookup-result" aria-label="Historial del pedido">
      <h2>Historial del pedido</h2>
      <p role="status">Solo lectura</p>
      <dl className="confirmation-summary">
        <div>
          <dt>Terminación</dt>
          <dd>{cancellation ? "Cancelación completa" : "Cierre"}</dd>
        </div>
        <div>
          <dt>Fecha terminal</dt>
          <dd>{when(h.termination.occurredAt)}</dd>
        </div>
        <div>
          <dt>Contexto final</dt>
          <dd>{h.finalContextOperationalName}</dd>
        </div>
        <div>
          <dt>Referencia operacional</dt>
          <dd className="technical-reference">{h.operationalReference}</dd>
        </div>
      </dl>
      {h.contextChanges.length > 0 && (
        <section>
          <h3>Historial de cambios de Contexto</h3>
          <ol>
            {h.contextChanges.map((x) => (
              <li key={x.sequence}>
                {x.previousContextOperationalName} →{" "}
                {x.newContextOperationalName}; {actor(x.actorIdentityId)} ·{" "}
                {when(x.occurredAtUtc)}
              </li>
            ))}
          </ol>
        </section>
      )}
      <section>
        <h3>Incorporaciones</h3>
        {h.incorporations.map((i) => (
          <article key={i.id}>
            <h4>Incorporación {i.ordinal}</h4>
            <p>
              Confirmada: {when(i.confirmedAtUtc)} · {actor(i.actorIdentityId)}
            </p>
            <p>Contexto de confirmación: {i.confirmedContext}</p>
            {i.contents.map((c) => (
              <Content key={c.contentOrdinal} content={c} />
            ))}
          </article>
        ))}
      </section>
      {h.liquidation && (
        <section>
          <h3>Liquidación</h3>
          <p>
            Importe funcional: {h.liquidation.functionalAmount} · Modo:{" "}
            {h.liquidation.mode === "Simple"
              ? "Liquidación simple"
              : h.liquidation.mode === "ExternalCollection"
                ? "Cobro gestionado externamente"
                : h.liquidation.mode}
          </p>
          {h.liquidation.declaredPaymentMedium && (
            <p>Medio declarado: {h.liquidation.declaredPaymentMedium}</p>
          )}
          <p>
            {actor(h.liquidation.actorIdentityId)} ·{" "}
            {when(h.liquidation.occurredAt)}
          </p>
        </section>
      )}
      {h.closure && (
        <section>
          <h3>Cierre</h3>
          <p>
            {actor(h.closure.actorIdentityId)} · {when(h.closure.occurredAtUtc)}
          </p>
        </section>
      )}
      {h.completeCancellation && (
        <section>
          <h3>Cancelación completa</h3>
          <p>
            {actor(h.completeCancellation.actorIdentityId)} ·{" "}
            {when(h.completeCancellation.occurredAtUtc)}
          </p>
          {h.completeCancellation.pendingCompositionDiscarded && (
            <p>Se descartó la composición pendiente.</p>
          )}
          <ul>
            {h.completeCancellation.consequences.map((x, i) => (
              <li key={i}>
                Incorporación {x.incorporationId}, contenido {x.contentOrdinal}:
                Directo/Pendiente {x.directOrPendingQuantity}, en preparación{" "}
                {x.inPreparationQuantity}, listo {x.readyQuantity}, resultado{" "}
                {x.resultingFulfillmentQuantity}
              </li>
            ))}
          </ul>
        </section>
      )}
    </section>
  );
}
