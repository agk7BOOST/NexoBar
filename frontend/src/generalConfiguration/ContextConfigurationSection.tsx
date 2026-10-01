import { ConfigurationLifecycleControls } from "./ConfigurationLifecycleControls.tsx";
import {
  useCallback,
  useEffect,
  useRef,
  useState,
  type FormEvent,
} from "react";
import {
  getAntiforgeryToken,
  SessionProblemError,
} from "../identity/sessionClient.ts";
import {
  createConfiguredContext,
  ContextConfigurationError,
  listConfiguredContexts,
  type ConfiguredContext,
} from "./contextConfigurationClient.ts";

export function ContextConfigurationSection({
  onUnauthorized,
  onForbidden,
}: {
  onUnauthorized: () => void;
  onForbidden: () => void;
}) {
  const [contexts, setContexts] = useState<ConfiguredContext[]>([]);
  const [name, setName] = useState("");
  const [notice, setNotice] = useState("");
  const [noticeError, setNoticeError] = useState(false);
  const [pending, setPending] = useState<{
    name: string;
    key: string;
    token: string;
  } | null>(null);
  const [busy, setBusy] = useState(false);
  const [lifecyclePending, setLifecyclePending] = useState(false);
  const [readState, setReadState] = useState<
    "loading" | "ready" | "error" | "forbidden"
  >("loading");
  const readGeneration = useRef(0);
  const reload = useCallback(async () => {
    const generation = ++readGeneration.current;
    setReadState("loading");
    try {
      const loaded = await listConfiguredContexts();
      if (generation !== readGeneration.current) return false;
      setContexts(loaded);
      setReadState("ready");
      setNotice((current) =>
        current.replace(
          ", pero no pudimos actualizar la lista.",
          ". La lista está actualizada.",
        ),
      );
      return true;
    } catch (error) {
      if (generation !== readGeneration.current) return false;
      setReadState("error");
      if (
        error instanceof ContextConfigurationError &&
        error.problem.status === 401
      )
        onUnauthorized();
      else if (
        error instanceof ContextConfigurationError &&
        error.problem.status === 403
      ) {
        setReadState("forbidden");
        onForbidden();
      }
      return false;
    }
  }, [onForbidden, onUnauthorized]);
  useEffect(() => {
    const generations = readGeneration;
    const scheduledRead = window.setTimeout(() => void reload(), 0);
    return () => {
      window.clearTimeout(scheduledRead);
      generations.current++;
    };
  }, [reload]);
  async function submit(intent: { name: string; key: string; token: string }) {
    setBusy(true);
    setNotice("");
    setNoticeError(false);
    try {
      await createConfiguredContext(
        { operationalName: intent.name },
        intent.key,
        intent.token,
      );
      setPending(null);
      if (name === intent.name) setName("");
      const refreshed = await reload();
      setNotice(
        refreshed
          ? `Se creó “${intent.name}”.`
          : `Se creó “${intent.name}”, pero no pudimos actualizar la lista.`,
      );
    } catch (error) {
      if (
        error instanceof ContextConfigurationError &&
        error.problem.status < 500 &&
        error.problem.status !== 408
      ) {
        if (error.problem.status === 401) {
          onUnauthorized();
          return;
        }
        if (error.problem.status === 403) {
          onForbidden();
          return;
        }
        setPending(null);
        setNoticeError(true);
        setNotice(
          error.problem.code ===
            "operational_configuration.context.operational_name_conflict"
            ? "Ya existe un Contexto equivalente."
            : error.problem.code ===
                "operational_configuration.context.operational_name_invalid"
              ? "Ingresá un nombre válido."
              : "No se pudo crear el Contexto.",
        );
      } else {
        setPending(intent);
        setNotice(
          "No pudimos confirmar si se creó el contexto. Podés reintentar esta operación sin duplicarla.",
        );
      }
    } finally {
      setBusy(false);
    }
  }
  async function handle(event: FormEvent) {
    event.preventDefault();
    if (busy || pending || !name.trim()) {
      if (!pending) setNotice("Ingresá un nombre válido.");
      return;
    }
    setBusy(true);
    try {
      await submit({
        name,
        key: crypto.randomUUID(),
        token: await getAntiforgeryToken(),
      });
    } catch (error) {
      if (error instanceof SessionProblemError && error.status === 401)
        onUnauthorized();
      else {
        setNoticeError(true);
        setNotice(
          "No pudimos preparar la creación del contexto. Intentá nuevamente.",
        );
      }
    } finally {
      setBusy(false);
    }
  }
  return (
    <section aria-labelledby="contexts-title">
      <h3 id="contexts-title" tabIndex={-1}>
        Contextos
      </h3>
      {readState === "loading" && <p role="status">Cargando contextos…</p>}
      {readState === "error" && (
        <div role="alert">
          <p>No pudimos consultar los contextos.</p>
          <button type="button" onClick={() => void reload()}>
            Reintentar consulta
          </button>
        </div>
      )}
      {readState === "forbidden" && (
        <p role="alert">
          Tu usuario no tiene autorización para configurar contextos.
        </p>
      )}
      {readState === "ready" && contexts.length === 0 && (
        <p>Todavía no hay contextos. Podés crear uno con el formulario.</p>
      )}
      <ul aria-label="Contextos configurados">
        {contexts.map((context) => (
          <li key={context.id}>
            <span>{context.operationalName}</span>
            <ConfigurationLifecycleControls
              entity="contexts"
              item={context}
              disabled={busy || pending !== null || lifecyclePending}
              onPendingChange={setLifecyclePending}
              onReload={reload}
              onResult={(message) => {
                setNoticeError(false);
                setNotice(message);
              }}
              onUnauthorized={onUnauthorized}
              onForbidden={onForbidden}
            />
          </li>
        ))}
      </ul>
      <form onSubmit={(event) => void handle(event)}>
        <label htmlFor="configured-context-name">Nombre del contexto</label>
        <input
          id="configured-context-name"
          value={name}
          onChange={(event) => setName(event.target.value)}
          required
          disabled={busy || lifecyclePending}
        />
        <button
          type="submit"
          disabled={busy || pending !== null || lifecyclePending}
        >
          Crear contexto
        </button>
      </form>
      {notice && <p role={noticeError ? "alert" : "status"}>{notice}</p>}
      {pending && (
        <div role="region" aria-label="Creación de Contexto incierta">
          <p>{pending.name}</p>
          <button
            type="button"
            disabled={busy || lifecyclePending}
            onClick={() => void submit(pending)}
          >
            Reintentar esta operación
          </button>
        </div>
      )}
    </section>
  );
}
