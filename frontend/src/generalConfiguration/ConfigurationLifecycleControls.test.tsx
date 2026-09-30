import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, expect, it, vi } from "vitest";
import { ConfigurationLifecycleControls } from "./ConfigurationLifecycleControls.tsx";
import {
  ConfigurationLifecycleError,
  type ConfigurationEntity,
} from "./configurationLifecycleClient.ts";
const { execute, token } = vi.hoisted(() => ({
  execute: vi.fn(),
  token: vi.fn(),
}));
vi.mock("./configurationLifecycleClient.ts", async (importOriginal) => ({
  ...(await importOriginal<
    typeof import("./configurationLifecycleClient.ts")
  >()),
  executeConfigurationLifecycle: execute,
}));
vi.mock("../identity/sessionClient.ts", async (importOriginal) => ({
  ...(await importOriginal<typeof import("../identity/sessionClient.ts")>()),
  getAntiforgeryToken: token,
}));
beforeEach(() => {
  execute.mockReset().mockResolvedValue({
    id: "target",
    operationalName: "Kitchen",
    isActive: false,
    isDeleted: false,
  });
  token.mockReset().mockResolvedValue("csrf");
});
function setup(entity: ConfigurationEntity = "contexts", active = true) {
  const reload = vi.fn().mockResolvedValue(undefined),
    unauthorized = vi.fn(),
    forbidden = vi.fn(),
    pending = vi.fn();
  render(
    <ConfigurationLifecycleControls
      entity={entity}
      item={{ id: "target", operationalName: "Kitchen", isActive: active }}
      disabled={false}
      onReload={reload}
      onUnauthorized={unauthorized}
      onForbidden={forbidden}
      onPendingChange={pending}
    />,
  );
  return { user: userEvent.setup(), reload, unauthorized, forbidden, pending };
}
it.each(["contexts", "preparation-responsibilities"] as const)(
  "renames %s with observed State and authoritative reload",
  async (entity) => {
    const { user, reload } = setup(entity);
    await user.click(screen.getByRole("button", { name: "Cambiar nombre" }));
    const input = screen.getByRole("textbox");
    await user.clear(input);
    await user.type(input, "New Kitchen");
    await user.click(
      screen.getByRole("button", { name: "Confirmar cambio de nombre" }),
    );
    await waitFor(() => expect(reload).toHaveBeenCalledOnce());
    expect(execute).toHaveBeenCalledWith(
      entity,
      "target",
      "rename",
      {
        expectedCurrentOperationalName: "Kitchen",
        expectedIsActive: true,
        newOperationalName: "New Kitchen",
      },
      expect.any(String),
      "csrf",
    );
  },
);
it.each(["contexts", "preparation-responsibilities"] as const)(
  "retires %s prospectively after inline confirmation",
  async (entity) => {
    const { user, reload } = setup(entity);
    expect(screen.getByText("Activo")).toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: "Retirar" }));
    expect(
      screen.getByText(
        entity === "contexts"
          ? /Los pedidos que ya lo usan no se modificarán/
          : /El trabajo ya originado conservará este destino/,
      ),
    ).toBeInTheDocument();
    expect(execute).not.toHaveBeenCalled();
    await user.click(screen.getByRole("button", { name: "Confirmar retiro" }));
    await waitFor(() => expect(reload).toHaveBeenCalledOnce());
    expect(execute.mock.calls[0][2]).toBe("retire");
  },
);
it("reactivates a retired item and reloads", async () => {
  const { user, reload } = setup("preparation-responsibilities", false);
  expect(screen.getByText("Retirado")).toBeInTheDocument();
  await user.click(screen.getByRole("button", { name: "Reactivar" }));
  await waitFor(() => expect(reload).toHaveBeenCalledOnce());
  expect(execute.mock.calls[0][2]).toBe("reactivate");
});
it.each([true, false])(
  "deletes an eligible item, active=%s, with separate consequence",
  async (active) => {
    const { user, reload } = setup("contexts", active);
    await user.click(
      screen.getByRole("button", { name: "Eliminar definitivamente" }),
    );
    expect(screen.getByText(/no se puede deshacer/)).toBeInTheDocument();
    await user.click(
      screen.getByRole("button", { name: "Confirmar eliminación definitiva" }),
    );
    await waitFor(() => expect(reload).toHaveBeenCalledOnce());
    expect(execute.mock.calls[0][2]).toBe("delete");
  },
);
it("explains backend dependencies without calculating eligibility", async () => {
  execute.mockRejectedValueOnce(
    new ConfigurationLifecycleError(
      409,
      "operational_configuration.preparation_responsibility.retire.active_products",
      "Cambiá primero su configuración de preparación.",
    ),
  );
  const { user, reload } = setup("preparation-responsibilities");
  await user.click(screen.getByRole("button", { name: "Retirar" }));
  await user.click(screen.getByRole("button", { name: "Confirmar retiro" }));
  expect(
    await screen.findByText("Cambiá primero su configuración de preparación."),
  ).toBeInTheDocument();
  expect(reload).toHaveBeenCalledOnce();
});
it.each([new Error("network"), new ConfigurationLifecycleError(503)])(
  "preserves exactly the uncertain command for explicit retry",
  async (error) => {
    execute.mockRejectedValueOnce(error);
    const { user, reload } = setup();
    await user.click(screen.getByRole("button", { name: "Retirar" }));
    await user.click(screen.getByRole("button", { name: "Confirmar retiro" }));
    expect(reload).not.toHaveBeenCalled();
    expect(
      screen.getByRole("button", { name: "Cambiar nombre" }),
    ).toBeDisabled();
    const original = execute.mock.calls[0];
    await user.click(
      await screen.findByRole("button", { name: "Reintentar esta operación" }),
    );
    await waitFor(() => expect(reload).toHaveBeenCalledOnce());
    expect(execute.mock.calls[1]).toEqual(original);
    expect(token).toHaveBeenCalledOnce();
  },
);
it.each([401, 403])("reconciles authority rejection %s", async (status) => {
  execute.mockRejectedValueOnce(new ConfigurationLifecycleError(status));
  const { user, unauthorized, forbidden } = setup("contexts", false);
  await user.click(screen.getByRole("button", { name: "Reactivar" }));
  await waitFor(() =>
    expect(status === 401 ? unauthorized : forbidden).toHaveBeenCalledOnce(),
  );
});

it("keeps a committed mutation confirmed when authoritative reload fails", async () => {
  const { user, reload } = setup("contexts", false);
  reload.mockRejectedValueOnce(new Error("Read failed"));
  await user.click(screen.getByRole("button", { name: "Reactivar" }));
  expect(
    await screen.findByText(
      /Operación confirmada. No se pudo actualizar la lista/,
    ),
  ).toBeInTheDocument();
  expect(
    screen.queryByRole("button", { name: "Reintentar esta operación" }),
  ).not.toBeInTheDocument();
  expect(screen.getByRole("button", { name: "Cambiar nombre" })).toBeDisabled();
  await user.click(screen.getByRole("button", { name: "Actualizar lista" }));
  await waitFor(() =>
    expect(
      screen.getByRole("button", { name: "Cambiar nombre" }),
    ).toBeEnabled(),
  );
  expect(execute).toHaveBeenCalledOnce();
});
