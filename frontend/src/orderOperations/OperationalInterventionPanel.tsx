import { useEffect, useRef, useState, type FormEvent } from "react";
import { discardAntiforgeryToken, getAntiforgeryToken, SessionProblemError } from "../identity/sessionClient.ts";
import { OrderOperationsProblemError } from "./orderOperationsClient.ts";
import {
  createInterventionIntent, getInterventionTarget, sendInterventionIntent,
  type InterventionIntent, type InterventionLookup, type InterventionStage, type InterventionTarget,
} from "./operationalInterventionClient.ts";

type Phase = "idle" | "reading" | "preparing" | "sending" | "uncertain" | "refresh-required";
const labels = {
  "in-preparation": "Cancelar cantidad ya iniciada",
  ready: "Cancelar cantidad ya lista",
};
const uuid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

function quantities(target: InterventionTarget) {
  return [
    ["Confirmada (Q)", target.confirmedQuantity],
    ["Retirada por corrección de contenido (R)", target.removedByCorrectionQuantity],
    ["Cancelada (C)", target.cancelledQuantity],
    ["Obligación de cumplimiento (F)", target.fulfillmentQuantity],
    ["Pendiente (P)", target.pendingQuantity],
    ["En preparación (I)", target.inPreparationQuantity],
    ["Lista (Y)", target.readyQuantity],
    ["Total vigente (T)", target.totalQuantity],
    ["Entregada (D)", target.deliveredQuantity],
  ] as const;
}

export function OperationalInterventionPanel({ onUnauthorized, isOrderBlocked }: { onUnauthorized: () => void; isOrderBlocked?: (reference: string) => boolean }) {
  const [incorporation, setIncorporation] = useState("");
  const [ordinal, setOrdinal] = useState("");
  const [lookup, setLookup] = useState<InterventionLookup | null>(null);
  const [target, setTarget] = useState<InterventionTarget | null>(null);
  const [phase, setPhase] = useState<Phase>("idle");
  const [message, setMessage] = useState<string | null>(null);
  const [intent, setIntent] = useState<InterventionIntent | null>(null);
  const [amounts, setAmounts] = useState({ "in-preparation": "1", ready: "1" });
  const busy = useRef(false);
  const sending = useRef(false);
  const mounted = useRef(true);
  useEffect(() => {
    mounted.current = true;
    return () => { mounted.current = false; };
  }, []);

  function unauthorized() {
    discardAntiforgeryToken();
    setTarget(null);
    setIntent(null);
    setPhase("idle");
    busy.current = false;
    onUnauthorized();
  }

  async function load(exact: InterventionLookup, required: boolean, notice: string | null = null) {
    busy.current = true;
    setPhase("reading");
    setMessage(notice);
    try {
      const current = await getInterventionTarget(exact);
      if (!mounted.current) return;
      setTarget(current);
      setAmounts({ "in-preparation": "1", ready: "1" });
      setPhase("idle");
      busy.current = false;
    } catch (error) {
      if (!mounted.current) return;
      if (error instanceof OrderOperationsProblemError) {
        if (error.problem.status === 401) { unauthorized(); return; }
        if (error.problem.status === 403 || error.problem.status === 404) {
          setTarget(null);
          setMessage(error.problem.status === 403
            ? "Esta Identity no tiene autorización de Intervención operacional."
            : "No se encontró ese contenido como destino de intervención.");
          setPhase("idle");
          busy.current = false;
          return;
        }
      }
      setMessage("No se pudo actualizar el contenido. Actualizá su Estado antes de intervenir.");
      setPhase(required ? "refresh-required" : "idle");
      busy.current = required;
    }
  }

  function find(event: FormEvent) {
    event.preventDefault();
    if (busy.current) return;
    const id = incorporation.trim().toLowerCase();
    const contentOrdinal = Number(ordinal);
    if (!uuid.test(id) || id === "00000000-0000-0000-0000-000000000000" ||
        !Number.isInteger(contentOrdinal) || contentOrdinal <= 0 || contentOrdinal > 2147483647) {
      setMessage("Ingresá el identificador UUID de Incorporación y un ordinal de Content entero positivo.");
      return;
    }
    const exact = { incorporationId: id, contentOrdinal };
    setLookup(exact);
    setTarget(null);
    void load(exact, false);
  }

  async function execute(exact: InterventionIntent) {
    if (sending.current) return;
    sending.current = true;
    setPhase("sending");
    setMessage(null);
    try {
      await sendInterventionIntent(exact);
    } catch (error) {
      if (!mounted.current) return;
      sending.current = false;
      if (!(error instanceof OrderOperationsProblemError)) {
        setPhase("uncertain");
        setMessage("Resultado incierto. Reintentá esta misma intervención para resolverlo.");
        return;
      }
      if (error.problem.status === 401) { unauthorized(); return; }
      setIntent(null);
      if (error.problem.status === 403) {
        setTarget(null);
        setMessage("Esta Identity no tiene autorización de Intervención operacional.");
        setPhase("idle");
        busy.current = false;
        return;
      }
      if (error.problem.code?.endsWith("antiforgery_invalid")) discardAntiforgeryToken();
      const notice = error.problem.code === "order_operations.order.frozen"
        ? "El Pedido está congelado por su Liquidación. Se consultará el Estado vigente."
        : error.problem.status === 409
          ? "El Estado cambió o la intención fue rechazada. Se consultará el Estado vigente."
          : "La intervención fue rechazada. Se consultará el Estado vigente antes de continuar.";
      await load(exact, true, notice);
      return;
    }
    if (!mounted.current) return;
    sending.current = false;
    setIntent(null);
    await load(exact, true, "Intervención registrada. Consultando la obligación vigente.");
  }

  async function begin(stage: InterventionStage) {
    if (busy.current || target === null || target.isFrozen || isOrderBlocked?.(target.orderId)) return;
    const quantity = Number(amounts[stage]);
    const maximum = stage === "ready" ? target.readyQuantity - target.deliveredQuantity : target.inPreparationQuantity;
    if (!Number.isInteger(quantity) || quantity <= 0 || quantity > maximum) return;
    busy.current = true;
    setPhase("preparing");
    setMessage(null);
    let token: string;
    try { token = await getAntiforgeryToken(); }
    catch (error) {
      if (!mounted.current) return;
      if (error instanceof SessionProblemError && error.status === 401) { unauthorized(); return; }
      setMessage("No se pudo obtener la protección de la solicitud. Intentá nuevamente.");
      busy.current = false;
      setPhase("idle");
      return;
    }
    if (!mounted.current) return;
    const exact = createInterventionIntent(target, stage, quantity, token);
    setIntent(exact);
    await execute(exact);
  }

  const locked = phase !== "idle";
  const mutationLocked = locked || (target !== null && isOrderBlocked?.(target.orderId) === true);
  return (
    <section className="panel operational-intervention" aria-label="Intervención operacional">
      <h2>Intervención operacional</h2>
      <p>El trabajo de preparación se realizó. Esta intervención cancela una cantidad que posteriormente dejó de ser requerida.</p>
      <p>Consultá el contenido exacto para intervenir con autorización de Intervención operacional.</p>
      <form onSubmit={find} aria-label="Consultar contenido para intervención">
        <label htmlFor="intervention-incorporation">Identificador de Incorporación</label>
        <label htmlFor="intervention-ordinal">Ordinal de Content</label>
        <input id="intervention-incorporation" value={incorporation} onChange={e => setIncorporation(e.target.value)} disabled={locked} required />
        <input id="intervention-ordinal" type="number" min="1" max="2147483647" step="1" value={ordinal} onChange={e => setOrdinal(e.target.value)} disabled={locked} required />
        <button type="submit" disabled={locked}>Consultar para intervenir</button>
      </form>
      {lookup && <button type="button" className="secondary-button" disabled={phase !== "idle" && phase !== "refresh-required"}
        onClick={() => void load(lookup, true)}>Actualizar contenido</button>}
      {message && <p role="status">{message}</p>}
      {phase === "reading" && <p role="status">Consultando Estado autoritativo…</p>}
      {target && <>
        <h3>{target.productOperationalName}</h3>
        <p>{target.instruction ?? "Sin instrucción"}</p>
        <dl className="confirmation-summary" aria-label="Identidad del contenido">
          <div><dt>Pedido</dt><dd>{target.orderId}</dd></div>
          <div><dt>Incorporación</dt><dd>{target.incorporationId}</dd></div>
          <div><dt>Content</dt><dd>{target.contentOrdinal}</dd></div>
          <div><dt>Work</dt><dd>{target.workId}</dd></div>
        </dl>
        <p>Pendiente, en preparación, lista y total describen la obligación vigente.</p>
        <dl className="confirmation-summary" aria-label="Obligación vigente">
          {quantities(target).map(([label, value]) => <div key={label}><dt>{label}</dt><dd>{value}</dd></div>)}
        </dl>
        {target.isFrozen ? <p role="status">Pedido congelado por Liquidación. No hay nuevas intervenciones disponibles.</p> :
          (["in-preparation", "ready"] as const).map(stage => {
            const maximum = stage === "ready" ? target.readyQuantity - target.deliveredQuantity : target.inPreparationQuantity;
            if (maximum <= 0) return null;
            const quantity = Number(amounts[stage]);
            const valid = Number.isInteger(quantity) && quantity > 0 && quantity <= maximum;
            const preview = { ...target, cancelledQuantity: target.cancelledQuantity + quantity,
              totalQuantity: target.totalQuantity - quantity, fulfillmentQuantity: target.fulfillmentQuantity - quantity,
              inPreparationQuantity: target.inPreparationQuantity - (stage === "in-preparation" ? quantity : 0),
              readyQuantity: target.readyQuantity - (stage === "ready" ? quantity : 0) };
            return <section key={stage} aria-label={labels[stage]}>
              <h3>{labels[stage]}</h3>
              <p>{stage === "ready" ? "Esta cantidad realmente llegó a lista. La cantidad entregada permanece sin cambios." : "Esta preparación realmente comenzó. La cantidad pendiente permanece sin cambios."}</p>
              <label htmlFor={`intervention-${stage}`}>Cantidad ya {stage === "ready" ? "lista" : "iniciada"} a cancelar</label>
              <input id={`intervention-${stage}`} type="number" min="1" max={maximum} step="1" value={amounts[stage]}
                disabled={mutationLocked} onChange={e => setAmounts(current => ({ ...current, [stage]: e.target.value }))} />
              <p>Máximo: {maximum}. Ingresá una cantidad entera positiva.</p>
              {valid && <table aria-label={`Vista previa: ${labels[stage]}`}>
                <caption>Vista previa de la obligación vigente</caption>
                <thead><tr><th scope="col">Cantidad</th><th scope="col">Actual</th><th scope="col">Después</th></tr></thead>
                <tbody>{quantities(target).map(([label, value], i) => <tr key={label}><th scope="row">{label}</th><td>{value}</td><td>{quantities(preview)[i][1]}</td></tr>)}</tbody>
              </table>}
              <button type="button" disabled={mutationLocked || !valid} onClick={() => void begin(stage)}>{labels[stage]}</button>
            </section>;
          })}
        {!target.isFrozen && target.inPreparationQuantity === 0 && target.readyQuantity === target.deliveredQuantity &&
          <p>No hay cantidad elegible para intervenir.</p>}
      </>}
      {intent && phase === "uncertain" && <div className="uncertain-intention" role="alert">
        <p>{labels[intent.stage]}: {intent.quantity}. La intervención sigue pendiente de resolución.</p>
        <p>Work: {intent.workId}. Content: {intent.incorporationId} / {intent.contentOrdinal}.</p>
        <p>Reintentá exactamente esta intervención antes de iniciar otra.</p>
        <button type="button" onClick={() => void execute(intent)}>Reintentar misma intervención</button>
      </div>}
    </section>
  );
}
