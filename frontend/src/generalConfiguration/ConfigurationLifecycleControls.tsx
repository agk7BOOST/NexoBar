import { useState, type FormEvent } from "react";
import {
  getAntiforgeryToken,
  SessionProblemError,
} from "../identity/sessionClient.ts";
import {
  ConfigurationLifecycleError,
  executeConfigurationLifecycle,
  type ConfigurationEntity,
  type LifecycleAction,
  type LifecycleRequest,
} from "./configurationLifecycleClient.ts";

interface Intention {
  action: LifecycleAction;
  request: LifecycleRequest;
  key: string;
  token: string;
}
export function ConfigurationLifecycleControls({
  entity,
  item,
  disabled,
  onReload,
  onUnauthorized,
  onForbidden,
  onPendingChange,
}: {
  entity: ConfigurationEntity;
  item: { id: string; operationalName: string; isActive: boolean };
  disabled: boolean;
  onReload: () => Promise<unknown>;
  onUnauthorized: () => void;
  onForbidden: () => void;
  onPendingChange: (pending: boolean) => void;
}) {
  const [editing, setEditing] = useState<"rename" | "retire" | "delete" | null>(
    null,
  );
  const [name, setName] = useState(item.operationalName);
  const [busy, setBusy] = useState(false);
  const [uncertain, setUncertain] = useState<Intention | null>(null);
  const [notice, setNotice] = useState("");
  const [refreshRequired, setRefreshRequired] = useState(false);
  const blocked = busy || uncertain !== null || refreshRequired || disabled;
  async function refresh() {
    try {
      if ((await onReload()) === false) throw new Error("Read failed");
      setRefreshRequired(false);
      onPendingChange(false);
      return true;
    } catch {
      setRefreshRequired(true);
      onPendingChange(true);
      return false;
    }
  }
  async function submit(intention: Intention) {
    setBusy(true);
    onPendingChange(true);
    setNotice("");
    let committed = false;
    try {
      await executeConfigurationLifecycle(
        entity,
        item.id,
        intention.action,
        intention.request,
        intention.key,
        intention.token,
      );
      committed = true;
      setUncertain(null);
      setEditing(null);
      setNotice(
        (await refresh())
          ? "Operación confirmada."
          : "Operación confirmada. No se pudo actualizar la lista; actualizala antes de continuar.",
      );
    } catch (error) {
      if (committed) {
        setNotice(
          "Operación confirmada. Actualizá la lista antes de continuar.",
        );
        setRefreshRequired(true);
      } else if (
        error instanceof ConfigurationLifecycleError &&
        error.status < 500
      ) {
        setUncertain(null);
        onPendingChange(false);
        if (error.status === 401) onUnauthorized();
        else if (error.status === 403) onForbidden();
        else {
          setNotice(error.detail ?? "No se pudo completar la operación.");
          if (error.status === 409) await refresh();
        }
      } else {
        setUncertain(intention);
        setNotice(
          "No pudimos confirmar el resultado. Reintentá esta misma operación sin duplicarla.",
        );
      }
    } finally {
      setBusy(false);
    }
  }
  async function start(action: LifecycleAction, event?: FormEvent) {
    event?.preventDefault();
    if (blocked || (action === "rename" && !name.trim())) return;
    setBusy(true);
    onPendingChange(true);
    try {
      const token = await getAntiforgeryToken();
      await submit({
        action,
        token,
        key: crypto.randomUUID(),
        request: {
          expectedCurrentOperationalName: item.operationalName,
          expectedIsActive: item.isActive,
          ...(action === "rename" ? { newOperationalName: name } : {}),
        },
      });
    } catch (error) {
      onPendingChange(false);
      if (error instanceof SessionProblemError && error.problem.status === 401)
        onUnauthorized();
      else setNotice("No se pudo preparar la operación segura.");
    } finally {
      setBusy(false);
    }
  }
  return (
    <div role="group" aria-label={`Administrar ${item.operationalName}`}>
      <p>{item.isActive ? "Activo" : "Retirado"}</p>
      <button
        type="button"
        disabled={blocked}
        onClick={() => {
          setName(item.operationalName);
          setEditing("rename");
        }}
      >
        Cambiar nombre
      </button>
      <button
        type="button"
        disabled={blocked}
        onClick={() =>
          item.isActive ? setEditing("retire") : void start("reactivate")
        }
      >
        {item.isActive ? "Retirar" : "Reactivar"}
      </button>
      <button
        type="button"
        disabled={blocked}
        onClick={() => setEditing("delete")}
      >
        Eliminar definitivamente
      </button>
      {editing && !uncertain && (
        <form
          aria-label={`Confirmar operación sobre ${item.operationalName}`}
          onSubmit={(event) => void start(editing, event)}
        >
          {editing === "rename" ? (
            <label>
              Nuevo nombre de {item.operationalName}
              <input
                value={name}
                disabled={blocked}
                onChange={(event) => setName(event.target.value)}
                required
              />
            </label>
          ) : (
            <p>
              {editing === "delete"
                ? "Se eliminará definitivamente si no tiene uso histórico ni dependencias. Esta acción no se puede deshacer."
                : entity === "contexts"
                  ? "Dejará de poder elegirse para nuevos pedidos o cambios de contexto. Los pedidos que ya lo usan no se modificarán."
                  : "Dejará de poder usarse para nueva preparación. El trabajo ya originado conservará este destino."}
            </p>
          )}
          <button type="submit" disabled={blocked}>
            Confirmar{" "}
            {editing === "rename"
              ? "cambio de nombre"
              : editing === "retire"
                ? "retiro"
                : "eliminación definitiva"}
          </button>
          <button
            type="button"
            disabled={blocked}
            onClick={() => setEditing(null)}
          >
            Cancelar
          </button>
        </form>
      )}
      {notice && <p role="status">{notice}</p>}
      {refreshRequired && (
        <button type="button" disabled={busy} onClick={() => void refresh()}>
          Actualizar lista
        </button>
      )}
      {uncertain && (
        <button
          type="button"
          disabled={busy}
          onClick={() => void submit(uncertain)}
        >
          Reintentar esta operación
        </button>
      )}
    </div>
  );
}
