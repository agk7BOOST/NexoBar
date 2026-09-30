import { useState, type FormEvent } from "react";
import {
  login,
  SessionProblemError,
  type CurrentIdentity,
} from "./sessionClient.ts";

interface LoginPanelProps {
  onAuthenticated: (identity: CurrentIdentity) => void;
  reason?: string;
}

export function LoginPanel({ onAuthenticated, reason }: LoginPanelProps) {
  const [loginIdentifier, setLoginIdentifier] = useState("");
  const [secret, setSecret] = useState("");
  const [isSubmitting, setIsSubmitting] = useState(false);
  const [message, setMessage] = useState<string | null>(null);
  const [showPassword, setShowPassword] = useState(false);
  const [emptyFields, setEmptyFields] = useState(false);

  async function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (isSubmitting) return;
    setEmptyFields(true);
    if (!loginIdentifier.trim() || !secret) {
      setMessage("Ingresá tu usuario y contraseña.");
      return;
    }
    setIsSubmitting(true);
    setMessage(null);
    try {
      const identity = await login(loginIdentifier, secret);
      setSecret("");
      onAuthenticated(identity);
    } catch (error) {
      setMessage(
        error instanceof SessionProblemError && error.status === 401
          ? "No pudimos ingresar. Revisá el usuario y la contraseña."
          : "No pudimos iniciar sesión. Intentá nuevamente.",
      );
    } finally {
      setIsSubmitting(false);
    }
  }

  return (
    <section className="panel session-panel" aria-labelledby="login-heading">
      <h2 id="login-heading">Ingresar</h2>
      {reason && <p role="status">{reason}</p>}
      <form className="login-form" onSubmit={(event) => void submit(event)}>
        <label htmlFor="login-identifier">Usuario de acceso</label>
        <input
          id="login-identifier"
          name="loginIdentifier"
          autoComplete="username"
          aria-invalid={(emptyFields && !loginIdentifier.trim()) || undefined}
          aria-describedby={
            emptyFields && !loginIdentifier.trim() ? "login-message" : undefined
          }
          value={loginIdentifier}
          onChange={(event) => setLoginIdentifier(event.target.value)}
        />
        <label htmlFor="login-secret">Contraseña</label>
        <input
          id="login-secret"
          name="secret"
          type={showPassword ? "text" : "password"}
          autoComplete="current-password"
          aria-invalid={(emptyFields && !secret) || undefined}
          aria-describedby={
            emptyFields && !secret ? "login-message" : undefined
          }
          value={secret}
          onChange={(event) => setSecret(event.target.value)}
        />
        <button
          className="secondary-button"
          type="button"
          aria-controls="login-secret"
          aria-pressed={showPassword}
          onClick={() => setShowPassword((value) => !value)}
        >
          {showPassword ? "Ocultar contraseña" : "Mostrar contraseña"}
        </button>
        <button type="submit" disabled={isSubmitting}>
          {isSubmitting ? "Ingresando…" : "Ingresar"}
        </button>
      </form>
      {message && (
        <p
          id="login-message"
          className="notice notice--functional-error"
          role="alert"
        >
          {message}
        </p>
      )}
    </section>
  );
}
