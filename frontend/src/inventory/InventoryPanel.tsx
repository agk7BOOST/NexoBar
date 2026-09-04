import {
  type FormEvent,
  useCallback,
  useEffect,
  useRef,
  useState,
} from "react";
import {
  discardAntiforgeryToken,
  getAntiforgeryToken,
  SessionProblemError,
} from "../identity/sessionClient.ts";
import { InventoryHistory } from "./InventoryHistory.tsx";
import { InventoryItemOperations } from "./InventoryItemOperations.tsx";
import {
  createInventoryItem,
  InventoryProblemError,
  listInventoryConfigurationItems,
  listInventoryOperationalItems,
  type CreateInventoryItemRequest,
  type InventoryConfigurationItem,
  type InventoryOperationalItem,
} from "./inventoryClient.ts";

interface InventoryPanelProps {
  onUnauthorized: () => void;
}

type ScopeState<T> =
  | { status: "loading" }
  | { status: "ready"; items: T[] }
  | { status: "forbidden" }
  | { status: "error" };

type CreateIntentPhase = "submitting" | "uncertain";

interface CreateIntent {
  phase: CreateIntentPhase;
  request: CreateInventoryItemRequest;
  idempotencyKey: string;
}

type Notice =
  | { kind: "success"; message: string }
  | { kind: "functional-error"; message: string }
  | { kind: "uncertain"; message: string };

const configurationForbiddenMessage =
  "Esta Identity no tiene autorización para configurar Inventario.";
const operationForbiddenMessage =
  "Esta Identity no tiene autorización para operar Inventario.";

function creationFailureMessage(error: InventoryProblemError): string {
  switch (error.problem.code) {
    case "inventory.item.operational_name_invalid":
      return "Ingresá un nombre operacional válido.";
    case "inventory.item.operational_unit_invalid":
      return "Ingresá una unidad operacional válida.";
    case "inventory.item.operational_name_conflict":
      return "Ya existe un elemento de Inventario con ese nombre.";
    case "inventory.item.idempotency_key_conflict":
      return "La identidad de esta creación ya fue usada para otra intención.";
    default:
      return "No se pudo crear el elemento. Revisá los datos e intentá nuevamente.";
  }
}

export function InventoryPanel({ onUnauthorized }: InventoryPanelProps) {
  const [configuration, setConfiguration] = useState<
    ScopeState<InventoryConfigurationItem>
  >({ status: "loading" });
  const [operation, setOperation] = useState<
    ScopeState<InventoryOperationalItem>
  >({ status: "loading" });
  const [operationalName, setOperationalName] = useState("");
  const [operationalUnit, setOperationalUnit] = useState("");
  const [createIntent, setCreateIntentState] = useState<CreateIntent | null>(
    null,
  );
  const [createNotice, setCreateNotice] = useState<Notice | null>(null);
  const [historyItem, setHistoryItem] =
    useState<InventoryOperationalItem | null>(null);
  const [historyRefreshRevision, setHistoryRefreshRevision] = useState(0);
  const configurationSequence = useRef(0);
  const operationSequence = useRef(0);
  const createIntentRef = useRef<CreateIntent | null>(null);
  const operationAuthorizedRef = useRef(false);
  const sessionEndedRef = useRef(false);
  const cancelReadRequests = useCallback(() => {
    configurationSequence.current++;
    operationSequence.current++;
  }, []);

  const setCreateIntent = useCallback((intent: CreateIntent | null) => {
    createIntentRef.current = intent;
    setCreateIntentState(intent);
  }, []);

  const handleUnauthorized = useCallback(() => {
    if (sessionEndedRef.current) return;
    sessionEndedRef.current = true;
    cancelReadRequests();
    discardAntiforgeryToken();
    setCreateIntent(null);
    setHistoryItem(null);
    onUnauthorized();
  }, [cancelReadRequests, onUnauthorized, setCreateIntent]);

  const refreshConfiguration = useCallback(async () => {
    const sequence = ++configurationSequence.current;
    setConfiguration({ status: "loading" });
    try {
      const items = await listInventoryConfigurationItems();
      if (sequence === configurationSequence.current) {
        setConfiguration({ status: "ready", items });
      }
    } catch (error) {
      if (sequence !== configurationSequence.current) return;
      if (error instanceof InventoryProblemError && error.status === 401) {
        handleUnauthorized();
      } else if (
        error instanceof InventoryProblemError &&
        error.status === 403
      ) {
        setConfiguration({ status: "forbidden" });
      } else {
        setConfiguration({ status: "error" });
      }
    }
  }, [handleUnauthorized]);

  const refreshOperation = useCallback(
    async (showLoading = true) => {
      const sequence = ++operationSequence.current;
      if (showLoading) setOperation({ status: "loading" });
      try {
        const items = await listInventoryOperationalItems();
        if (sequence === operationSequence.current) {
          operationAuthorizedRef.current = true;
          setOperation({ status: "ready", items });
        }
      } catch (error) {
        if (sequence !== operationSequence.current) return;
        operationAuthorizedRef.current = false;
        if (error instanceof InventoryProblemError && error.status === 401) {
          handleUnauthorized();
        } else if (
          error instanceof InventoryProblemError &&
          error.status === 403
        ) {
          setOperation({ status: "forbidden" });
          setHistoryItem(null);
        } else {
          setOperation({ status: "error" });
        }
      }
    },
    [handleUnauthorized],
  );

  useEffect(() => {
    const timeout = window.setTimeout(() => {
      void refreshConfiguration();
      void refreshOperation();
    }, 0);
    return () => {
      window.clearTimeout(timeout);
      cancelReadRequests();
    };
  }, [cancelReadRequests, refreshConfiguration, refreshOperation]);

  const executeCreateIntent = useCallback(
    async (intent: CreateIntent) => {
      let antiforgeryToken: string;
      try {
        antiforgeryToken = await getAntiforgeryToken();
      } catch (error) {
        setCreateIntent(null);
        if (error instanceof SessionProblemError && error.status === 401) {
          handleUnauthorized();
          return;
        }
        setCreateNotice({
          kind: "functional-error",
          message: "No se pudo preparar la solicitud. Intentá nuevamente.",
        });
        return;
      }

      try {
        await createInventoryItem(
          intent.request,
          intent.idempotencyKey,
          antiforgeryToken,
        );
        setCreateIntent(null);
        setOperationalName("");
        setOperationalUnit("");
        setCreateNotice({
          kind: "success",
          message: "Elemento de Inventario creado correctamente.",
        });
        const refreshes: Promise<void>[] = [refreshConfiguration()];
        if (operationAuthorizedRef.current) refreshes.push(refreshOperation());
        await Promise.all(refreshes);
      } catch (error) {
        if (error instanceof InventoryProblemError) {
          if (error.status === 401) {
            setCreateIntent(null);
            handleUnauthorized();
            return;
          }
          if (error.status === 408 || error.status >= 500) {
            setCreateIntent({ ...intent, phase: "uncertain" });
            setCreateNotice({
              kind: "uncertain",
              message: "No se pudo confirmar si el elemento fue creado.",
            });
            return;
          }

          setCreateIntent(null);
          if (error.problem.code?.endsWith(".antiforgery_invalid") === true) {
            discardAntiforgeryToken();
          }
          if (error.status === 403) {
            setConfiguration({ status: "forbidden" });
            setCreateNotice({
              kind: "functional-error",
              message: configurationForbiddenMessage,
            });
            return;
          }
          setCreateNotice({
            kind: "functional-error",
            message: creationFailureMessage(error),
          });
          if (error.status === 409) await refreshConfiguration();
          return;
        }

        setCreateIntent({ ...intent, phase: "uncertain" });
        setCreateNotice({
          kind: "uncertain",
          message: "No se pudo confirmar si el elemento fue creado.",
        });
      }
    },
    [
      handleUnauthorized,
      refreshConfiguration,
      refreshOperation,
      setCreateIntent,
    ],
  );

  async function submitCreate(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (createIntentRef.current !== null) return;
    const request = {
      operationalName: operationalName.trim(),
      operationalUnit: operationalUnit.trim(),
    };
    if (request.operationalName === "" || request.operationalUnit === "") {
      setCreateNotice({
        kind: "functional-error",
        message: "Completá el nombre y la unidad operacional.",
      });
      return;
    }

    const intent: CreateIntent = {
      phase: "submitting",
      request,
      idempotencyKey: crypto.randomUUID(),
    };
    setCreateNotice(null);
    setCreateIntent(intent);
    await executeCreateIntent(intent);
  }

  function retryCreate() {
    const intent = createIntentRef.current;
    if (intent?.phase !== "uncertain") return;
    const submitting = { ...intent, phase: "submitting" as const };
    setCreateNotice(null);
    setCreateIntent(submitting);
    void executeCreateIntent(submitting);
  }

  const creationBlocked = createIntent !== null;

  const handleAuthoritativeOperation = useCallback(
    async (itemId: string) => {
      await refreshOperation(false);
      if (historyItem?.itemId === itemId) {
        setHistoryRefreshRevision((current) => current + 1);
      }
    },
    [historyItem?.itemId, refreshOperation],
  );

  return (
    <section className="inventory-family" aria-labelledby="inventory-heading">
      <div className="inventory-family-heading">
        <p className="eyebrow">Inventario</p>
        <h2 id="inventory-heading">Inventario</h2>
        {(configuration.status === "ready" || operation.status === "ready") && (
          <nav aria-label="Secciones de Inventario">
            {configuration.status === "ready" && (
              <a href="#inventory-configuration">Configuración</a>
            )}
            {operation.status === "ready" && (
              <a href="#inventory-operation">Operación</a>
            )}
          </nav>
        )}
      </div>

      <section
        id="inventory-configuration"
        className="panel"
        aria-labelledby="inventory-configuration-heading"
        aria-busy={configuration.status === "loading"}
      >
        <div className="section-heading">
          <h3 id="inventory-configuration-heading">
            Configuración de Inventario
          </h3>
          {configuration.status === "ready" && (
            <button
              type="button"
              className="secondary-button"
              onClick={() => void refreshConfiguration()}
            >
              Actualizar configuración
            </button>
          )}
        </div>

        {configuration.status === "loading" && (
          <p role="status">Cargando configuración de Inventario…</p>
        )}
        {configuration.status === "forbidden" && (
          <p className="notice notice--functional-error" role="alert">
            {configurationForbiddenMessage}
          </p>
        )}
        {configuration.status === "error" && (
          <div className="notice notice--functional-error" role="alert">
            <p>No se pudo cargar la configuración de Inventario.</p>
            <button type="button" onClick={() => void refreshConfiguration()}>
              Reintentar
            </button>
          </div>
        )}
        {configuration.status === "ready" && (
          <>
            <form
              className="inventory-create-form"
              onSubmit={(event) => void submitCreate(event)}
            >
              <label htmlFor="inventory-operational-name">
                Nombre operacional
              </label>
              <input
                id="inventory-operational-name"
                name="operationalName"
                value={operationalName}
                disabled={creationBlocked}
                required
                onChange={(event) => setOperationalName(event.target.value)}
              />
              <label htmlFor="inventory-operational-unit">
                Unidad operacional
              </label>
              <input
                id="inventory-operational-unit"
                name="operationalUnit"
                value={operationalUnit}
                disabled={creationBlocked}
                required
                onChange={(event) => setOperationalUnit(event.target.value)}
              />
              <button type="submit" disabled={creationBlocked}>
                {createIntent?.phase === "submitting"
                  ? "Creando…"
                  : createIntent?.phase === "uncertain"
                    ? "Creación pendiente"
                    : "Crear elemento"}
              </button>
              <p className="inventory-count-help">
                La existencia se establecerá después mediante un conteo.
              </p>
            </form>

            {createNotice && (
              <div
                className={`notice notice--${createNotice.kind}`}
                role={createNotice.kind === "success" ? "status" : "alert"}
              >
                <p>{createNotice.message}</p>
                {createIntent?.phase === "uncertain" && (
                  <button type="button" onClick={retryCreate}>
                    Reintentar
                  </button>
                )}
              </div>
            )}

            {configuration.items.length === 0 ? (
              <p>No hay elementos de Inventario configurados.</p>
            ) : (
              <div className="table-scroll">
                <table>
                  <thead>
                    <tr>
                      <th scope="col">Nombre operacional</th>
                      <th scope="col">Unidad operacional</th>
                    </tr>
                  </thead>
                  <tbody>
                    {configuration.items.map((item) => (
                      <tr key={item.itemId}>
                        <td>{item.operationalName}</td>
                        <td>{item.operationalUnit}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            )}
          </>
        )}
      </section>

      <section
        id="inventory-operation"
        className="panel"
        aria-labelledby="inventory-operation-heading"
        aria-busy={operation.status === "loading"}
      >
        <div className="section-heading">
          <div>
            <h3 id="inventory-operation-heading">
              Estado actual de Inventario
            </h3>
            <p>La cantidad que NexoBar registra ahora.</p>
          </div>
          {operation.status === "ready" && (
            <button
              type="button"
              className="secondary-button"
              onClick={() => void refreshOperation()}
            >
              Actualizar estado
            </button>
          )}
        </div>

        {operation.status === "loading" && (
          <p role="status">Cargando estado de Inventario…</p>
        )}
        {operation.status === "forbidden" && (
          <p className="notice notice--functional-error" role="alert">
            {operationForbiddenMessage}
          </p>
        )}
        {operation.status === "error" && (
          <div className="notice notice--functional-error" role="alert">
            <p>No se pudo cargar el estado de Inventario.</p>
            <button type="button" onClick={() => void refreshOperation()}>
              Reintentar
            </button>
          </div>
        )}
        {operation.status === "ready" && operation.items.length === 0 && (
          <p>No hay elementos de Inventario disponibles para operar.</p>
        )}
        {operation.status === "ready" && operation.items.length > 0 && (
          <div className="inventory-operational-items">
            {operation.items.map((item) => (
              <article
                className="inventory-operational-item"
                key={item.itemId}
                aria-label={item.operationalName}
              >
                <h4>{item.operationalName}</h4>
                {!item.quantityEstablished ? (
                  <div className="inventory-unestablished">
                    <p>Existencia no establecida</p>
                    <p>Requiere conteo y reconciliación.</p>
                  </div>
                ) : (
                  <p className="inventory-current-quantity">
                    <span>Existencia registrada</span>
                    <strong>
                      {item.currentRegisteredQuantity} {item.operationalUnit}
                    </strong>
                  </p>
                )}
                {item.hasNegativeBalanceInconsistency && (
                  <div className="inventory-negative-warning" role="alert">
                    <strong>Inconsistencia de saldo</strong>
                    <span>Realiza un conteo para verificar la existencia.</span>
                  </div>
                )}
                <InventoryItemOperations
                  item={item}
                  onUnauthorized={handleUnauthorized}
                  onStateRefresh={() => refreshOperation(false)}
                  onAuthoritativeMutation={handleAuthoritativeOperation}
                  onItemUnavailable={() => void refreshOperation()}
                />
                <button
                  type="button"
                  className="secondary-button"
                  aria-label={`Ver movimientos de ${item.operationalName}`}
                  onClick={() => setHistoryItem(item)}
                >
                  Ver movimientos
                </button>
              </article>
            ))}
          </div>
        )}

        <InventoryHistory
          key={historyItem?.itemId ?? "no-inventory-history"}
          item={historyItem}
          refreshRevision={historyRefreshRevision}
          onClose={() => setHistoryItem(null)}
          onUnauthorized={handleUnauthorized}
          onItemUnavailable={() => void refreshOperation()}
        />
      </section>
    </section>
  );
}
