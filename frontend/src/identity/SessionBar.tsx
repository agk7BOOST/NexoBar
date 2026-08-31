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
    <section className="session-bar" aria-label="Identity actual">
      <p>
        Identity actual: <strong>{identity.operationalName}</strong>
      </p>
      <button
        className="secondary-button"
        type="button"
        disabled={isLoggingOut}
        onClick={() => void endSession()}
      >
        {isLoggingOut ? "Cerrando…" : "Cambiar persona / salir"}
      </button>
      {message && <p role="alert">{message}</p>}
    </section>
  );
}
