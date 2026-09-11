import { useEffect, useRef, useState } from "react";
import {
  discardAntiforgeryToken,
  getAntiforgeryToken,
  SessionProblemError,
} from "../identity/sessionClient.ts";
import { OrderOperationsProblemError } from "./orderOperationsClient.ts";
import {
  cancellationBlockers,
  createCompleteCancellationIntent,
  sendCompleteCancellationIntent,
  type CompleteCancellationEvaluation,
  type CompleteCancellationIntent,
} from "./completeCancellationClient.ts";

interface Props {
  evaluation: CompleteCancellationEvaluation | null;
  evaluationError: string | null;
  canAct: boolean;
  onRefresh: () => Promise<boolean>;
  onUnauthorized: () => void;
  onBusyChange: (busy: boolean) => void;
}

export function CompleteCancellation({
  evaluation,
  evaluationError,
  canAct,
  onRefresh,
  onUnauthorized,
  onBusyChange,
}: Props) {
  const [confirmation, setConfirmation] =
    useState<CompleteCancellationEvaluation | null>(null);
  const confirming = confirmation !== null && confirmation === evaluation;
  const [phase, setPhase] = useState<
    "idle" | "sending" | "uncertain" | "refreshing"
  >("idle");
  const [intent, setIntent] = useState<CompleteCancellationIntent | null>(null);
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
    setConfirmation(null);
    setPhase("idle");
    lock(false);
    onUnauthorized();
  }
  async function refresh() {
    setPhase("refreshing");
    setConfirmation(null);
    const refreshed = await onRefresh();
    if (!mounted.current) return;
    if (refreshed) {
      setPhase("idle");
      lock(false);
    } else
      setMessage(
        "No se pudo actualizar el Pedido. Actualizá su Estado antes de continuar.",
      );
  }
  async function execute(exact: CompleteCancellationIntent) {
    if (sending.current) return;
    sending.current = true;
    setPhase("sending");
    setMessage(null);
    try {
      const result = await sendCompleteCancellationIntent(exact);
      if (!mounted.current) return;
      setMessage(
        result.pendingCompositionDiscarded
          ? "Cancelación completa registrada. La Composición pendiente fue descartada."
          : "Cancelación completa registrada. Consultando el Estado vigente.",
      );
    } catch (error) {
      if (!mounted.current) return;
      sending.current = false;
      if (!(error instanceof OrderOperationsProblemError)) {
        setPhase("uncertain");
        setMessage(
          "Resultado incierto. Reintentá la misma cancelación completa para resolverlo.",
        );
        return;
      }
      if (error.problem.status === 401) {
        unauthorized();
        return;
      }
      const code = error.problem.code?.split(".").at(-1) ?? "";
      if (code === "antiforgery_invalid") discardAntiforgeryToken();
      setIntent(null);
      setMessage(
        error.problem.status === 403
          ? code === "operational_intervention_required"
            ? "Esta Identity necesita además autorización de Intervención operacional para cancelar este pedido completo."
            : "Esta Identity no tiene autorización para cancelar el pedido completo."
          : (cancellationBlockers[code] ??
              "El Estado del Pedido cambió o la cancelación fue rechazada. Se consultará el Estado vigente."),
      );
      await refresh();
      return;
    }
    sending.current = false;
    setIntent(null);
    await refresh();
  }
  async function begin() {
    if (busy.current || !canAct || !confirming || !evaluation?.isEligible)
      return;
    lock(true);
    setPhase("sending");
    setMessage(null);
    try {
      const token = await getAntiforgeryToken();
      if (!mounted.current) return;
      const exact = createCompleteCancellationIntent(evaluation.orderId, token);
      setIntent(exact);
      await execute(exact);
    } catch (error) {
      if (!mounted.current) return;
      if (error instanceof SessionProblemError && error.status === 401) {
        unauthorized();
        return;
      }
      setMessage("No se pudo obtener la protección de la solicitud.");
      setPhase("idle");
      lock(false);
    }
  }

  const disabled = !canAct || phase !== "idle";
  return (
    <section
      aria-label="Cancelación completa excepcional"
      className="order-ending"
    >
      <h3>Cancelación completa excepcional</h3>
      {evaluationError && <p role="alert">{evaluationError}</p>}
      {evaluation && (
        <>
          {evaluation.isCompletelyCancelled ? (
            <div role="status">
              <strong>Pedido completamente cancelado</strong>
              <p>
                El pedido terminó excepcionalmente. La consulta histórica
                permanece disponible.
              </p>
              {evaluation.cancelledAt && (
                <time dateTime={evaluation.cancelledAt}>
                  {evaluation.cancelledAt}
                </time>
              )}
            </div>
          ) : (
            evaluation.isTerminal && (
              <p>
                El pedido ya es terminal. La cancelación completa no está
                disponible.
              </p>
            )
          )}
          {evaluation.blockers.length > 0 && (
            <ul>
              {evaluation.blockers.map((code) => (
                <li key={code}>
                  {cancellationBlockers[code] ??
                    `Cancelación completa no disponible (${code}). Actualizá el Estado.`}
                </li>
              ))}
            </ul>
          )}
          {evaluation.hasEffectiveDelivery &&
            !evaluation.blockers.includes("effective_delivery") && (
              <p>{cancellationBlockers.effective_delivery}</p>
            )}
          {!evaluation.isTerminal && (
            <>
              <p>
                Requiere autorización de Operaciones de pedidos y cierre básico
                (OrderOperationsAndBasicClosure).
              </p>
              {evaluation.requiresOperationalIntervention === true && (
                <p>
                  La evaluación requiere además Intervención operacional
                  (OperationalIntervention) para esta misma Identity.
                </p>
              )}
              <button
                type="button"
                className="secondary-button"
                disabled={disabled || !evaluation.isEligible}
                onClick={() => setConfirmation(evaluation)}
              >
                Cancelar pedido completo
              </button>
              {confirming && evaluation.isEligible && (
                <section aria-label="Confirmar cancelación completa">
                  <p>
                    Toda la obligación de cumplimiento actual restante dejará de
                    ser requerida. El pedido terminará excepcionalmente. No se
                    creará Liquidación ni Cierre.
                  </p>
                  <p>
                    Obligación restante informada:{" "}
                    {evaluation.remainingFulfillmentQuantity ?? "No disponible"}
                    .
                  </p>
                  {evaluation.requiresOperationalIntervention === true && (
                    <p>
                      Hay trabajo real ya iniciado o listo. Su obligación cesará
                      y el trabajo realizado permanecerá registrado en la
                      Historia.
                    </p>
                  )}
                  {evaluation.hasPendingComposition && (
                    <p>
                      La Composición pendiente será descartada como parte de
                      esta cancelación completa.
                    </p>
                  )}
                  {evaluation.consequences.length > 0 && (
                    <table aria-label="Consecuencias informadas">
                      <thead>
                        <tr>
                          <th>Contenido</th>
                          <th>Directo o pendiente</th>
                          <th>En preparación</th>
                          <th>Listo</th>
                          <th>Obligación resultante</th>
                        </tr>
                      </thead>
                      <tbody>
                        {evaluation.consequences.map((c) => (
                          <tr key={`${c.incorporationId}:${c.contentOrdinal}`}>
                            <th>
                              {c.incorporationId} / {c.contentOrdinal}
                            </th>
                            <td>{c.directOrPendingQuantity}</td>
                            <td>{c.inPreparationQuantity}</td>
                            <td>{c.readyQuantity}</td>
                            <td>{c.resultingFulfillmentQuantity}</td>
                          </tr>
                        ))}
                      </tbody>
                    </table>
                  )}
                  <button
                    type="button"
                    disabled={disabled}
                    onClick={() => void begin()}
                  >
                    Confirmar cancelación completa
                  </button>
                  <button
                    type="button"
                    disabled={phase !== "idle"}
                    onClick={() => setConfirmation(null)}
                  >
                    Volver sin cancelar
                  </button>
                </section>
              )}
            </>
          )}
        </>
      )}
      {message && <p role="alert">{message}</p>}
      {phase === "sending" && (
        <p role="status">Enviando cancelación completa…</p>
      )}
      {phase === "uncertain" && intent && (
        <div>
          <p>Pedido: {intent.orderId}. Se conserva la misma intención.</p>
          <button type="button" onClick={() => void execute(intent)}>
            Reintentar misma cancelación completa
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
