import { useEffect, useId, useRef, type ReactNode } from "react";

/** Presentation only. The caller owns the command and any uncertain intention. */
export function SensitiveActionDialog({
  objectName,
  consequence,
  busy,
  onConfirm,
  onClose,
  fallbackFocusId,
  feedback,
}: {
  objectName: string;
  consequence: string;
  busy: boolean;
  onConfirm: () => void;
  onClose: () => void;
  fallbackFocusId: string;
  feedback?: ReactNode;
}) {
  const dialogRef = useRef<HTMLDialogElement>(null);
  const safeButton = useRef<HTMLButtonElement>(null);
  const titleId = useId();
  const descriptionId = useId();
  useEffect(() => {
    const dialog = dialogRef.current!;
    const opener = document.activeElement;
    dialog.showModal();
    safeButton.current?.focus();
    return () => {
      dialog.close();
      queueMicrotask(() => {
        if (
          opener instanceof HTMLElement &&
          opener.isConnected &&
          !opener.closest("[hidden]") &&
          !opener.matches(":disabled")
        )
          opener.focus();
        else document.getElementById(fallbackFocusId)?.focus();
      });
    };
  }, [fallbackFocusId]);
  return (
    <dialog
      ref={dialogRef}
      className="sensitive-action-dialog"
      aria-labelledby={titleId}
      aria-describedby={descriptionId}
      aria-busy={busy}
      onCancel={(event) => {
        event.preventDefault();
        if (!busy) onClose();
      }}
    >
      <h3 id={titleId}>Eliminar definitivamente “{objectName}”</h3>
      <p id={descriptionId}>{consequence} Esta acción no se puede deshacer.</p>
      {feedback}
      <div className="intention-actions">
        <button
          type="button"
          className="danger-button"
          disabled={busy}
          onClick={onConfirm}
        >
          {busy ? "Eliminando…" : "Eliminar definitivamente"}
        </button>
        <button
          ref={safeButton}
          type="button"
          className="secondary-button"
          disabled={busy}
          onClick={onClose}
        >
          Volver
        </button>
      </div>
    </dialog>
  );
}
