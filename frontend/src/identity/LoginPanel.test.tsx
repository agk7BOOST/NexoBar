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

  it("clears secret and reports the current Identity after success", async () => {
    vi.mocked(login).mockResolvedValue({
      identityId: "identity-1",
      operationalName: "Ana",
    });
    const onAuthenticated = vi.fn();
    const user = userEvent.setup();
    render(<LoginPanel onAuthenticated={onAuthenticated} />);

    await user.type(screen.getByLabelText("Identificador de acceso"), "ana");
    const secret = screen.getByLabelText("Secreto");
    await user.type(secret, "very-secret");
    await user.click(screen.getByRole("button", { name: "Ingresar" }));

    await waitFor(() => expect(secret).toHaveValue(""));
    expect(login).toHaveBeenCalledWith("ana", "very-secret");
    expect(onAuthenticated).toHaveBeenCalledWith({
      identityId: "identity-1",
      operationalName: "Ana",
    });
  });

  it("shows the same generic message for invalid credentials", async () => {
    vi.mocked(login).mockRejectedValue(
      new SessionProblemError(401, { code: "invalid_credentials" }),
    );
    const user = userEvent.setup();
    render(<LoginPanel onAuthenticated={vi.fn()} />);

    await user.type(
      screen.getByLabelText("Identificador de acceso"),
      "missing",
    );
    await user.type(screen.getByLabelText("Secreto"), "wrong");
    await user.click(screen.getByRole("button", { name: "Ingresar" }));

    expect(await screen.findByRole("alert")).toHaveTextContent(
      "No se pudo ingresar con las credenciales proporcionadas.",
    );
  });
});
