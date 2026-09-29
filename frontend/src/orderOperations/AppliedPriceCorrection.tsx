import { useCallback, useEffect, useRef, useState } from "react";
import { ActiveOrderFreshnessSubscription } from "../notifications/ActiveOrderFreshnessSubscription.tsx";
import { FreshnessReadCoordinator } from "../notifications/FreshnessReadCoordinator.ts";
import {
  DeliveryProblemError,
  getOrderDelivery,
  type OrderDeliveryContent,
} from "../delivery/deliveryClient.ts";
import {
  discardAntiforgeryToken,
  getAntiforgeryToken,
  SessionProblemError,
} from "../identity/sessionClient.ts";
import { OrderOperationsProblemError } from "./orderOperationsClient.ts";
import {
  createAppliedPriceIntent,
  evaluateAppliedPrice,
  priceBlockers,
  sendAppliedPriceIntent,
  type AppliedPriceEvaluation,
  type AppliedPriceIntent,
} from "./appliedPriceCorrectionClient.ts";

interface Props {
  orderId: string;
  canAct: boolean;
  isTerminal: boolean;
  onRefresh: () => Promise<boolean>;
  onUnauthorized: () => void;
  onBusyChange: (busy: boolean) => void;
}
interface Row {
  content: OrderDeliveryContent;
  evaluation: AppliedPriceEvaluation;
}
function status(error: unknown) {
  return error instanceof OrderOperationsProblemError
    ? error.problem.status
    : error instanceof DeliveryProblemError ||
        error instanceof SessionProblemError
      ? error.status
      : undefined;
}
export function AppliedPriceCorrection({
  orderId,
  canAct,
  isTerminal,
  onRefresh,
  onUnauthorized,
  onBusyChange,
}: Props) {
  const [rows, setRows] = useState<Row[]>([]);
  const [confirmation, setConfirmation] =
    useState<AppliedPriceEvaluation | null>(null);
  const [phase, setPhase] = useState<
    "idle" | "reading" | "sending" | "uncertain" | "refreshing"
  >("idle");
  const [intent, setIntent] = useState<AppliedPriceIntent | null>(null);
  const [message, setMessage] = useState<string | null>(null);
  const locked = useRef(false);
  const sending = useRef(false);
  const confirmationOpen = useRef(false);
  const mounted = useRef(true);
  const evaluationCoordinator = useRef(new FreshnessReadCoordinator());
  useEffect(() => {
    const coordinator = evaluationCoordinator.current;
    mounted.current = true;
    return () => {
      mounted.current = false;
      confirmationOpen.current = false;
      coordinator.cancel();
    };
  }, []);
  const lock = useCallback(
    (value: boolean) => {
      locked.current = value;
      onBusyChange(value);
    },
    [onBusyChange],
  );
  const unauthorized = useCallback(() => {
    confirmationOpen.current = false;
    discardAntiforgeryToken();
    setIntent(null);
    setConfirmation(null);
    setRows([]);
    setPhase("idle");
    lock(false);
    onUnauthorized();
  }, [lock, onUnauthorized]);
  const readRows = useCallback(async (): Promise<Row[]> => {
    const delivery = await getOrderDelivery(orderId);
    if (delivery.orderId !== orderId)
      throw new Error("Unexpected Order target");
    return Promise.all(
      delivery.contents.map(async (content) => ({
        content,
        evaluation: await evaluateAppliedPrice({
          orderId,
          incorporationId: content.incorporationId,
          contentOrdinal: content.contentOrdinal,
        }),
      })),
    );
  }, [orderId]);
  async function read() {
    if (locked.current || !canAct) return;
    confirmationOpen.current = false;
    evaluationCoordinator.current.cancel();
    lock(true);
    setPhase("reading");
    setConfirmation(null);
    setMessage(null);
    setRows([]);
    try {
      const loaded = await readRows();
      if (mounted.current) setRows(loaded);
    } catch (error) {
      if (!mounted.current) return;
      if (status(error) === 401) {
        unauthorized();
        return;
      }
      setMessage(
        status(error) === 403
          ? "Tu usuario no tiene autorización para corregir precios aplicados en pedidos."
          : "No se pudieron consultar los precios aplicados. Intentá actualizar.",
      );
    } finally {
      if (mounted.current) {
        setPhase("idle");
        lock(false);
      }
    }
  }
  const invalidateEvaluations = useCallback(() => {
    // A previously queued SSE read must not erase an explicit confirmation.
    // The command itself revalidates Catalog and the Order before committing.
    if (confirmationOpen.current) return;
    if (rows.length === 0 && confirmation === null) return;
    evaluationCoordinator.current.invalidate(async (isCurrent) => {
      try {
        const loaded = await readRows();
        if (!mounted.current || !isCurrent() || confirmationOpen.current)
          return;
        setConfirmation(null);
        setRows(loaded);
      } catch (error) {
        if (!mounted.current || !isCurrent()) return;
        if (status(error) === 401) unauthorized();
      }
    });
  }, [confirmation, readRows, rows.length, unauthorized]);
  async function refresh() {
    confirmationOpen.current = false;
    setPhase("refreshing");
    setConfirmation(null);
    try {
      // Both prices and the economic/ending read must be authoritative before another intent.
      const orderRefreshed = await onRefresh();
      const loaded = await readRows();
      if (!mounted.current) return;
      if (!orderRefreshed) throw new Error("Order refresh failed");
      setRows(loaded);
      setPhase("idle");
      lock(false);
    } catch (error) {
      if (!mounted.current) return;
      if (status(error) === 401) {
        unauthorized();
        return;
      }
      setRows([]);
      setMessage(
        status(error) === 403
          ? "Tu usuario no tiene autorización para consultar los precios aplicados. Actualizá precios y pedido antes de continuar."
          : "No se pudo consultar el estado actual. Actualizá precios y Pedido antes de continuar.",
      );
    }
  }
  async function execute(exact: AppliedPriceIntent) {
    if (sending.current) return;
    sending.current = true;
    setPhase("sending");
    setMessage(null);
    try {
      await sendAppliedPriceIntent(exact);
      if (!mounted.current) return;
      setMessage(
        "Corrección de precio registrada. Consultando el Estado vigente.",
      );
    } catch (error) {
      if (!mounted.current) return;
      if (!(error instanceof OrderOperationsProblemError)) {
        setPhase("uncertain");
        setMessage(
          "Resultado incierto. Reintentá la misma corrección de precio para resolverlo.",
        );
        return;
      }
      if (status(error) === 401) {
        unauthorized();
        return;
      }
      if (error.problem.code?.endsWith("antiforgery_invalid"))
        discardAntiforgeryToken();
      setMessage(
        status(error) === 403
          ? "Tu usuario no tiene autorización para corregir precios aplicados en pedidos."
          : "El Estado del Pedido cambió o la corrección fue rechazada. Se consultará el Estado vigente.",
      );
    } finally {
      sending.current = false;
    }
    setIntent(null);
    await refresh();
  }
  async function begin() {
    if (
      locked.current ||
      !canAct ||
      isTerminal ||
      !confirmation?.isCorrectionAvailable
    )
      return;
    lock(true);
    confirmationOpen.current = false;
    setPhase("sending");
    setMessage(null);
    try {
      const token = await getAntiforgeryToken();
      if (!mounted.current) return;
      const exact = createAppliedPriceIntent(confirmation, token);
      setIntent(exact);
      await execute(exact);
    } catch (error) {
      if (!mounted.current) return;
      if (status(error) === 401) {
        unauthorized();
        return;
      }
      setPhase("idle");
      lock(false);
      setMessage("No se pudo obtener la protección de la solicitud.");
    }
  }
  const disabled = !canAct || phase !== "idle";
  return (
    <section
      className="order-ending"
      aria-label="Corrección de precio aplicado"
    >
      <ActiveOrderFreshnessSubscription
        orderId={rows.length > 0 || confirmation !== null ? orderId : null}
        invalidate={invalidateEvaluations}
      />
      <h3>Precios aplicados por contenido</h3>
      <button type="button" disabled={disabled} onClick={() => void read()}>
        Consultar precios aplicados
      </button>
      {phase === "reading" && <p role="status">Consultando precios…</p>}
      {rows.map(({ content, evaluation }) => (
        <article
          key={`${content.incorporationId}:${content.contentOrdinal}`}
          aria-label={`Incorporación ${content.incorporationOrdinal}, contenido ${content.contentOrdinal}`}
        >
          <h4>
            {content.productOperationalName ?? "Nombre histórico no disponible"}{" "}
            — Incorporación {content.incorporationOrdinal}, contenido{" "}
            {content.contentOrdinal}
          </h4>
          <p>{content.instruction ?? "Sin instrucción"}</p>
          <p>Entregado: {content.deliveredQuantity}</p>
          <dl>
            <dt>Precio confirmado original</dt>
            <dd>{evaluation.appliedPrice}</dd>
            <dt>Precio aplicado efectivo actual</dt>
            <dd>{evaluation.effectiveAppliedPrice}</dd>
            <dt>Precio vigente en catálogo</dt>
            <dd>{evaluation.currentCatalogPrice ?? "No disponible"}</dd>
          </dl>
          {evaluation.blockers.map((code) => (
            <p key={code}>
              {priceBlockers[code] ??
                "La corrección no está disponible. Actualizá el Estado."}
            </p>
          ))}
          {evaluation.isCorrectionAvailable && !isTerminal && (
            <button
              type="button"
              disabled={disabled}
              onClick={() => {
                confirmationOpen.current = true;
                evaluationCoordinator.current.cancel();
                setConfirmation(evaluation);
              }}
            >
              Corregir precio aplicado
            </button>
          )}
          {confirmation === evaluation &&
            evaluation.isCorrectionAvailable &&
            !isTerminal && (
              <section aria-label="Confirmar corrección de precio">
                <p>
                  Precio aplicado actual: {evaluation.effectiveAppliedPrice} →
                  Precio vigente corregido: {evaluation.currentCatalogPrice}
                </p>
                <p>
                  Se aplicará sólo a este contenido confirmado. El precio
                  confirmado original permanece en su Historia. Catálogo y los
                  demás contenidos y pedidos permanecen sin cambios.
                </p>
                <p>
                  Si hay cantidad efectivamente entregada, puede cambiar el
                  Importe funcional antes de la Liquidación. La cantidad
                  entregada y la Composición pendiente se conservan.
                </p>
                <p>
                  El backend revalidará el precio vigente de Catálogo al aplicar
                  la corrección.
                </p>
                <button
                  type="button"
                  disabled={disabled}
                  onClick={() => void begin()}
                >
                  Aplicar precio {evaluation.currentCatalogPrice}
                </button>
                <button
                  type="button"
                  disabled={phase !== "idle"}
                  onClick={() => {
                    confirmationOpen.current = false;
                    setConfirmation(null);
                    invalidateEvaluations();
                  }}
                >
                  Volver sin corregir
                </button>
              </section>
            )}
        </article>
      ))}
      {message && <p role="alert">{message}</p>}
      {phase === "sending" && (
        <p role="status">Enviando corrección de precio…</p>
      )}
      {phase === "uncertain" && intent && (
        <button type="button" onClick={() => void execute(intent)}>
          Reintentar misma corrección de precio
        </button>
      )}
      {phase === "refreshing" && (
        <button type="button" onClick={() => void refresh()}>
          Actualizar precios y Pedido
        </button>
      )}
    </section>
  );
}
