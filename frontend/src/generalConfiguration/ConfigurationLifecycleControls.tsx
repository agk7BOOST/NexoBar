import { useEffect, useRef, useState, type FormEvent } from "react";
import { SensitiveActionDialog } from "../ui/SensitiveActionDialog.tsx";
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
function rejectionMessage(error: ConfigurationLifecycleError): string {
  if (error.code?.endsWith("retire.active_products"))
    return "Este destino tiene productos activos que lo usan. Cambiá o deshabilitá su preparación antes de retirarlo.";
  if (error.code?.endsWith("delete.product_references"))
    return "Este destino sigue configurado en productos. Revisá su preparación antes de eliminarlo.";
  if (error.code?.endsWith("delete.preparation_enablements"))
    return "Este destino tiene habilitaciones de preparación. Revisalas antes de eliminarlo.";
  if (error.code?.endsWith("delete.operational_participation"))
    return "Debe conservarse porque ya se usó en pedidos o preparación. Podés retirarlo por separado.";
  if (error.code?.endsWith("operational_name_conflict"))
    return "Ya existe otro nombre equivalente, incluso entre los retirados. Elegí otro nombre.";
  if (error.code?.endsWith("concurrency_conflict"))
    return "La configuración cambió. Revisá la lista actualizada antes de volver a guardar.";
  if (error.code?.endsWith("already_retired"))
    return "Ya está retirado. Revisá la lista actualizada.";
  if (error.code?.endsWith("already_active"))
    return "Ya está activo. Revisá la lista actualizada.";
  if (error.code?.endsWith("idempotency_key_conflict"))
    return "Esta operación no coincide con el envío original. Revisá el estado antes de continuar.";
  if (error.status === 404)
    return "Este objeto ya no está disponible. Actualizá la lista.";
  return "No pudimos completar el cambio. Revisá los datos y el estado actual.";
}
export function ConfigurationLifecycleControls({
  entity,
  item,
  disabled,
  onReload,
  onUnauthorized,
  onForbidden,
  onPendingChange,
  onResult,
}: {
  entity: ConfigurationEntity;
  item: { id: string; operationalName: string; isActive: boolean };
  disabled: boolean;
  onReload: () => Promise<unknown>;
  onUnauthorized: () => void;
  onForbidden: () => void;
  onPendingChange: (pending: boolean) => void;
  onResult?: (message: string) => void;
}) {
  const [editing, setEditing] = useState<"rename" | "retire" | "delete" | null>(
    null,
  );
  const [name, setName] = useState(item.operationalName);
  const [busy, setBusy] = useState(false);
  const sending = useRef(false);
  const [uncertain, setUncertain] = useState<Intention | null>(null);
  const [notice, setNotice] = useState("");
  const [noticeError, setNoticeError] = useState(false);
  const [refreshRequired, setRefreshRequired] = useState(false);
  const editor = useRef<HTMLFormElement>(null);
  const opener = useRef<HTMLElement | null>(null);
  const fallbackFocusId =
    entity === "contexts"
      ? "contexts-title"
      : "preparation-responsibilities-title";
  const blocked = busy || uncertain !== null || refreshRequired || disabled;
  useEffect(() => {
    if (editing === "rename") {
      const input = editor.current?.querySelector("input");
      input?.focus();
      input?.select();
    } else if (editing === "retire")
      editor.current
        ?.querySelector<HTMLButtonElement>('button[type="button"]')
        ?.focus();
  }, [editing]);
  function returnFocus() {
    queueMicrotask(() => {
      if (
        opener.current?.isConnected &&
        !opener.current.closest("[hidden]") &&
        !opener.current.matches(":disabled")
      )
        opener.current.focus();
      else document.getElementById(fallbackFocusId)?.focus();
    });
  }
  function openEditor(kind: "rename" | "retire" | "delete") {
    opener.current =
      document.activeElement instanceof HTMLElement
        ? document.activeElement
        : null;
    setNotice("");
    setName(item.operationalName);
    setEditing(kind);
  }
  function closeEditor() {
    if (busy || uncertain) return;
    setEditing(null);
    returnFocus();
  }
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
    if (sending.current) return;
    sending.current = true;
    setBusy(true);
    onPendingChange(true);
    setNotice("");
    setNoticeError(false);
    let committed = false;
    try {
      const result = await executeConfigurationLifecycle(
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
      const outcome =
        intention.action === "rename"
          ? `“${intention.request.expectedCurrentOperationalName}” ahora se llama “${result.operationalName}”.`
          : `Se ${intention.action === "delete" ? "eliminó definitivamente" : intention.action === "retire" ? "retiró" : "reactivó"} “${intention.request.expectedCurrentOperationalName}”.`;
      const refreshed = await refresh();
      const message = refreshed
        ? outcome
        : outcome.slice(0, -1) + ", pero no pudimos actualizar la lista.";
      setNotice(message);
      if (intention.action === "delete") onResult?.(message);
      returnFocus();
    } catch (error) {
      if (committed) {
        setNotice(
          "Se confirmó el cambio, pero no pudimos actualizar la lista.",
        );
        setRefreshRequired(true);
      } else if (
        error instanceof ConfigurationLifecycleError &&
        error.status < 500 &&
        error.status !== 408
      ) {
        setUncertain(null);
        onPendingChange(false);
        if (error.status === 401) onUnauthorized();
        else if (error.status === 403) {
          onForbidden();
          setNotice(
            "Tu usuario no tiene autorización para configurar este objeto.",
          );
          setNoticeError(true);
        } else {
          setNotice(rejectionMessage(error));
          setNoticeError(true);
          if (error.status === 409) await refresh();
        }
      } else {
        setUncertain(intention);
        setEditing(null);
        setNotice(
          `No pudimos confirmar el resultado de la operación sobre “${intention.request.expectedCurrentOperationalName}”. Reintentá esta misma operación; no se duplicará.`,
        );
      }
    } finally {
      sending.current = false;
      setBusy(false);
    }
  }
  async function start(action: LifecycleAction, event?: FormEvent) {
    event?.preventDefault();
    if (blocked || sending.current || (action === "rename" && !name.trim()))
      return;
    sending.current = true;
    setBusy(true);
    onPendingChange(true);
    try {
      const token = await getAntiforgeryToken();
      sending.current = false;
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
      setNoticeError(true);
      if (error instanceof SessionProblemError && error.status === 401)
        onUnauthorized();
      else setNotice("No pudimos preparar la operación. Intentá nuevamente.");
    } finally {
      sending.current = false;
      setBusy(false);
    }
  }
  return (
    <div role="group" aria-label={`Administrar ${item.operationalName}`}>
      <p>{item.isActive ? "Activo" : "Retirado"}</p>
      <button
        type="button"
        disabled={blocked}
        aria-label={`Cambiar nombre de ${item.operationalName}`}
        onClick={() => openEditor("rename")}
      >
        Cambiar nombre
      </button>
      <button
        type="button"
        disabled={blocked}
        aria-label={`${item.isActive ? "Retirar" : "Reactivar"} ${item.operationalName}`}
        onClick={(event) => {
          if (item.isActive) openEditor("retire");
          else {
            opener.current = event.currentTarget;
            void start("reactivate");
          }
        }}
      >
        {item.isActive ? "Retirar" : "Reactivar"}
      </button>
      <button
        type="button"
        className="danger-button"
        disabled={blocked}
        aria-label={`Eliminar definitivamente ${item.operationalName}`}
        onClick={() => openEditor("delete")}
      >
        Eliminar definitivamente
      </button>
      {editing === "delete" && !uncertain && (
        <SensitiveActionDialog
          objectName={item.operationalName}
          consequence="Sólo puede eliminarse si no tiene uso histórico ni dependencias. Se quitará de la configuración actual."
          busy={busy}
          fallbackFocusId={fallbackFocusId}
          onConfirm={() => void start("delete")}
          onClose={closeEditor}
          feedback={
            notice && <p role={noticeError ? "alert" : "status"}>{notice}</p>
          }
        />
      )}
      {editing && editing !== "delete" && !uncertain && (
        <form
          ref={editor}
          aria-label={`Confirmar operación sobre ${item.operationalName}`}
          onSubmit={(event) => void start(editing, event)}
          onKeyDown={(event) => {
            if (event.key === "Escape" && !busy && !uncertain) {
              event.preventDefault();
              closeEditor();
            }
          }}
        >
          {editing === "rename" ? (
            <label>
              Nuevo nombre de {item.operationalName}
              <input
                value={name}
                disabled={busy}
                onChange={(event) => setName(event.target.value)}
                required
              />
            </label>
          ) : (
            <p>
              {entity === "contexts"
                ? `“${item.operationalName}” dejará de poder elegirse para nuevos pedidos o cambios de contexto. Los pedidos que ya lo usan no se modificarán.`
                : `“${item.operationalName}” dejará de poder usarse para nueva preparación. El trabajo ya originado conserva su destino y puede seguir operándose. No se modifican productos ni habilitaciones automáticamente.`}
            </p>
          )}
          <button type="submit" disabled={blocked}>
            {editing === "rename" ? "Guardar nombre" : "Confirmar retiro"}
          </button>
          <button type="button" disabled={busy} onClick={closeEditor}>
            Volver
          </button>
        </form>
      )}
      {notice && editing !== "delete" && (
        <p role={noticeError ? "alert" : "status"}>{notice}</p>
      )}
      {refreshRequired && (
        <button
          type="button"
          disabled={busy}
          onClick={async () => {
            if (await refresh())
              setNotice((current) =>
                current.replace(
                  ", pero no pudimos actualizar la lista.",
                  ". La lista está actualizada.",
                ),
              );
          }}
        >
          Reintentar consulta
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
