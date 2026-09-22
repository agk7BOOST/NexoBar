import { useCallback, useEffect, useRef, useState } from "react";
import {
  createProductAvailabilityIntent,
  listAvailabilityAdministrationProducts,
  sendProductAvailabilityIntent,
  AvailabilityNetworkError,
  AvailabilityProblemError,
  type AvailabilityAdministrationProduct,
  type ProductAvailabilityIntent,
} from "./availabilityClient.ts";
import {
  discardAntiforgeryToken,
  getAntiforgeryToken,
  SessionProblemError,
} from "../identity/sessionClient.ts";

type Phase = "loading" | "idle" | "sending" | "uncertain";

interface Props {
  onUnauthorized: () => void;
  onForbidden: () => void;
}

function problemMessage(code: string | undefined): string | null {
  switch (code) {
    case "catalog.product.availability_concurrency_conflict":
      return "La disponibilidad cambió en otra operación. Se actualizará la lista.";
    case "catalog.product.not_current":
      return "El Product ya no es vigente. Se actualizará la lista.";
    case "catalog.product.not_found":
      return "El Product ya no existe. Se actualizará la lista.";
    default:
      return null;
  }
}

export function ProductAvailabilityInterventionPanel({
  onUnauthorized,
  onForbidden,
}: Props) {
  const [products, setProducts] = useState<AvailabilityAdministrationProduct[]>([]);
  const [phase, setPhase] = useState<Phase>("loading");
  const [message, setMessage] = useState<string | null>(null);
  const [uncertainIntent, setUncertainIntent] =
    useState<ProductAvailabilityIntent | null>(null);
  const mounted = useRef(true);

  useEffect(() => {
    mounted.current = true;
    return () => {
      mounted.current = false;
    };
  }, []);

  const reload = useCallback(async () => {
    setPhase("loading");
    try {
      const current = await listAvailabilityAdministrationProducts();
      if (!mounted.current) return;
      setProducts(current);
      setPhase("idle");
    } catch (error) {
      if (!mounted.current) return;
      if (error instanceof AvailabilityProblemError && error.problem.status === 401) {
        discardAntiforgeryToken();
        onUnauthorized();
        return;
      }
      if (error instanceof AvailabilityProblemError && error.problem.status === 403) {
        onForbidden();
        return;
      }
      setMessage("No se pudo consultar la disponibilidad de los Products.");
      setPhase("idle");
    }
  }, [onForbidden, onUnauthorized]);

  useEffect(() => {
    void reload();
  }, [reload]);

  async function submit(intent: ProductAvailabilityIntent) {
    setPhase("sending");
    setMessage(null);
    try {
      await sendProductAvailabilityIntent(intent);
      if (!mounted.current) return;
      setUncertainIntent(null);
      setMessage("Disponibilidad actualizada. Consultando el estado vigente.");
      await reload();
    } catch (error) {
      if (!mounted.current) return;
      if (error instanceof AvailabilityNetworkError) {
        setUncertainIntent(intent);
        setMessage("Resultado incierto. Reintentá exactamente la misma intención.");
        setPhase("uncertain");
        return;
      }
      if (!(error instanceof AvailabilityProblemError)) {
        setUncertainIntent(intent);
        setMessage("Resultado incierto. Reintentá exactamente la misma intención.");
        setPhase("uncertain");
        return;
      }
      if (error.problem.status === 401) {
        discardAntiforgeryToken();
        onUnauthorized();
        return;
      }
      if (error.problem.status === 403) {
        onForbidden();
        return;
      }
      if (error.problem.code?.endsWith("antiforgery_invalid")) {
        discardAntiforgeryToken();
      }
      const notice = problemMessage(error.problem.code);
      setUncertainIntent(null);
      setMessage(notice ?? "La disponibilidad no pudo actualizarse. Se consultará el estado vigente.");
      await reload();
    }
  }

  async function change(product: AvailabilityAdministrationProduct) {
    if (phase !== "idle" || uncertainIntent !== null) return;
    let antiforgeryToken: string;
    try {
      antiforgeryToken = await getAntiforgeryToken();
    } catch (error) {
      if (error instanceof SessionProblemError && error.status === 401) {
        discardAntiforgeryToken();
        onUnauthorized();
        return;
      }
      setMessage("No se pudo obtener la protección de la solicitud. Intentá nuevamente.");
      return;
    }
    const intent = createProductAvailabilityIntent(
      product.id,
      {
        expectedCurrentAvailability: product.isAvailable,
        newAvailability: !product.isAvailable,
      },
      crypto.randomUUID(),
      antiforgeryToken,
    );
    await submit(intent);
  }

  return (
    <section className="panel product-availability-intervention" aria-label="Intervención de disponibilidad de Products">
      <h2>Intervención de disponibilidad de Products</h2>
      <p>Esta superficie cambia únicamente la disponibilidad de Products vigentes.</p>
      {message && <p role="status">{message}</p>}
      {phase === "loading" && <p role="status">Cargando Products vigentes…</p>}
      {uncertainIntent && (
        <div className="uncertain-intention" role="region" aria-label="Cambio de disponibilidad con resultado no confirmado">
          <h3>Cambio de disponibilidad pendiente de confirmación</h3>
          <p>Product: {uncertainIntent.productId}</p>
          <p>La próxima tentativa conserva exactamente el Product, el estado esperado, el nuevo estado y la misma key.</p>
          <button type="button" onClick={() => void submit(uncertainIntent)} disabled={phase === "sending"}>
            Reintentar misma intención
          </button>
        </div>
      )}
      {phase !== "loading" && products.length === 0 && <p>No hay Products vigentes.</p>}
      {phase !== "loading" && products.length > 0 && (
        <ul aria-label="Products vigentes para intervención de disponibilidad">
          {products.map((product) => (
            <li key={product.id}>
              <span aria-label={`Product ${product.operationalName}`}>
                {product.operationalName}
              </span>{" "}
              <span aria-label={`Disponibilidad de ${product.operationalName}`}>
                {product.isAvailable ? "Disponible" : "No disponible"}
              </span>{" "}
              <button
                type="button"
                onClick={() => void change(product)}
                disabled={phase !== "idle" || uncertainIntent !== null}
                aria-label={`${product.isAvailable ? "Marcar no disponible" : "Marcar disponible"} ${product.operationalName}`}
              >
                {product.isAvailable ? "Marcar no disponible" : "Marcar disponible"}
              </button>
            </li>
          ))}
        </ul>
      )}
    </section>
  );
}
