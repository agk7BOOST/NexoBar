import { useCallback, useEffect, useRef, useState } from "react";
import {
  listPreparationDestinations,
  SessionProblemError,
  type PreparationDestination,
} from "../identity/sessionClient.ts";
import {
  listPreparationWork,
  PreparationProblemError,
  type PreparationWork,
} from "./preparationClient.ts";

interface PreparationPanelProps {
  onUnauthorized: () => void;
}

const forbiddenMessage =
  "Esta Identity no tiene autorización para esa preparación.";

export function PreparationPanel({ onUnauthorized }: PreparationPanelProps) {
  const [destinations, setDestinations] = useState<PreparationDestination[]>(
    [],
  );
  const [selectedId, setSelectedId] = useState("");
  const [work, setWork] = useState<PreparationWork[]>([]);
  const [isLoadingDestinations, setIsLoadingDestinations] = useState(true);
  const [isLoadingWork, setIsLoadingWork] = useState(false);
  const [message, setMessage] = useState<string | null>(null);
  const workRequestSequence = useRef(0);

  const loadWork = useCallback(
    async (destinationId: string) => {
      const sequence = ++workRequestSequence.current;
      setIsLoadingWork(true);
      setMessage(null);
      setWork([]);
      try {
        const loaded = await listPreparationWork(destinationId);
        if (sequence !== workRequestSequence.current) return;
        setWork(loaded);
      } catch (error) {
        if (sequence !== workRequestSequence.current) return;
        if (error instanceof PreparationProblemError && error.status === 401) {
          onUnauthorized();
          return;
        }
        setMessage(
          error instanceof PreparationProblemError && error.status === 403
            ? forbiddenMessage
            : "No se pudo consultar el trabajo de preparación.",
        );
      } finally {
        if (sequence === workRequestSequence.current) setIsLoadingWork(false);
      }
    },
    [onUnauthorized],
  );

  useEffect(() => {
    let isCurrent = true;
    void listPreparationDestinations().then(
      (loaded) => {
        if (!isCurrent) return;
        setDestinations(loaded);
        const automaticDestination =
          loaded.length === 1 ? loaded[0].preparationResponsibilityId : "";
        setSelectedId(automaticDestination);
        setIsLoadingDestinations(false);
        if (automaticDestination !== "") void loadWork(automaticDestination);
      },
      (error: unknown) => {
        if (!isCurrent) return;
        setIsLoadingDestinations(false);
        if (error instanceof SessionProblemError && error.status === 401) {
          onUnauthorized();
          return;
        }
        setMessage(
          error instanceof SessionProblemError && error.status === 403
            ? forbiddenMessage
            : "No se pudieron cargar los destinos de preparación.",
        );
      },
    );
    return () => {
      isCurrent = false;
    };
  }, [loadWork, onUnauthorized]);

  return (
    <section className="panel" aria-labelledby="preparation-heading">
      <div className="section-heading">
        <h2 id="preparation-heading">Preparación</h2>
        <span className="ephemeral-label">Solo lectura</span>
      </div>

      {isLoadingDestinations && <p>Cargando destinos…</p>}
      {!isLoadingDestinations && destinations.length === 0 && !message && (
        <p>No hay destinos de preparación habilitados para esta Identity.</p>
      )}
      {destinations.length > 1 && (
        <label className="preparation-destination-selector">
          Destino de preparación
          <select
            value={selectedId}
            onChange={(event) => {
              const destinationId = event.target.value;
              setSelectedId(destinationId);
              if (destinationId === "") {
                workRequestSequence.current++;
                setWork([]);
                setMessage(null);
                setIsLoadingWork(false);
              } else {
                void loadWork(destinationId);
              }
            }}
          >
            <option value="">Seleccionar destino</option>
            {destinations.map((destination) => (
              <option
                key={destination.preparationResponsibilityId}
                value={destination.preparationResponsibilityId}
              >
                {destination.operationalName}
              </option>
            ))}
          </select>
        </label>
      )}
      {destinations.length === 1 && (
        <p>
          Destino: <strong>{destinations[0].operationalName}</strong>
        </p>
      )}
      {message && (
        <p className="notice notice--functional-error" role="alert">
          {message}
        </p>
      )}
      {isLoadingWork && <p>Cargando trabajo…</p>}
      {!isLoadingWork && selectedId !== "" && !message && work.length === 0 && (
        <p>No hay trabajo pendiente para este destino.</p>
      )}
      {work.length > 0 && (
        <div className="table-scroll">
          <table>
            <thead>
              <tr>
                <th>Producto</th>
                <th>Contexto / referencia</th>
                <th>Cantidades</th>
                <th>Instrucción</th>
              </tr>
            </thead>
            <tbody>
              {work.map((item) => (
                <tr key={item.workId}>
                  <td>{item.productOperationalName}</td>
                  <td>
                    {item.context}
                    <br />
                    <span className="technical-reference">
                      {item.operationalReference}
                    </span>
                  </td>
                  <td>
                    Total {item.totalQuantity}; pendiente {item.pendingQuantity}
                    ; en preparación {item.inPreparationQuantity}; listo{" "}
                    {item.readyQuantity}
                  </td>
                  <td className="confirmed-instruction">
                    {item.instruction ?? "Sin instrucción"}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </section>
  );
}
