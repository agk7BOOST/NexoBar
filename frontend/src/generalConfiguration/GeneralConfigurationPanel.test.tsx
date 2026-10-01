import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { GeneralConfigurationPanel } from "./GeneralConfigurationPanel.tsx";
import {
  IdentityAdministrationNetworkError,
  IdentityAdministrationProblemError,
  type AdministrativeIdentity,
} from "./identityAdministrationClient.ts";

const {
  activateIdentityMock,
  assignResponsibilityMock,
  createIdentityMock,
  createPreparationResponsibilityMock,
  deactivateIdentityMock,
  deleteIdentityMock,
  getAntiforgeryTokenMock,
  grantPreparationEnablementMock,
  listAdministrativeIdentitiesMock,
  listPreparationResponsibilitiesMock,
  renameIdentityMock,
  revokePreparationEnablementMock,
  revokeResponsibilityMock,
  setLocalCredentialMock,
} = vi.hoisted(() => ({
  activateIdentityMock: vi.fn(),
  assignResponsibilityMock: vi.fn(),
  createIdentityMock: vi.fn(),
  createPreparationResponsibilityMock: vi.fn(),
  deactivateIdentityMock: vi.fn(),
  deleteIdentityMock: vi.fn(),
  getAntiforgeryTokenMock: vi.fn(),
  grantPreparationEnablementMock: vi.fn(),
  listAdministrativeIdentitiesMock: vi.fn(),
  listPreparationResponsibilitiesMock: vi.fn(),
  renameIdentityMock: vi.fn(),
  revokePreparationEnablementMock: vi.fn(),
  revokeResponsibilityMock: vi.fn(),
  setLocalCredentialMock: vi.fn(),
}));

vi.mock("./identityAdministrationClient.ts", async (importOriginal) => {
  const original =
    await importOriginal<typeof import("./identityAdministrationClient.ts")>();
  return {
    ...original,
    activateIdentity: activateIdentityMock,
    assignResponsibility: assignResponsibilityMock,
    createIdentity: createIdentityMock,
    createPreparationResponsibility: createPreparationResponsibilityMock,
    deactivateIdentity: deactivateIdentityMock,
    deleteIdentity: deleteIdentityMock,
    grantPreparationEnablement: grantPreparationEnablementMock,
    listAdministrativeIdentities: listAdministrativeIdentitiesMock,
    listPreparationResponsibilities: listPreparationResponsibilitiesMock,
    renameIdentity: renameIdentityMock,
    revokePreparationEnablement: revokePreparationEnablementMock,
    revokeResponsibility: revokeResponsibilityMock,
    setLocalCredential: setLocalCredentialMock,
  };
});

vi.mock("../identity/sessionClient.ts", async (importOriginal) => {
  const original =
    await importOriginal<typeof import("../identity/sessionClient.ts")>();
  return { ...original, getAntiforgeryToken: getAntiforgeryTokenMock };
});

function identity(
  overrides?: Partial<AdministrativeIdentity>,
): AdministrativeIdentity {
  return {
    identityId: "identity-1",
    operationalName: "Ana",
    isActive: true,
    hasLocalCredential: true,
    loginIdentifier: "ana",
    responsibilities: ["GeneralConfiguration"],
    preparationEnablements: ["destination-1"],
    ...overrides,
  };
}

function renderPanel() {
  const onCurrentIdentityChanged = vi.fn().mockResolvedValue(undefined);
  const onForbidden = vi.fn();
  const onUnauthorized = vi.fn();
  const user = userEvent.setup();
  const result = render(
    <GeneralConfigurationPanel
      currentIdentityId="identity-1"
      onCurrentIdentityChanged={onCurrentIdentityChanged}
      onForbidden={onForbidden}
      onUnauthorized={onUnauthorized}
    />,
  );
  return {
    ...result,
    onCurrentIdentityChanged,
    onForbidden,
    onUnauthorized,
    user,
  };
}

async function selectIdentity(name = "Ana") {
  await userEvent
    .setup()
    .click(await screen.findByRole("button", { name: `Administrar ${name}` }));
  return screen.findByRole("region", { name: `Administrar identidad ${name}` });
}

async function createWithName(
  user: ReturnType<typeof userEvent.setup>,
  name = "Nueva",
) {
  await user.type(screen.getByLabelText("Nombre"), name);
  await user.click(screen.getByRole("button", { name: "Crear identidad" }));
}

describe("GeneralConfigurationPanel", () => {
  beforeEach(() => {
    activateIdentityMock.mockReset();
    assignResponsibilityMock.mockReset();
    createIdentityMock.mockReset();
    createPreparationResponsibilityMock.mockReset();
    deactivateIdentityMock.mockReset();
    deleteIdentityMock.mockReset();
    getAntiforgeryTokenMock.mockReset();
    getAntiforgeryTokenMock.mockResolvedValue("csrf-token");
    grantPreparationEnablementMock.mockReset();
    listAdministrativeIdentitiesMock.mockReset();
    listAdministrativeIdentitiesMock.mockResolvedValue([]);
    listPreparationResponsibilitiesMock.mockReset();
    listPreparationResponsibilitiesMock.mockResolvedValue([]);
    renameIdentityMock.mockReset();
    revokePreparationEnablementMock.mockReset();
    revokeResponsibilityMock.mockReset();
    setLocalCredentialMock.mockReset();
  });

  it("owns the Identity read and renders only the permitted administrative fields", async () => {
    listAdministrativeIdentitiesMock.mockResolvedValueOnce([
      identity({ preparationEnablements: [] }),
      identity({
        identityId: "identity-2",
        operationalName: "Beto",
        isActive: false,
        hasLocalCredential: false,
        loginIdentifier: null,
        preparationEnablements: [],
      }),
    ]);
    renderPanel();

    expect(await screen.findByText("Ana")).toBeInTheDocument();
    expect(screen.getByText("Beto")).toBeInTheDocument();
    const list = screen.getByRole("list", { name: "Listado de identidades" });
    expect(list).toHaveTextContent("Activa · Acceso configurado");
    expect(list).toHaveTextContent("Inactiva · Acceso sin configurar");
    await selectIdentity("Ana");
    expect(screen.getByText("Usuario de acceso: ana")).toBeInTheDocument();
    expect(screen.queryByRole("checkbox")).not.toBeInTheDocument();
    expect(screen.queryByText(/secret|verifier|hash/i)).not.toBeInTheDocument();
    expect(listAdministrativeIdentitiesMock).toHaveBeenCalledOnce();
    expect(listPreparationResponsibilitiesMock).toHaveBeenCalledOnce();
  });

  it.each([true, false])(
    "deletes an eligible %s Identity through a distinct confirmed action and refreshes",
    async (isActive) => {
      const target = identity({
        identityId: "identity-2",
        operationalName: "Beto",
        isActive,
      });
      listAdministrativeIdentitiesMock.mockResolvedValue([identity(), target]);
      deleteIdentityMock.mockResolvedValue(target);
      const { user } = renderPanel();
      await selectIdentity("Beto");
      const detail = await screen.findByRole("region", {
        name: "Administrar identidad Beto",
      });
      expect(
        within(detail).getByRole("button", {
          name: isActive ? "Desactivar Beto" : "Activar Beto",
        }),
      ).toBeInTheDocument();
      await user.click(
        screen.getByRole("button", { name: "Eliminar definitivamente Beto" }),
      );
      expect(
        screen.getByText(/no tiene operaciones cuya atribución/),
      ).toBeInTheDocument();
      expect(deleteIdentityMock).not.toHaveBeenCalled();
      await user.click(
        screen.getByRole("button", {
          name: "Eliminar definitivamente",
        }),
      );
      await waitFor(() => expect(deleteIdentityMock).toHaveBeenCalledOnce());
      expect(deleteIdentityMock.mock.calls[0]?.[0]).toBe("identity-2");
      await waitFor(() =>
        expect(listAdministrativeIdentitiesMock).toHaveBeenCalledTimes(2),
      );
      expect(deactivateIdentityMock).not.toHaveBeenCalled();
    },
  );

  it("explains functional History and last GC path without deactivating", async () => {
    listAdministrativeIdentitiesMock.mockResolvedValue([identity()]);
    deleteIdentityMock
      .mockRejectedValueOnce(
        new IdentityAdministrationProblemError({
          status: 409,
          code: "identities_and_capabilities.functional_history_exists",
        }),
      )
      .mockRejectedValueOnce(
        new IdentityAdministrationProblemError({
          status: 409,
          code: "identities_and_capabilities.last_general_configuration_path",
        }),
      );
    const { user } = renderPanel();
    await selectIdentity("Ana");
    for (const expected of [
      /operaciones registradas a su nombre/,
      /otra vía ordinaria utilizable/,
    ]) {
      await user.click(
        await screen.findByRole("button", {
          name: "Eliminar definitivamente Ana",
        }),
      );
      await user.click(
        screen.getByRole("button", {
          name: "Eliminar definitivamente",
        }),
      );
      expect(await screen.findByText(expected)).toBeInTheDocument();
    }
    expect(deactivateIdentityMock).not.toHaveBeenCalled();
  });

  it("transitions through the ordinary session refresh after self-delete", async () => {
    listAdministrativeIdentitiesMock.mockResolvedValue([identity()]);
    deleteIdentityMock.mockResolvedValue(identity());
    const { user, onCurrentIdentityChanged } = renderPanel();
    await selectIdentity("Ana");
    await user.click(
      await screen.findByRole("button", {
        name: "Eliminar definitivamente Ana",
      }),
    );
    await user.click(
      screen.getByRole("button", { name: "Eliminar definitivamente" }),
    );
    await waitFor(() =>
      expect(onCurrentIdentityChanged).toHaveBeenCalledOnce(),
    );
  });

  it("creates with a UUID-v4 key and reconciles the authoritative response", async () => {
    createIdentityMock.mockResolvedValueOnce(
      identity({
        identityId: "identity-2",
        operationalName: "Nueva",
        isActive: false,
      }),
    );
    const { user } = renderPanel();
    await screen.findByText("No hay identidades.");
    await createWithName(user);

    expect(
      await screen.findByText(/Se creó la identidad “/),
    ).toBeInTheDocument();
    const [request, key, token] = createIdentityMock.mock.calls[0]!;
    expect(request).toEqual({ operationalName: "Nueva" });
    expect(key).toMatch(
      /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i,
    );
    expect(token).toBe("csrf-token");
    expect(
      screen.getByRole("button", { name: "Administrar Nueva" }),
    ).toBeInTheDocument();
  });

  it("orients the new Identity from its returned state without assigning access or enablements", async () => {
    createIdentityMock.mockResolvedValueOnce(
      identity({
        identityId: "identity-2",
        operationalName: "Nueva",
        isActive: false,
        hasLocalCredential: false,
        responsibilities: ["Preparation"],
        preparationEnablements: [],
      }),
    );
    const { user } = renderPanel();
    await screen.findByText("No hay identidades.");
    await createWithName(user);

    const summary = await screen.findByRole("region", {
      name: "Estado de Nueva",
    });
    expect(summary).toHaveTextContent(
      "Inactiva · Acceso todavía sin configurar",
    );
    expect(summary).toHaveTextContent("Preparación");
    expect(summary).toHaveTextContent("habilitación en un destino concreto");
    expect(assignResponsibilityMock).not.toHaveBeenCalled();
    expect(grantPreparationEnablementMock).not.toHaveBeenCalled();
    expect(setLocalCredentialMock).not.toHaveBeenCalled();
  });

  it("lists Preparation Responsibilities separately, creates one, and refreshes the enablement choices", async () => {
    const kitchen = { id: "preparation-1", operationalName: "Cocina" };
    const bar = { id: "preparation-2", operationalName: "Barra" };
    listAdministrativeIdentitiesMock.mockResolvedValueOnce([
      identity({
        preparationEnablements: [],
        responsibilities: ["Preparation"],
      }),
    ]);
    listPreparationResponsibilitiesMock
      .mockResolvedValueOnce([kitchen])
      .mockResolvedValueOnce([kitchen, bar]);
    createPreparationResponsibilityMock.mockResolvedValueOnce(bar);
    const { user } = renderPanel();

    expect(
      await screen.findByRole("heading", {
        name: "Destinos de preparación",
      }),
    ).toBeInTheDocument();
    expect(
      screen.getByLabelText("Listado de destinos de preparación"),
    ).toHaveTextContent("Cocina");
    await user.type(
      screen.getByLabelText("Nombre del destino de preparación"),
      "Barra",
    );
    await user.click(
      screen.getByRole("button", {
        name: "Crear destino de preparación",
      }),
    );

    expect(await screen.findByText(/Se creó el destino “/)).toBeInTheDocument();
    expect(createPreparationResponsibilityMock.mock.calls[0]?.[0]).toEqual({
      operationalName: "Barra",
    });
    expect(createPreparationResponsibilityMock.mock.calls[0]?.[1]).toMatch(
      /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i,
    );
    expect(
      screen.getByLabelText("Listado de destinos de preparación"),
    ).toHaveTextContent("Barra");
    await selectIdentity("Ana");
    expect(
      screen.getByRole("button", { name: "Habilitar destino Barra a Ana" }),
    ).toBeInTheDocument();
  });

  it("rejects a blank name locally and displays a duplicate conflict", async () => {
    const { user } = renderPanel();
    await user.type(
      screen.getByLabelText("Nombre del destino de preparación"),
      " ",
    );
    await user.click(
      screen.getByRole("button", {
        name: "Crear destino de preparación",
      }),
    );
    expect(
      await screen.findByText("Ingresá un nombre válido."),
    ).toBeInTheDocument();
    expect(createPreparationResponsibilityMock).not.toHaveBeenCalled();

    await user.clear(
      screen.getByLabelText("Nombre del destino de preparación"),
    );
    await user.type(
      screen.getByLabelText("Nombre del destino de preparación"),
      "Cocina",
    );
    createPreparationResponsibilityMock.mockRejectedValueOnce(
      new IdentityAdministrationProblemError({ status: 409 }),
    );
    await user.click(
      screen.getByRole("button", {
        name: "Crear destino de preparación",
      }),
    );
    expect(
      await screen.findByText(
        "Ya existe un destino de preparación con ese nombre.",
      ),
    ).toBeInTheDocument();
  });

  it("retries an uncertain Preparation Responsibility creation with the exact intent", async () => {
    listPreparationResponsibilitiesMock.mockResolvedValueOnce([]);
    createPreparationResponsibilityMock
      .mockRejectedValueOnce(new IdentityAdministrationNetworkError())
      .mockResolvedValueOnce({
        id: "preparation-1",
        operationalName: "Cocina",
      });
    const { user } = renderPanel();
    await user.type(
      screen.getByLabelText("Nombre del destino de preparación"),
      "Cocina",
    );
    await user.click(
      screen.getByRole("button", {
        name: "Crear destino de preparación",
      }),
    );
    await screen.findByRole("region", {
      name: "Creación de destino de preparación con resultado no confirmado",
    });
    const firstCall = createPreparationResponsibilityMock.mock.calls[0];
    await user.click(
      screen.getByRole("button", { name: "Reintentar esta operación" }),
    );
    await screen.findByText(/Se creó el destino/);
    expect(createPreparationResponsibilityMock.mock.calls[1]).toEqual(
      firstCall,
    );
  });

  it("retries an uncertain creation with the same key and creates a new key after discard", async () => {
    vi.spyOn(crypto, "randomUUID")
      .mockReturnValueOnce("11111111-1111-4111-8111-111111111111")
      .mockReturnValueOnce("22222222-2222-4222-8222-222222222222");
    createIdentityMock.mockRejectedValueOnce(
      new IdentityAdministrationNetworkError(),
    );
    const { user } = renderPanel();
    await screen.findByText("No hay identidades.");
    await createWithName(user, "Primera");
    await screen.findByRole("region", {
      name: "Creación de identidad con resultado no confirmado",
    });
    const firstCall = createIdentityMock.mock.calls[0];

    createIdentityMock.mockResolvedValueOnce(
      identity({ operationalName: "Primera" }),
    );
    await user.click(
      screen.getByRole("button", { name: "Reintentar esta operación" }),
    );
    await screen.findByText(/Se creó la identidad/);
    expect(createIdentityMock.mock.calls[1]).toEqual(firstCall);

    createIdentityMock.mockRejectedValueOnce(
      new IdentityAdministrationNetworkError(),
    );
    await user.clear(screen.getByLabelText("Nombre"));
    await user.type(screen.getByLabelText("Nombre"), "Segunda");
    await user.click(screen.getByRole("button", { name: "Crear identidad" }));
    await screen.findByRole("region", {
      name: "Creación de identidad con resultado no confirmado",
    });
    await user.click(
      screen.getByRole("button", { name: "Dejar de reintentar" }),
    );
    createIdentityMock.mockResolvedValueOnce(
      identity({ operationalName: "Tercera" }),
    );
    await user.clear(screen.getByLabelText("Nombre"));
    await user.type(screen.getByLabelText("Nombre"), "Tercera");
    await user.click(screen.getByRole("button", { name: "Crear identidad" }));
    await screen.findByText(/Se creó la identidad/);
    expect(createIdentityMock.mock.calls[3]?.[1]).not.toBe(
      createIdentityMock.mock.calls[2]?.[1],
    );
  });

  it("renames through the exact intention and only changes the shown name after success", async () => {
    listAdministrativeIdentitiesMock.mockResolvedValueOnce([identity()]);
    renameIdentityMock.mockResolvedValueOnce(
      identity({ operationalName: "Ana renovada" }),
    );
    const { user } = renderPanel();
    await selectIdentity("Ana");
    await user.click(
      screen.getByRole("button", { name: "Cambiar nombre de Ana" }),
    );
    await user.clear(screen.getByLabelText("Nuevo nombre"));
    await user.type(screen.getByLabelText("Nuevo nombre"), "Ana renovada");
    await user.click(
      screen.getByRole("button", { name: "Confirmar cambio de nombre" }),
    );

    expect(
      await screen.findByRole("button", { name: "Administrar Ana renovada" }),
    ).toBeInTheDocument();
    const [identityId, request, key, token] = renameIdentityMock.mock.calls[0]!;
    expect(identityId).toBe("identity-1");
    expect(request).toEqual({ operationalName: "Ana renovada" });
    expect(key).toMatch(
      /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i,
    );
    expect(token).toBe("csrf-token");
  });

  it("does not treat a failed rename as an authoritative name change", async () => {
    listAdministrativeIdentitiesMock.mockResolvedValueOnce([identity()]);
    renameIdentityMock.mockRejectedValueOnce(
      new IdentityAdministrationProblemError({
        status: 400,
        code: "identities_and_capabilities.invalid_request",
      }),
    );
    const { user } = renderPanel();
    await selectIdentity("Ana");
    await user.click(
      screen.getByRole("button", { name: "Cambiar nombre de Ana" }),
    );
    await user.clear(screen.getByLabelText("Nuevo nombre"));
    await user.type(screen.getByLabelText("Nuevo nombre"), "Nombre fallido");
    await user.click(
      screen.getByRole("button", { name: "Confirmar cambio de nombre" }),
    );

    expect(
      await screen.findByText("Ingresá un nombre válido."),
    ).toBeInTheDocument();
    expect(
      screen.getByRole("button", { name: "Administrar Ana" }),
    ).toBeInTheDocument();
    expect(screen.queryByText("Nombre fallido")).not.toBeInTheDocument();
  });

  it("loads the administrative Preparation Responsibility lookup and resolves enablements by operational name", async () => {
    const kitchen = { id: "preparation-1", operationalName: "Cocina" };
    listAdministrativeIdentitiesMock.mockResolvedValueOnce([
      identity({ preparationEnablements: [kitchen.id], responsibilities: [] }),
    ]);
    listPreparationResponsibilitiesMock.mockResolvedValueOnce([kitchen]);
    renderPanel();
    await selectIdentity("Ana");

    expect(await screen.findByText("Cocina: Habilitada")).toBeInTheDocument();
    expect(screen.queryByText(kitchen.id)).not.toBeInTheDocument();
    expect(listPreparationResponsibilitiesMock).toHaveBeenCalledOnce();
    expect(screen.getByText("Preparación: No asignada")).toBeInTheDocument();
  });

  it("keeps enablements independent from the Preparation Functional Responsibility and exposes unresolved IDs", async () => {
    listAdministrativeIdentitiesMock.mockResolvedValueOnce([
      identity({
        responsibilities: [],
        preparationEnablements: ["missing-preparation"],
      }),
    ]);
    listPreparationResponsibilitiesMock.mockResolvedValueOnce([]);
    renderPanel();
    await selectIdentity("Ana");

    expect(
      await screen.findByText("Preparación: No asignada"),
    ).toBeInTheDocument();
    expect(
      screen.getByText("Destino de preparación desconocido"),
    ).toBeInTheDocument();
    await userEvent.setup().click(screen.getByText("Identificador técnico"));
    expect(screen.getByText("missing-preparation")).toBeInTheDocument();
  });

  it("retains General Configuration error handling for lookup authorization", async () => {
    listPreparationResponsibilitiesMock.mockRejectedValueOnce(
      new IdentityAdministrationProblemError({ status: 401 }),
    );
    const unauthorized = renderPanel();
    await waitFor(() =>
      expect(unauthorized.onUnauthorized).toHaveBeenCalledOnce(),
    );
    unauthorized.unmount();

    listPreparationResponsibilitiesMock.mockReset();
    listPreparationResponsibilitiesMock.mockRejectedValueOnce(
      new IdentityAdministrationProblemError({ status: 403 }),
    );
    const forbidden = renderPanel();
    await waitFor(() => expect(forbidden.onForbidden).toHaveBeenCalledOnce());
    expect(
      screen.queryByRole("heading", { name: "Configuración general" }),
    ).not.toBeInTheDocument();
  });

  it("fences a late Preparation Responsibility lookup after panel replacement", async () => {
    let resolveOldLookup:
      ((items: { id: string; operationalName: string }[]) => void) | undefined;
    const oldLookup = new Promise<{ id: string; operationalName: string }[]>(
      (resolve) => {
        resolveOldLookup = resolve;
      },
    );
    listPreparationResponsibilitiesMock
      .mockReturnValueOnce(oldLookup)
      .mockResolvedValueOnce([
        { id: "preparation-2", operationalName: "Barra" },
      ]);
    listAdministrativeIdentitiesMock.mockResolvedValue([
      identity({
        preparationEnablements: [],
        responsibilities: ["Preparation"],
      }),
    ]);
    const { rerender } = render(
      <GeneralConfigurationPanel
        key="old"
        currentIdentityId="identity-1"
        onCurrentIdentityChanged={vi.fn().mockResolvedValue(undefined)}
        onForbidden={vi.fn()}
        onUnauthorized={vi.fn()}
      />,
    );
    rerender(
      <GeneralConfigurationPanel
        key="new"
        currentIdentityId="identity-2"
        onCurrentIdentityChanged={vi.fn().mockResolvedValue(undefined)}
        onForbidden={vi.fn()}
        onUnauthorized={vi.fn()}
      />,
    );

    await selectIdentity("Ana");
    await screen.findByText("Barra: No habilitada");
    resolveOldLookup?.([
      { id: "preparation-1", operationalName: "Cocina anterior" },
    ]);
    await waitFor(() =>
      expect(
        screen.queryByText("Cocina anterior: No habilitada"),
      ).not.toBeInTheDocument(),
    );
  });

  it("grants enablement with a UUID key and reconciles only the authoritative Identity response", async () => {
    const kitchen = { id: "preparation-1", operationalName: "Cocina" };
    listAdministrativeIdentitiesMock.mockResolvedValueOnce([
      identity({
        responsibilities: ["Preparation"],
        preparationEnablements: [],
      }),
    ]);
    listPreparationResponsibilitiesMock.mockResolvedValueOnce([kitchen]);
    grantPreparationEnablementMock.mockResolvedValueOnce(
      identity({
        responsibilities: ["Preparation"],
        preparationEnablements: [kitchen.id],
      }),
    );
    const { onCurrentIdentityChanged, user } = renderPanel();
    await selectIdentity("Ana");

    await screen.findByText("Cocina: No habilitada");
    await user.click(
      screen.getByRole("button", { name: "Habilitar destino Cocina a Ana" }),
    );

    expect(await screen.findByText("Cocina: Habilitada")).toBeInTheDocument();
    const [identityId, responsibilityId, key, token] =
      grantPreparationEnablementMock.mock.calls[0]!;
    expect(identityId).toBe("identity-1");
    expect(responsibilityId).toBe(kitchen.id);
    expect(key).toMatch(
      /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i,
    );
    expect(token).toBe("csrf-token");
    expect(screen.getByText("Preparación: Asignada")).toBeInTheDocument();
    expect(onCurrentIdentityChanged).not.toHaveBeenCalled();
  });

  it("reuses an uncertain enablement key and creates a new key for a changed grant", async () => {
    vi.spyOn(crypto, "randomUUID")
      .mockReturnValueOnce("11111111-1111-4111-8111-111111111111")
      .mockReturnValueOnce("22222222-2222-4222-8222-222222222222");
    const kitchen = { id: "preparation-1", operationalName: "Cocina" };
    const bar = { id: "preparation-2", operationalName: "Barra" };
    listAdministrativeIdentitiesMock.mockResolvedValueOnce([
      identity({
        responsibilities: ["Preparation"],
        preparationEnablements: [],
      }),
    ]);
    listPreparationResponsibilitiesMock.mockResolvedValueOnce([kitchen, bar]);
    grantPreparationEnablementMock
      .mockRejectedValueOnce(new IdentityAdministrationNetworkError())
      .mockResolvedValueOnce(
        identity({
          responsibilities: ["Preparation"],
          preparationEnablements: [kitchen.id],
        }),
      )
      .mockRejectedValueOnce(new IdentityAdministrationNetworkError())
      .mockResolvedValueOnce(
        identity({
          responsibilities: ["Preparation"],
          preparationEnablements: [bar.id],
        }),
      );
    const { user } = renderPanel();
    await selectIdentity("Ana");

    await screen.findByText("Cocina: No habilitada");
    await user.click(
      screen.getByRole("button", { name: "Habilitar destino Cocina a Ana" }),
    );
    await screen.findByRole("region", {
      name: "Habilitación de preparación con resultado no confirmado",
    });
    const firstCall = grantPreparationEnablementMock.mock.calls[0];
    await user.click(
      screen.getByRole("button", { name: "Reintentar esta operación" }),
    );
    await screen.findByText("Cocina: Habilitada");
    expect(grantPreparationEnablementMock.mock.calls[1]).toEqual(firstCall);

    await user.click(
      screen.getByRole("button", { name: "Habilitar destino Barra a Ana" }),
    );
    await screen.findByRole("region", {
      name: "Habilitación de preparación con resultado no confirmado",
    });
    await user.click(
      screen.getByRole("button", { name: "Dejar de reintentar" }),
    );
    await user.click(
      screen.getByRole("button", { name: "Habilitar destino Barra a Ana" }),
    );
    expect(grantPreparationEnablementMock.mock.calls[2]?.[2]).not.toBe(
      grantPreparationEnablementMock.mock.calls[3]?.[2],
    );
  });

  it("revokes an enablement only from an authoritative response and retains it after failure", async () => {
    const kitchen = { id: "preparation-1", operationalName: "Cocina" };
    listAdministrativeIdentitiesMock.mockResolvedValueOnce([
      identity({
        responsibilities: ["Preparation"],
        preparationEnablements: [kitchen.id],
      }),
    ]);
    listPreparationResponsibilitiesMock.mockResolvedValueOnce([kitchen]);
    revokePreparationEnablementMock.mockRejectedValueOnce(
      new IdentityAdministrationProblemError({ status: 400 }),
    );
    const { user } = renderPanel();
    await selectIdentity("Ana");

    await screen.findByText("Cocina: Habilitada");
    await user.click(
      screen.getByRole("button", { name: "Quitar habilitación Cocina a Ana" }),
    );
    expect(
      await screen.findByText(
        "El destino de preparación indicado no es válido.",
      ),
    ).toBeInTheDocument();
    expect(screen.getByText("Cocina: Habilitada")).toBeInTheDocument();

    revokePreparationEnablementMock.mockResolvedValueOnce(
      identity({
        responsibilities: ["Preparation"],
        preparationEnablements: [],
      }),
    );
    await user.click(
      screen.getByRole("button", { name: "Quitar habilitación Cocina a Ana" }),
    );
    expect(
      await screen.findByText("Cocina: No habilitada"),
    ).toBeInTheDocument();
  });

  it("does not couple Preparation responsibility assignment or revocation to enablements", async () => {
    const kitchen = { id: "preparation-1", operationalName: "Cocina" };
    listAdministrativeIdentitiesMock.mockResolvedValueOnce([
      identity({
        responsibilities: ["GeneralConfiguration", "Preparation"],
        preparationEnablements: [kitchen.id],
      }),
    ]);
    listPreparationResponsibilitiesMock.mockResolvedValueOnce([kitchen]);
    revokeResponsibilityMock.mockResolvedValueOnce(
      identity({
        responsibilities: ["GeneralConfiguration"],
        preparationEnablements: [kitchen.id],
      }),
    );
    assignResponsibilityMock.mockResolvedValueOnce(
      identity({
        responsibilities: ["GeneralConfiguration", "Preparation"],
        preparationEnablements: [kitchen.id],
      }),
    );
    const { user } = renderPanel();
    await selectIdentity("Ana");

    await screen.findByText("Cocina: Habilitada");
    await user.click(
      screen.getByRole("button", { name: "Quitar Preparación a Ana" }),
    );
    expect(
      await screen.findByText("Preparación: No asignada"),
    ).toBeInTheDocument();
    expect(screen.getByText("Cocina: Habilitada")).toBeInTheDocument();

    await user.click(
      screen.getByRole("button", { name: "Asignar Preparación a Ana" }),
    );
    expect(
      await screen.findByText("Preparación: Asignada"),
    ).toBeInTheDocument();
    expect(screen.getByText("Cocina: Habilitada")).toBeInTheDocument();
  });

  it("reuses the exact key for an uncertain enablement revocation", async () => {
    const kitchen = { id: "preparation-1", operationalName: "Cocina" };
    listAdministrativeIdentitiesMock.mockResolvedValueOnce([
      identity({
        responsibilities: ["Preparation"],
        preparationEnablements: [kitchen.id],
      }),
    ]);
    listPreparationResponsibilitiesMock.mockResolvedValueOnce([kitchen]);
    revokePreparationEnablementMock
      .mockRejectedValueOnce(new IdentityAdministrationNetworkError())
      .mockResolvedValueOnce(
        identity({
          responsibilities: ["Preparation"],
          preparationEnablements: [],
        }),
      );
    const { user } = renderPanel();
    await selectIdentity("Ana");

    await screen.findByText("Cocina: Habilitada");
    await user.click(
      screen.getByRole("button", { name: "Quitar habilitación Cocina a Ana" }),
    );
    await screen.findByRole("region", {
      name: "Habilitación de preparación con resultado no confirmado",
    });
    const firstCall = revokePreparationEnablementMock.mock.calls[0];
    await user.click(
      screen.getByRole("button", { name: "Reintentar esta operación" }),
    );

    await screen.findByText("Cocina: No habilitada");
    expect(revokePreparationEnablementMock.mock.calls[1]).toEqual(firstCall);
  });

  it("renders the fixed closed responsibility set and reconciles activation from the response", async () => {
    listAdministrativeIdentitiesMock.mockResolvedValueOnce([
      identity({ isActive: false, responsibilities: [] }),
    ]);
    activateIdentityMock.mockResolvedValueOnce(identity({ isActive: true }));
    const { user } = renderPanel();
    await selectIdentity("Ana");

    await screen.findByText("Pedidos y cierre básico: No asignada");
    for (const responsibility of [
      "Pedidos y cierre básico",
      "Intervención operacional",
      "Preparación",
      "Configuración de productos",
      "Operación de inventario",
      "Configuración de inventario",
      "Configuración general",
    ]) {
      expect(
        screen.getByText(`${responsibility}: No asignada`),
      ).toBeInTheDocument();
    }
    await user.click(screen.getByRole("button", { name: "Activar Ana" }));

    expect(await screen.findByText("Activa")).toBeInTheDocument();
    const [identityId, key, token] = activateIdentityMock.mock.calls[0]!;
    expect(identityId).toBe("identity-1");
    expect(key).toMatch(
      /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i,
    );
    expect(token).toBe("csrf-token");
  });

  it("reconciles assignment and revocation only from their authoritative responses", async () => {
    listAdministrativeIdentitiesMock.mockResolvedValueOnce([
      identity({ responsibilities: [] }),
    ]);
    assignResponsibilityMock.mockResolvedValueOnce(
      identity({ responsibilities: ["Preparation"] }),
    );
    revokeResponsibilityMock.mockResolvedValueOnce(
      identity({ responsibilities: [] }),
    );
    const { user } = renderPanel();
    await selectIdentity("Ana");

    await screen.findByText("Preparación: No asignada");
    await user.click(
      screen.getByRole("button", { name: "Asignar Preparación a Ana" }),
    );
    expect(
      await screen.findByText("Preparación: Asignada"),
    ).toBeInTheDocument();
    expect(assignResponsibilityMock.mock.calls[0]?.slice(0, 2)).toEqual([
      "identity-1",
      "Preparation",
    ]);

    await user.click(
      screen.getByRole("button", { name: "Quitar Preparación a Ana" }),
    );
    expect(
      await screen.findByText("Preparación: No asignada"),
    ).toBeInTheDocument();
    expect(revokeResponsibilityMock.mock.calls[0]?.slice(0, 2)).toEqual([
      "identity-1",
      "Preparation",
    ]);
  });

  it("does not fabricate state after a last GeneralConfiguration path rejection", async () => {
    listAdministrativeIdentitiesMock.mockResolvedValueOnce([
      identity({ responsibilities: ["GeneralConfiguration"] }),
    ]);
    revokeResponsibilityMock.mockRejectedValueOnce(
      new IdentityAdministrationProblemError({
        status: 409,
        code: "identities_and_capabilities.last_general_configuration_path",
      }),
    );
    const { onCurrentIdentityChanged, user } = renderPanel();
    await selectIdentity("Ana");

    await screen.findByText("Configuración general: Asignada");
    await user.click(
      screen.getByRole("button", {
        name: "Quitar Configuración general a Ana",
      }),
    );

    expect(
      await screen.findByText(
        "Debe permanecer al menos una vía administrativa utilizable.",
      ),
    ).toBeInTheDocument();
    expect(
      screen.getByText("Configuración general: Asignada"),
    ).toBeInTheDocument();
    expect(onCurrentIdentityChanged).not.toHaveBeenCalled();
  });

  it("does not fabricate an inactive Identity after a safeguard rejection", async () => {
    listAdministrativeIdentitiesMock.mockResolvedValueOnce([identity()]);
    deactivateIdentityMock.mockRejectedValueOnce(
      new IdentityAdministrationProblemError({
        status: 409,
        code: "identities_and_capabilities.last_general_configuration_path",
      }),
    );
    const { onCurrentIdentityChanged, user } = renderPanel();
    await selectIdentity("Ana");

    await user.click(screen.getByRole("button", { name: "Desactivar Ana" }));
    expect(deactivateIdentityMock).not.toHaveBeenCalled();
    await user.click(
      screen.getByRole("button", { name: "Confirmar desactivación" }),
    );

    expect(
      await screen.findByText(
        "Debe permanecer al menos una vía administrativa utilizable.",
      ),
    ).toBeInTheDocument();
    expect(screen.getByText("Activa")).toBeInTheDocument();
    expect(onCurrentIdentityChanged).not.toHaveBeenCalled();
  });

  it("retries an uncertain activation with the exact same key", async () => {
    vi.spyOn(crypto, "randomUUID").mockReturnValue(
      "11111111-1111-4111-8111-111111111111",
    );
    listAdministrativeIdentitiesMock.mockResolvedValueOnce([
      identity({ isActive: false }),
    ]);
    activateIdentityMock.mockRejectedValueOnce(
      new IdentityAdministrationNetworkError(),
    );
    const { user } = renderPanel();
    await selectIdentity("Ana");

    await user.click(screen.getByRole("button", { name: "Activar Ana" }));
    await screen.findByRole("region", {
      name: "Actualización de identidad con resultado no confirmado",
    });
    const firstCall = activateIdentityMock.mock.calls[0];
    activateIdentityMock.mockResolvedValueOnce(identity({ isActive: true }));
    await user.click(
      screen.getByRole("button", { name: "Reintentar esta operación" }),
    );

    await screen.findByText("Activa");
    expect(activateIdentityMock.mock.calls[1]).toEqual(firstCall);
  });

  it("does not fabricate activation after a failed command", async () => {
    listAdministrativeIdentitiesMock.mockResolvedValueOnce([
      identity({ isActive: false }),
    ]);
    activateIdentityMock.mockRejectedValueOnce(
      new IdentityAdministrationProblemError({ status: 400 }),
    );
    const { user } = renderPanel();
    await selectIdentity("Ana");

    await screen.findByText("Inactiva");
    await user.click(screen.getByRole("button", { name: "Activar Ana" }));

    expect(
      await screen.findByText(
        "No se pudo actualizar la identidad. Revisá los datos e intentá nuevamente.",
      ),
    ).toBeInTheDocument();
    expect(screen.getByText("Inactiva")).toBeInTheDocument();
  });

  it("uses a new key for a changed responsibility intention and does not refresh another actor", async () => {
    vi.spyOn(crypto, "randomUUID")
      .mockReturnValueOnce("11111111-1111-4111-8111-111111111111")
      .mockReturnValueOnce("22222222-2222-4222-8222-222222222222");
    listAdministrativeIdentitiesMock.mockResolvedValueOnce([
      identity({
        identityId: "identity-2",
        operationalName: "Beto",
        responsibilities: [],
      }),
      identity({ responsibilities: ["GeneralConfiguration"] }),
    ]);
    assignResponsibilityMock
      .mockRejectedValueOnce(new IdentityAdministrationNetworkError())
      .mockResolvedValueOnce(
        identity({
          identityId: "identity-2",
          operationalName: "Beto",
          responsibilities: ["CatalogConfiguration"],
        }),
      );
    const { onCurrentIdentityChanged, user } = renderPanel();
    await selectIdentity("Beto");

    await user.click(
      screen.getByRole("button", { name: "Asignar Preparación a Beto" }),
    );
    await screen.findByRole("region", {
      name: "Actualización de identidad con resultado no confirmado",
    });
    await user.click(
      screen.getByRole("button", { name: "Dejar de reintentar" }),
    );
    await user.click(
      screen.getByRole("button", {
        name: "Asignar Configuración de productos a Beto",
      }),
    );

    expect(
      await screen.findByText("Configuración de productos: Asignada"),
    ).toBeInTheDocument();
    expect(assignResponsibilityMock.mock.calls[0]?.[2]).not.toBe(
      assignResponsibilityMock.mock.calls[1]?.[2],
    );
    expect(onCurrentIdentityChanged).not.toHaveBeenCalled();
    await selectIdentity("Ana");
    expect(
      screen.getByText("Configuración general: Asignada"),
    ).toBeInTheDocument();
  });

  it("refreshes the current Identity after a successful self responsibility change", async () => {
    listAdministrativeIdentitiesMock.mockResolvedValueOnce([
      identity({
        responsibilities: ["GeneralConfiguration", "CatalogConfiguration"],
      }),
    ]);
    revokeResponsibilityMock.mockResolvedValueOnce(
      identity({ responsibilities: ["CatalogConfiguration"] }),
    );
    const { onCurrentIdentityChanged, user } = renderPanel();
    await selectIdentity("Ana");

    await screen.findByText("Configuración general: Asignada");
    await user.click(
      screen.getByRole("button", {
        name: "Quitar Configuración general a Ana",
      }),
    );

    await waitFor(() =>
      expect(onCurrentIdentityChanged).toHaveBeenCalledOnce(),
    );
    expect(
      screen.getByText("Configuración general: No asignada"),
    ).toBeInTheDocument();
  });

  it("reconciles successful self-deactivation before refreshing the current session", async () => {
    listAdministrativeIdentitiesMock.mockResolvedValueOnce([identity()]);
    deactivateIdentityMock.mockResolvedValueOnce(identity({ isActive: false }));
    const { onCurrentIdentityChanged, user } = renderPanel();
    await selectIdentity("Ana");

    await user.click(screen.getByRole("button", { name: "Desactivar Ana" }));
    await user.click(
      screen.getByRole("button", { name: "Confirmar desactivación" }),
    );

    expect(await screen.findByText("Inactiva")).toBeInTheDocument();
    await waitFor(() =>
      expect(onCurrentIdentityChanged).toHaveBeenCalledOnce(),
    );
  });

  it("replaces another Identity credential without refreshing the current session and clears the secret", async () => {
    listAdministrativeIdentitiesMock.mockResolvedValueOnce([
      identity({
        identityId: "identity-2",
        operationalName: "Beto",
        loginIdentifier: "beto",
      }),
      identity(),
    ]);
    setLocalCredentialMock.mockResolvedValueOnce(
      identity({
        identityId: "identity-2",
        operationalName: "Beto",
        loginIdentifier: "beto",
      }),
    );
    const { onCurrentIdentityChanged, user } = renderPanel();
    await selectIdentity("Beto");
    await user.click(
      screen.getByRole("button", { name: "Cambiar acceso de Beto" }),
    );
    expect(
      screen.getByRole("form", { name: "Cambiar acceso de Beto" }),
    ).toHaveTextContent("Usuario de acceso actual: beto");
    expect(
      screen.getByText(/se revocarán todas las sesiones de esta persona/),
    ).toBeVisible();
    await user.type(screen.getByLabelText("Nueva contraseña"), "secret-new");
    await user.click(screen.getByRole("button", { name: "Guardar acceso" }));

    await screen.findByText(/Se actualizó el acceso de/);
    expect(setLocalCredentialMock.mock.calls[0]?.[1]).toEqual({
      secret: "secret-new",
    });
    expect(onCurrentIdentityChanged).not.toHaveBeenCalled();
    expect(screen.queryByLabelText("Nueva contraseña")).not.toBeInTheDocument();
  });

  it("requires an explicit login identifier for credentialless setup and returns to login after self success", async () => {
    listAdministrativeIdentitiesMock.mockResolvedValueOnce([
      identity({ hasLocalCredential: false, loginIdentifier: null }),
    ]);
    const { onUnauthorized, user } = renderPanel();
    await selectIdentity("Ana");
    await user.click(
      screen.getByRole("button", { name: "Configurar acceso de Ana" }),
    );
    expect(
      screen.getByRole("form", { name: "Configurar acceso de Ana" }),
    ).toBeVisible();
    await user.type(screen.getByLabelText("Nueva contraseña"), "secret-new");
    await user.click(screen.getByRole("button", { name: "Guardar acceso" }));
    expect(
      await screen.findByText("Ingresá un usuario de acceso."),
    ).toBeInTheDocument();

    await user.type(screen.getByLabelText("Usuario de acceso"), "ana");
    setLocalCredentialMock.mockResolvedValueOnce(
      identity({ hasLocalCredential: true, loginIdentifier: "ana" }),
    );
    await user.click(screen.getByRole("button", { name: "Guardar acceso" }));
    await waitFor(() => expect(onUnauthorized).toHaveBeenCalledOnce());
    expect(setLocalCredentialMock.mock.calls[0]?.[1]).toEqual({
      loginIdentifier: "ana",
      secret: "secret-new",
    });
  });

  it("retires stale state after 403 and fences a late read after owner replacement", async () => {
    listAdministrativeIdentitiesMock.mockRejectedValueOnce(
      new IdentityAdministrationProblemError({ status: 403 }),
    );
    const first = renderPanel();
    await waitFor(() => expect(first.onForbidden).toHaveBeenCalledOnce());
    expect(
      screen.queryByRole("heading", { name: "Configuración general" }),
    ).not.toBeInTheDocument();

    let resolveOldRead:
      ((identities: AdministrativeIdentity[]) => void) | undefined;
    const oldRead = new Promise<AdministrativeIdentity[]>((resolve) => {
      resolveOldRead = resolve;
    });
    listAdministrativeIdentitiesMock.mockReset();
    listAdministrativeIdentitiesMock
      .mockReturnValueOnce(oldRead)
      .mockResolvedValueOnce([identity({ operationalName: "Beto" })]);
    const { rerender } = render(
      <GeneralConfigurationPanel
        key="old"
        currentIdentityId="identity-1"
        onCurrentIdentityChanged={vi.fn().mockResolvedValue(undefined)}
        onForbidden={vi.fn()}
        onUnauthorized={vi.fn()}
      />,
    );
    rerender(
      <GeneralConfigurationPanel
        key="new"
        currentIdentityId="identity-1"
        onCurrentIdentityChanged={vi.fn().mockResolvedValue(undefined)}
        onForbidden={vi.fn()}
        onUnauthorized={vi.fn()}
      />,
    );
    expect(await screen.findByText("Beto")).toBeInTheDocument();
    resolveOldRead?.([identity({ operationalName: "Ana anterior" })]);
    await waitFor(() =>
      expect(screen.queryByText("Ana anterior")).not.toBeInTheDocument(),
    );
  });
});
