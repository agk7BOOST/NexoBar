import { useState, type FormEvent } from "react";
import {
  login,
  SessionProblemError,
  type CurrentIdentity,
} from "./sessionClient.ts";

interface LoginPanelProps {
  onAuthenticated: (identity: CurrentIdentity) => void;
}

export function LoginPanel({ onAuthenticated }: LoginPanelProps) {
  const [loginIdentifier, setLoginIdentifier] = useState("");
  const [secret, setSecret] = useState("");
  const [isSubmitting, setIsSubmitting] = useState(false);
  const [message, setMessage] = useState<string | null>(null);

  async function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setIsSubmitting(true);
    setMessage(null);
    try {
      const identity = await login(loginIdentifier, secret);
      setSecret("");
      onAuthenticated(identity);
    } catch (error) {
      setMessage(
        error instanceof SessionProblemError && error.status === 401
          ? "No se pudo ingresar con las credenciales proporcionadas."
          : "No se pudo iniciar la sesión.",
      );
    } finally {
      setIsSubmitting(false);
    }
  }

  return (
    <section className="panel session-panel" aria-labelledby="login-heading">
      <h2 id="login-heading">Ingresar</h2>
      <form className="login-form" onSubmit={(event) => void submit(event)}>
        <label htmlFor="login-identifier">Identificador de acceso</label>
        <input
          id="login-identifier"
          name="loginIdentifier"
          autoComplete="username"
          value={loginIdentifier}
          onChange={(event) => setLoginIdentifier(event.target.value)}
        />
        <label htmlFor="login-secret">Secreto</label>
        <input
          id="login-secret"
          name="secret"
          type="password"
          autoComplete="current-password"
          value={secret}
          onChange={(event) => setSecret(event.target.value)}
        />
        <button type="submit" disabled={isSubmitting}>
          {isSubmitting ? "Ingresando…" : "Ingresar"}
        </button>
      </form>
      {message && (
        <p className="notice notice--functional-error" role="alert">
          {message}
        </p>
      )}
    </section>
  );
}
