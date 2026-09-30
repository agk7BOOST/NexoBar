import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { LoginPanel } from "./LoginPanel.tsx";
import { login, SessionProblemError } from "./sessionClient.ts";

vi.mock("./sessionClient.ts", async (importOriginal) => {
  const original = await importOriginal<typeof import("./sessionClient.ts")>();
  return { ...original, login: vi.fn() };
});

describe("LoginPanel", () => {
  beforeEach(() => {
    vi.mocked(login).mockReset();
  });
  it("rejects empty local input and toggles the same password without submitting", async () => {
    const user = userEvent.setup();
    render(<LoginPanel onAuthenticated={vi.fn()} />);
    await user.click(screen.getByRole("button", { name: "Ingresar" }));
    expect(login).not.toHaveBeenCalled();
    expect(screen.getByLabelText("Usuario de acceso")).toHaveAttribute(
      "aria-invalid",
      "true",
    );
    const password = screen.getByLabelText("Contraseña");
    await user.type(password, "intact-secret");
    await user.click(
      screen.getByRole("button", { name: "Mostrar contraseña" }),
    );
    expect(password).toHaveAttribute("type", "text");
    expect(password).toHaveValue("intact-secret");
    await user.click(
      screen.getByRole("button", { name: "Ocultar contraseña" }),
    );
    expect(password).toHaveAttribute("type", "password");
    expect(password).toHaveValue("intact-secret");
    expect(login).not.toHaveBeenCalled();
  });

  it("submits with Enter and keeps a generic 401 without blaming either field", async () => {
    vi.mocked(login).mockRejectedValue(
      new SessionProblemError(401, { code: "invalid_credentials" }),
    );
    const user = userEvent.setup();
    render(<LoginPanel onAuthenticated={vi.fn()} />);
    await user.type(screen.getByLabelText("Usuario de acceso"), "missing");
    await user.type(screen.getByLabelText("Contraseña"), "wrong{Enter}");
    expect(await screen.findByRole("alert")).toHaveTextContent(
      "No pudimos ingresar. Revisá el usuario y la contraseña.",
    );
    expect(login).toHaveBeenCalledOnce();
    expect(screen.getByLabelText("Usuario de acceso")).not.toHaveAttribute(
      "aria-invalid",
    );
    expect(screen.getByLabelText("Contraseña")).not.toHaveAttribute(
      "aria-invalid",
    );
  });

  it("clears secret and reports the current Identity after success", async () => {
    vi.mocked(login).mockResolvedValue({
      identityId: "identity-1",
      operationalName: "Ana",
      responsibilities: ["CatalogConfiguration"],
    });
    const onAuthenticated = vi.fn();
    const user = userEvent.setup();
    render(<LoginPanel onAuthenticated={onAuthenticated} />);

    await user.type(screen.getByLabelText("Usuario de acceso"), "ana");
    const secret = screen.getByLabelText("Contraseña");
    await user.type(secret, "very-secret");
    await user.click(screen.getByRole("button", { name: "Ingresar" }));

    await waitFor(() => expect(secret).toHaveValue(""));
    expect(login).toHaveBeenCalledWith("ana", "very-secret");
    expect(onAuthenticated).toHaveBeenCalledWith({
      identityId: "identity-1",
      operationalName: "Ana",
      responsibilities: ["CatalogConfiguration"],
    });
  });

  it("shows the same generic message for invalid credentials", async () => {
    vi.mocked(login).mockRejectedValue(
      new SessionProblemError(401, { code: "invalid_credentials" }),
    );
    const user = userEvent.setup();
    render(<LoginPanel onAuthenticated={vi.fn()} />);

    await user.type(screen.getByLabelText("Usuario de acceso"), "missing");
    await user.type(screen.getByLabelText("Contraseña"), "wrong");
    await user.click(screen.getByRole("button", { name: "Ingresar" }));

    expect(await screen.findByRole("alert")).toHaveTextContent(
      "No pudimos ingresar. Revisá el usuario y la contraseña.",
    );
  });
});
