import { useState } from "react";
import { logout, type CurrentIdentity } from "./sessionClient.ts";

interface SessionBarProps {
  identity: CurrentIdentity;
  onLoggedOut: () => void;
}

export function SessionBar({ identity, onLoggedOut }: SessionBarProps) {
  const [isLoggingOut, setIsLoggingOut] = useState(false);
  const [message, setMessage] = useState<string | null>(null);

  async function endSession() {
    setIsLoggingOut(true);
    setMessage(null);
    try {
      await logout();
      onLoggedOut();
    } catch {
      setMessage("No se pudo cerrar la sesión.");
    } finally {
      setIsLoggingOut(false);
    }
  }

  return (
    <section className="session-bar" aria-label="Usuario actual">
      <p>
        Usuario actual: <strong>{identity.operationalName}</strong>
      </p>
      <button
        className="secondary-button"
        type="button"
        disabled={isLoggingOut}
        onClick={() => void endSession()}
      >
        {isLoggingOut ? "Cerrando…" : "Cerrar sesión"}
      </button>
      {message && <p role="alert">{message}</p>}
    </section>
  );
}
