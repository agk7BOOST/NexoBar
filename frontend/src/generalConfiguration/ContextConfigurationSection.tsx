import { ConfigurationLifecycleControls } from "./ConfigurationLifecycleControls.tsx";
import { useCallback, useEffect, useState, type FormEvent } from "react";
import { getAntiforgeryToken } from "../identity/sessionClient.ts";
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
  const [pending, setPending] = useState<{
    name: string;
    key: string;
    token: string;
  } | null>(null);
  const [busy, setBusy] = useState(false);
  const [lifecyclePending, setLifecyclePending] = useState(false);
  const reload = useCallback(async () => {
    try {
      setContexts(await listConfiguredContexts());
      return true;
    } catch (error) {
      if (
        error instanceof ContextConfigurationError &&
        error.problem.status === 401
      )
        onUnauthorized();
      else if (
        error instanceof ContextConfigurationError &&
        error.problem.status === 403
      )
        onForbidden();
      else setNotice("No se pudo cargar la configuración de Contextos.");
      return false;
    }
  }, [onForbidden, onUnauthorized]);
  useEffect(() => {
    const scheduledRead = window.setTimeout(() => void reload(), 0);
    return () => window.clearTimeout(scheduledRead);
  }, [reload]);
  async function submit(intent: { name: string; key: string; token: string }) {
    setBusy(true);
    setNotice("");
    try {
      await createConfiguredContext(
        { operationalName: intent.name },
        intent.key,
        intent.token,
      );
      setPending(null);
      if (name === intent.name) setName("");
      await reload();
      setNotice("Contexto creado correctamente.");
    } catch (error) {
      if (error instanceof ContextConfigurationError) {
        if (error.problem.status === 401) {
          onUnauthorized();
          return;
        }
        if (error.problem.status === 403) {
          onForbidden();
          return;
        }
        setPending(null);
        setNotice(
          error.problem.code ===
            "operational_configuration.context.operational_name_conflict"
            ? "Ya existe un Contexto equivalente."
            : error.problem.code ===
                "operational_configuration.context.operational_name_invalid"
              ? "Ingresá un nombre operacional válido."
              : "No se pudo crear el Contexto.",
        );
      } else {
        setPending(intent);
        setNotice(
          "No pudimos confirmar si se creó el Contexto. Podés reintentar esta operación sin duplicarla.",
        );
      }
    } finally {
      setBusy(false);
    }
  }
  async function handle(event: FormEvent) {
    event.preventDefault();
    if (pending || !name.trim()) {
      if (!pending) setNotice("Ingresá un nombre operacional válido.");
      return;
    }
    try {
      await submit({
        name,
        key: crypto.randomUUID(),
        token: await getAntiforgeryToken(),
      });
    } catch {
      setNotice("No se pudo preparar la creación segura del Contexto.");
    }
  }
  return (
    <section aria-labelledby="contexts-title">
      <h3 id="contexts-title">Contextos</h3>
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
              onUnauthorized={onUnauthorized}
              onForbidden={onForbidden}
            />
          </li>
        ))}
      </ul>
      <form onSubmit={(event) => void handle(event)}>
        <label htmlFor="configured-context-name">
          Nombre operacional del Contexto
        </label>
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
          Crear Contexto
        </button>
      </form>
      {notice && <p role="status">{notice}</p>}
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
