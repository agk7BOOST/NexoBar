import { useEffect, useRef, useState } from "react";
import {
  discardAntiforgeryToken,
  getAntiforgeryToken,
  SessionProblemError,
} from "../identity/sessionClient.ts";
import {
  OrderOperationsProblemError,
  type OrderResponse,
} from "./orderOperationsClient.ts";
import {
  createOrderEndingIntent,
  sendOrderEndingIntent,
  type OrderEndingIntent,
  type OrderEndingKind,
} from "./orderEndingClient.ts";

const blockers: Record<string, string> = {
  pending_composition:
    "Hay una Composición pendiente. Confirmala o descartala antes de Liquidar.",
  unresolved_fulfillment:
    "Quedan contenidos sin entregar. Completá la preparación que corresponda y la entrega.",
  already_liquidated: "El Pedido ya está liquidado.",
  state_inconsistent:
    "El Estado del Pedido es inconsistente. No se puede Liquidar.",
  not_liquidated: "El Pedido debe liquidarse antes de cerrarlo.",
  already_closed: "El Pedido ya está cerrado.",
  frozen: "El Pedido está congelado por su Liquidación.",
};

interface OrderEndingProps {
  order: OrderResponse;
  canAct: boolean;
  onRefresh: () => Promise<boolean>;
  onUnauthorized: () => void;
  onBusyChange: (busy: boolean) => void;
  occurredAt: string | null;
  onOccurredAt: (occurredAt: string) => void;
}

export function OrderEnding({
  order,
  canAct,
  onRefresh,
  onUnauthorized,
  onBusyChange,
  occurredAt,
  onOccurredAt,
}: OrderEndingProps) {
  const [medium, setMedium] = useState("");
  const [intent, setIntent] = useState<OrderEndingIntent | null>(null);
  const [phase, setPhase] = useState<
    "idle" | "submitting" | "uncertain" | "refreshing"
  >("idle");
  const [message, setMessage] = useState<string | null>(null);
  const busy = useRef(false);
  const sending = useRef(false);
  const mounted = useRef(true);
  useEffect(() => {
    mounted.current = true;
    return () => {
      mounted.current = false;
    };
  }, []);

  function lock(value: boolean) {
    busy.current = value;
    onBusyChange(value);
  }

  function unauthorized() {
    discardAntiforgeryToken();
    setIntent(null);
    setPhase("idle");
    lock(false);
    onUnauthorized();
  }

  async function refresh() {
    setPhase("refreshing");
    const refreshed = await onRefresh();
    if (!mounted.current) return;
    if (refreshed) {
      setPhase("idle");
      lock(false);
    } else {
      setMessage(
        "No se pudo actualizar el Pedido. Actualizá su Estado antes de continuar.",
      );
    }
  }

  async function execute(next: OrderEndingIntent, retry: boolean) {
    if (sending.current) return;
    sending.current = true;
    setPhase("submitting");
    setMessage(null);
    let token: string;
    try {
      token = await getAntiforgeryToken();
      if (!mounted.current) return;
    } catch (error) {
      if (!mounted.current) return;
      sending.current = false;
      if (error instanceof SessionProblemError && error.status === 401) {
        unauthorized();
        return;
      }
      setMessage("No se pudo obtener la protección de la solicitud.");
      setPhase(retry ? "uncertain" : "idle");
      if (!retry) {
        setIntent(null);
        lock(false);
      }
      return;
    }
    try {
      const timestamp = await sendOrderEndingIntent(next, token);
      if (!mounted.current) return;
      if (timestamp !== null) onOccurredAt(timestamp);
    } catch (error) {
      if (!mounted.current) return;
      sending.current = false;
      if (!(error instanceof OrderOperationsProblemError)) {
        setPhase("uncertain");
        setMessage(
          "Resultado incierto. Reintentá la misma operación para resolverlo.",
        );
        return;
      }
      if (error.problem.status === 401) {
        unauthorized();
        return;
      }
      setIntent(null);
      const code = error.problem.code?.split(".").at(-1) ?? "";
      if (error.problem.status === 409 || error.problem.status === 404) {
        setMessage(
          blockers[code] ??
            (code === "idempotency_key_conflict"
              ? "La operación no coincide con la intención original. Se consultará el Estado vigente."
              : "El Estado del Pedido cambió. Se consultará el Estado vigente."),
        );
        await refresh();
        return;
      }
      if (code === "antiforgery_invalid") discardAntiforgeryToken();
      setMessage(
        error.problem.status === 403
          ? "Esta Identity no tiene autorización para realizar esta operación."
          : "La operación fue rechazada. Revisá los datos e intentá nuevamente.",
      );
      setPhase("idle");
      lock(false);
      return;
    }
    setIntent(null);
    sending.current = false;
    setMedium("");
    await refresh();
  }

  function begin(kind: OrderEndingKind) {
    if (busy.current || !canAct || order.isClosed) return;
    if (
      kind === "close"
        ? !order.isClosureEligible || !order.isLiquidated
        : !order.isLiquidationEligible || order.isFrozen
    )
      return;
    const trimmed = medium.trim();
    if (kind === "simple" && (trimmed.length === 0 || trimmed.length > 200)) {
      setMessage(
        "Ingresá un medio de pago declarado de 1 a 200 caracteres, sin contar espacios exteriores.",
      );
      return;
    }
    const next = createOrderEndingIntent(
      order.operationalReference,
      kind,
      trimmed,
    );
    lock(true);
    setIntent(next);
    void execute(next, false);
  }

  const disabled = !canAct || phase !== "idle";
  return (
    <section aria-label="Liquidación y Cierre" className="order-ending">
      <h3>Liquidación y Cierre</h3>
      <dl className="confirmation-summary">
        <div>
          <dt>Importe funcional actual</dt>
          <dd>{order.functionalAmount}</dd>
        </div>
        <div>
          <dt>Liquidación</dt>
          <dd>
            {order.isLiquidationEligible ? "Disponible" : "No disponible"}
          </dd>
        </div>
        <div>
          <dt>Liquidado</dt>
          <dd>{order.isLiquidated ? "Sí" : "No"}</dd>
        </div>
        <div>
          <dt>Congelado</dt>
          <dd>{order.isFrozen ? "Sí" : "No"}</dd>
        </div>
        <div>
          <dt>Cierre</dt>
          <dd>{order.isClosureEligible ? "Disponible" : "No disponible"}</dd>
        </div>
        <div>
          <dt>Cerrado</dt>
          <dd>{order.isClosed ? "Sí" : "No"}</dd>
        </div>
      </dl>
      {order.liquidationBlockers?.length > 0 && (
        <ul>
          {order.liquidationBlockers.map((code) => (
            <li key={code}>
              {blockers[code] ??
                "El Pedido tiene un bloqueo de Liquidación no reconocido. Actualizá su Estado."}
            </li>
          ))}
        </ul>
      )}
      {order.isFrozen && (
        <p role="status">
          Pedido congelado. Las operaciones ordinarias ya no están disponibles.
        </p>
      )}
      {order.isLiquidated && (
        <dl className="confirmation-summary">
          <div>
            <dt>Momento de Liquidación</dt>
            <dd>
              {occurredAt === null ? (
                "Fecha de Liquidación no disponible en esta consulta."
              ) : (
                <time dateTime={occurredAt}>{occurredAt}</time>
              )}
            </dd>
          </div>
          <div>
            <dt>Importe liquidado</dt>
            <dd>{order.liquidatedAmount}</dd>
          </div>
          <div>
            <dt>Modo de Liquidación</dt>
            <dd>
              {order.liquidationMode === "Simple"
                ? "Liquidación simple"
                : order.liquidationMode === "ExternalCollection"
                  ? "Cobro gestionado externamente"
                  : order.liquidationMode}
            </dd>
          </div>
          {order.liquidationMode === "Simple" && (
            <div>
              <dt>Medio de pago declarado</dt>
              <dd>{order.declaredPaymentMedium}</dd>
            </div>
          )}
        </dl>
      )}
      {!order.isLiquidated && !order.isClosed && !order.isFrozen && (
        <>
          <p>Después de Liquidar, el Pedido quedará congelado.</p>
          <form
            aria-label="Liquidación simple"
            noValidate
            onSubmit={(event) => {
              event.preventDefault();
              begin("simple");
            }}
          >
            <label htmlFor="declared-payment-medium">
              Medio de pago declarado
            </label>
            <input
              id="declared-payment-medium"
              type="text"
              value={medium}
              required
              aria-describedby="declared-payment-medium-help"
              disabled={disabled || !order.isLiquidationEligible}
              onChange={(event) => setMedium(event.target.value)}
            />
            <p id="declared-payment-medium-help">
              De 1 a 200 caracteres, sin contar espacios exteriores.
            </p>
            <button
              type="submit"
              disabled={disabled || !order.isLiquidationEligible}
            >
              Liquidar
            </button>
          </form>
          <div className="external-collection">
            <h4>Cobro gestionado externamente</h4>
            <p>Registra la Liquidación sin declarar un medio de pago.</p>
            <button
              type="button"
              className="secondary-button"
              disabled={disabled || !order.isLiquidationEligible}
              onClick={() => begin("external")}
            >
              Registrar cobro gestionado externamente
            </button>
          </div>
        </>
      )}
      {order.isLiquidated && order.isClosureEligible && !order.isClosed && (
        <button
          type="button"
          disabled={disabled}
          onClick={() => begin("close")}
        >
          Cerrar Pedido
        </button>
      )}
      {order.isClosed && (
        <div role="status">
          <strong>Pedido cerrado</strong>
          {order.closedAt && (
            <p>
              Cerrado el <time dateTime={order.closedAt}>{order.closedAt}</time>
            </p>
          )}
        </div>
      )}
      {message && <p role="alert">{message}</p>}
      {phase === "submitting" && <p role="status">Enviando operación…</p>}
      {phase === "uncertain" && intent && (
        <div>
          <p>
            {intent.kind === "close"
              ? "Cierre"
              : intent.kind === "simple"
                ? `Liquidación simple: ${JSON.parse(intent.body!).declaredPaymentMedium}`
                : "Cobro gestionado externamente"}
            . Se conserva la misma intención.
          </p>
          <button
            type="button"
            onClick={() => {
              if (phase === "uncertain") void execute(intent, true);
            }}
          >
            Reintentar misma operación
          </button>
        </div>
      )}
      {phase === "refreshing" && (
        <button type="button" onClick={() => void refresh()}>
          Actualizar Estado del Pedido
        </button>
      )}
    </section>
  );
}
