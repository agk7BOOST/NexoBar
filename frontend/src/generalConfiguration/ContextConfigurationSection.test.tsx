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
    .mockResolvedValue([
      { id: "ctx-a", operationalName: "Mesa A", isActive: true },
    ]);
  create.mockReset();
  token.mockReset().mockResolvedValue("csrf");
});

describe("ContextConfigurationSection", () => {
  it("keeps a confirmed creation separate from a failed refresh and retries only the GET", async () => {
    list
      .mockResolvedValueOnce([])
      .mockRejectedValueOnce(new Error("read failed"))
      .mockResolvedValueOnce([
        { id: "new", operationalName: "Terraza", isActive: true },
      ]);
    create.mockResolvedValueOnce({
      id: "new",
      operationalName: "Terraza",
      isActive: true,
    });
    const user = userEvent.setup();
    render(
      <ContextConfigurationSection
        onUnauthorized={vi.fn()}
        onForbidden={vi.fn()}
      />,
    );
    await screen.findByText(/Todavía no hay contextos/);
    await user.type(screen.getByLabelText("Nombre del contexto"), "Terraza");
    await user.click(screen.getByRole("button", { name: "Crear contexto" }));
    expect(
      await screen.findByText(
        "Se creó “Terraza”, pero no pudimos actualizar la lista.",
      ),
    ).toBeInTheDocument();
    expect(
      screen.queryByRole("button", { name: "Reintentar esta operación" }),
    ).not.toBeInTheDocument();
    await user.click(
      screen.getByRole("button", { name: "Reintentar consulta" }),
    );
    await screen.findByText("Terraza");
    expect(list).toHaveBeenCalledTimes(3);
    expect(create).toHaveBeenCalledOnce();
  });

  it("does not call a failed initial read an empty list", async () => {
    list.mockRejectedValueOnce(new Error("network"));
    render(
      <ContextConfigurationSection
        onUnauthorized={vi.fn()}
        onForbidden={vi.fn()}
      />,
    );
    await screen.findByText("No pudimos consultar los contextos.");
    expect(
      screen.queryByText(/Todavía no hay contextos/),
    ).not.toBeInTheDocument();
    expect(
      screen.getByRole("button", { name: "Reintentar consulta" }),
    ).toBeEnabled();
  });
  it("lista, crea y recarga de forma autoritativa; ofrece lifecycle administrativo", async () => {
    const user = userEvent.setup();
    create.mockResolvedValue({ id: "server-id", operationalName: "Salón" });
    render(
      <ContextConfigurationSection
        onUnauthorized={vi.fn()}
        onForbidden={vi.fn()}
      />,
    );
    expect(await screen.findByText("Mesa A")).toBeInTheDocument();
    await user.type(screen.getByLabelText("Nombre del contexto"), "Salón");
    await user.click(screen.getByRole("button", { name: "Crear contexto" }));
    await waitFor(() => expect(list).toHaveBeenCalledTimes(2));
    expect(create).toHaveBeenCalledWith(
      { operationalName: "Salón" },
      expect.any(String),
      "csrf",
    );
    expect(screen.getByText("Se creó “Salón”.")).toBeInTheDocument();
    expect(
      screen.getByRole("button", { name: "Cambiar nombre de Mesa A" }),
    ).toBeInTheDocument();
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
    const nameInput = screen.getByLabelText("Nombre del contexto");
    expect(nameInput).toBeRequired();
    await user.click(screen.getByRole("button", { name: "Crear contexto" }));
    expect(create).not.toHaveBeenCalled();
    await user.type(screen.getByLabelText("Nombre del contexto"), "MESA A");
    create.mockRejectedValueOnce(
      new ContextConfigurationError({
        status: 409,
        code: "operational_configuration.context.operational_name_conflict",
      }),
    );
    await user.click(screen.getByRole("button", { name: "Crear contexto" }));
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
    await user.type(screen.getByLabelText("Nombre del contexto"), "Barra");
    await user.click(screen.getByRole("button", { name: "Crear contexto" }));
    await user.click(
      await screen.findByRole("button", { name: "Reintentar esta operación" }),
    );
    expect(create.mock.calls[0]).toEqual(create.mock.calls[1]);
  });
});
