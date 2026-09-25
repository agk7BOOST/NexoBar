import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { ContextConfigurationError } from "./contextConfigurationClient.ts";
import { ContextConfigurationSection } from "./ContextConfigurationSection.tsx";

const { list, create, token } = vi.hoisted(() => ({
  list: vi.fn(),
  create: vi.fn(),
  token: vi.fn(),
}));
vi.mock("./contextConfigurationClient.ts", async (importOriginal) => ({
  ...(await importOriginal<typeof import("./contextConfigurationClient.ts")>()),
  listConfiguredContexts: list,
  createConfiguredContext: create,
}));
vi.mock("../identity/sessionClient.ts", async (importOriginal) => ({
  ...(await importOriginal<typeof import("../identity/sessionClient.ts")>()),
  getAntiforgeryToken: token,
}));

beforeEach(() => {
  list
    .mockReset()
    .mockResolvedValue([{ id: "ctx-a", operationalName: "Mesa A" }]);
  create.mockReset();
  token.mockReset().mockResolvedValue("csrf");
});

describe("ContextConfigurationSection", () => {
  it("lista, crea e recarga de forma autoritativa; no ofrece acciones de lifecycle u Order", async () => {
    const user = userEvent.setup();
    create.mockResolvedValue({ id: "server-id", operationalName: "Salón" });
    render(
      <ContextConfigurationSection
        onUnauthorized={vi.fn()}
        onForbidden={vi.fn()}
      />,
    );
    expect(await screen.findByText("Mesa A")).toBeInTheDocument();
    await user.type(
      screen.getByLabelText("Nombre operacional del Contexto"),
      "Salón",
    );
    await user.click(screen.getByRole("button", { name: "Crear Contexto" }));
    await waitFor(() => expect(list).toHaveBeenCalledTimes(2));
    expect(create).toHaveBeenCalledWith(
      { operationalName: "Salón" },
      expect.any(String),
      "csrf",
    );
    expect(
      screen.getByText("Contexto creado correctamente."),
    ).toBeInTheDocument();
    expect(
      screen.queryByRole("button", {
        name: /renombrar|retirar|reactivar|eliminar|cambiar contexto/i,
      }),
    ).not.toBeInTheDocument();
  });

  it("valida nombre vacío y muestra duplicado normalizado del backend", async () => {
    const user = userEvent.setup();
    render(
      <ContextConfigurationSection
        onUnauthorized={vi.fn()}
        onForbidden={vi.fn()}
      />,
    );
    await screen.findByText("Mesa A");
    const nameInput = screen.getByLabelText("Nombre operacional del Contexto");
    expect(nameInput).toBeRequired();
    await user.click(screen.getByRole("button", { name: "Crear Contexto" }));
    expect(create).not.toHaveBeenCalled();
    await user.type(
      screen.getByLabelText("Nombre operacional del Contexto"),
      "MESA A",
    );
    create.mockRejectedValueOnce(
      new ContextConfigurationError({
        status: 409,
        code: "operational_configuration.context.operational_name_conflict",
      }),
    );
    await user.click(screen.getByRole("button", { name: "Crear Contexto" }));
    expect(
      await screen.findByText("Ya existe un Contexto equivalente."),
    ).toBeInTheDocument();
  });

  it("reintenta creación incierta con idéntico nombre y key", async () => {
    const user = userEvent.setup();
    create
      .mockRejectedValueOnce(new Error("lost response"))
      .mockResolvedValueOnce({ id: "server-id", operationalName: "Barra" });
    render(
      <ContextConfigurationSection
        onUnauthorized={vi.fn()}
        onForbidden={vi.fn()}
      />,
    );
    await screen.findByText("Mesa A");
    await user.type(
      screen.getByLabelText("Nombre operacional del Contexto"),
      "Barra",
    );
    await user.click(screen.getByRole("button", { name: "Crear Contexto" }));
    await user.click(
      await screen.findByRole("button", { name: "Reintentar misma intención" }),
    );
    expect(create.mock.calls[0]).toEqual(create.mock.calls[1]);
  });
});
