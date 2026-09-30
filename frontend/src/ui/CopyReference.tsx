import { useState } from "react";

export function CopyReference({
  value,
  label = "Copiar referencia",
}: {
  value: string;
  label?: string;
}) {
  const [message, setMessage] = useState<{ value: string; text: string }>();
  async function copy() {
    try {
      await navigator.clipboard.writeText(value);
      setMessage({ value, text: "Copiado" });
    } catch {
      setMessage({
        value,
        text: "No pudimos copiar. Seleccioná la referencia completa y copiala.",
      });
    }
  }
  return (
    <span className="copy-reference">
      <button
        type="button"
        className="secondary-button"
        onClick={() => void copy()}
      >
        {label}
      </button>
      {message?.value === value && <span role="status">{message.text}</span>}
    </span>
  );
}
