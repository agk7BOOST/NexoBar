import { render, screen, waitFor } from "@testing-library/react";
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
  deactivateIdentityMock,
  getAntiforgeryTokenMock,
  listAdministrativeIdentitiesMock,
  renameIdentityMock,
  revokeResponsibilityMock,
} = vi.hoisted(() => ({
  activateIdentityMock: vi.fn(),
  assignResponsibilityMock: vi.fn(),
  createIdentityMock: vi.fn(),
  deactivateIdentityMock: vi.fn(),
  getAntiforgeryTokenMock: vi.fn(),
  listAdministrativeIdentitiesMock: vi.fn(),
  renameIdentityMock: vi.fn(),
  revokeResponsibilityMock: vi.fn(),
}));

vi.mock("./identityAdministrationClient.ts", async (importOriginal) => {
  const original = await importOriginal<
    typeof import("./identityAdministrationClient.ts")
  >();
  return {
    ...original,
    activateIdentity: activateIdentityMock,
    assignResponsibility: assignResponsibilityMock,
    createIdentity: createIdentityMock,
    deactivateIdentity: deactivateIdentityMock,
    listAdministrativeIdentities: listAdministrativeIdentitiesMock,
    renameIdentity: renameIdentityMock,
    revokeResponsibility: revokeResponsibilityMock,
  };
});

vi.mock("../identity/sessionClient.ts", async (importOriginal) => {
  const original = await importOriginal<
    typeof import("../identity/sessionClient.ts")
  >();
  return { ...original, getAntiforgeryToken: getAntiforgeryTokenMock };
});

function identity(overrides?: Partial<AdministrativeIdentity>): AdministrativeIdentity {
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
  return { ...result, onCurrentIdentityChanged, onForbidden, onUnauthorized, user };
}

async function createWithName(
  user: ReturnType<typeof userEvent.setup>,
  name = "Nueva",
) {
  await user.type(screen.getByLabelText("Nombre operacional"), name);
  await user.click(screen.getByRole("button", { name: "Crear Identity" }));
}

describe("GeneralConfigurationPanel", () => {
  beforeEach(() => {
    activateIdentityMock.mockReset();
    assignResponsibilityMock.mockReset();
    createIdentityMock.mockReset();
    deactivateIdentityMock.mockReset();
    getAntiforgeryTokenMock.mockReset();
    getAntiforgeryTokenMock.mockResolvedValue("csrf-token");
    listAdministrativeIdentitiesMock.mockReset();
    listAdministrativeIdentitiesMock.mockResolvedValue([]);
    renameIdentityMock.mockReset();
    revokeResponsibilityMock.mockReset();
  });

  it("owns the Identity read and renders only the permitted administrative fields", async () => {
    listAdministrativeIdentitiesMock.mockResolvedValueOnce([
      identity(),
      identity({
        identityId: "identity-2",
        operationalName: "Beto",
        isActive: false,
        hasLocalCredential: false,
        loginIdentifier: null,
      }),
    ]);
    renderPanel();

    expect(await screen.findByText("Ana")).toBeInTheDocument();
    expect(screen.getByText("Beto")).toBeInTheDocument();
    expect(screen.getByText("Activa")).toBeInTheDocument();
    expect(screen.getByText("Inactiva")).toBeInTheDocument();
    expect(screen.getByText("Configurada")).toBeInTheDocument();
    expect(screen.getByText("No configurada")).toBeInTheDocument();
    expect(screen.getByText("ana")).toBeInTheDocument();
    expect(screen.queryByText("destination-1")).not.toBeInTheDocument();
    expect(screen.queryByRole("checkbox")).not.toBeInTheDocument();
    expect(screen.queryByText(/secret|verifier|hash/i)).not.toBeInTheDocument();
    expect(listAdministrativeIdentitiesMock).toHaveBeenCalledOnce();
  });

  it("creates with a UUID-v4 key and reconciles the authoritative response", async () => {
    createIdentityMock.mockResolvedValueOnce(
      identity({ identityId: "identity-2", operationalName: "Nueva", isActive: false }),
    );
    const { user } = renderPanel();
    await screen.findByText("No hay Identities.");
    await createWithName(user);

    expect(await screen.findByText("Identity creada correctamente.")).toBeInTheDocument();
    const [request, key, token] = createIdentityMock.mock.calls[0]!;
    expect(request).toEqual({ operationalName: "Nueva" });
    expect(key).toMatch(
      /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i,
    );
    expect(token).toBe("csrf-token");
    expect(screen.getByText("Nueva")).toBeInTheDocument();
  });

  it("retries an uncertain creation with the same key and creates a new key after discard", async () => {
    vi.spyOn(crypto, "randomUUID")
      .mockReturnValueOnce("11111111-1111-4111-8111-111111111111")
      .mockReturnValueOnce("22222222-2222-4222-8222-222222222222");
    createIdentityMock.mockRejectedValueOnce(new IdentityAdministrationNetworkError());
    const { user } = renderPanel();
    await screen.findByText("No hay Identities.");
    await createWithName(user, "Primera");
    await screen.findByRole("region", {
      name: "Creación de Identity con resultado no confirmado",
    });
    const firstCall = createIdentityMock.mock.calls[0];

    createIdentityMock.mockResolvedValueOnce(identity({ operationalName: "Primera" }));
    await user.click(screen.getByRole("button", { name: "Reintentar misma intención" }));
    await screen.findByText("Identity creada correctamente.");
    expect(createIdentityMock.mock.calls[1]).toEqual(firstCall);

    createIdentityMock.mockRejectedValueOnce(new IdentityAdministrationNetworkError());
    await user.clear(screen.getByLabelText("Nombre operacional"));
    await user.type(screen.getByLabelText("Nombre operacional"), "Segunda");
    await user.click(screen.getByRole("button", { name: "Crear Identity" }));
    await screen.findByRole("region", {
      name: "Creación de Identity con resultado no confirmado",
    });
    await user.click(screen.getByRole("button", { name: "Descartar e iniciar nueva" }));
    createIdentityMock.mockResolvedValueOnce(identity({ operationalName: "Tercera" }));
    await user.clear(screen.getByLabelText("Nombre operacional"));
    await user.type(screen.getByLabelText("Nombre operacional"), "Tercera");
    await user.click(screen.getByRole("button", { name: "Crear Identity" }));
    await screen.findByText("Identity creada correctamente.");
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
    await screen.findByText("Ana");
    await user.click(screen.getByRole("button", { name: "Cambiar nombre de Ana" }));
    await user.clear(screen.getByLabelText("Nuevo nombre operacional"));
    await user.type(screen.getByLabelText("Nuevo nombre operacional"), "Ana renovada");
    await user.click(screen.getByRole("button", { name: "Confirmar cambio de nombre" }));

    expect(await screen.findByText("Ana renovada")).toBeInTheDocument();
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
    await screen.findByText("Ana");
    await user.click(screen.getByRole("button", { name: "Cambiar nombre de Ana" }));
    await user.clear(screen.getByLabelText("Nuevo nombre operacional"));
    await user.type(screen.getByLabelText("Nuevo nombre operacional"), "Nombre fallido");
    await user.click(screen.getByRole("button", { name: "Confirmar cambio de nombre" }));

    expect(await screen.findByText("Ingresá un nombre operacional válido.")).toBeInTheDocument();
    expect(screen.getByText("Ana")).toBeInTheDocument();
    expect(screen.queryByText("Nombre fallido")).not.toBeInTheDocument();
  });

  it("renders the fixed closed responsibility set and reconciles activation from the response", async () => {
    listAdministrativeIdentitiesMock.mockResolvedValueOnce([
      identity({ isActive: false, responsibilities: [] }),
    ]);
    activateIdentityMock.mockResolvedValueOnce(identity({ isActive: true }));
    const { user } = renderPanel();

    await screen.findByText("OrderOperationsAndBasicClosure: No asignada");
    for (const responsibility of [
      "OrderOperationsAndBasicClosure",
      "OperationalIntervention",
      "Preparation",
      "CatalogConfiguration",
      "InventoryOperation",
      "InventoryConfiguration",
      "GeneralConfiguration",
    ]) {
      expect(screen.getByText(`${responsibility}: No asignada`)).toBeInTheDocument();
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
    revokeResponsibilityMock.mockResolvedValueOnce(identity({ responsibilities: [] }));
    const { user } = renderPanel();

    await screen.findByText("Preparation: No asignada");
    await user.click(
      screen.getByRole("button", { name: "Asignar Preparation a Ana" }),
    );
    expect(await screen.findByText("Preparation: Asignada")).toBeInTheDocument();
    expect(assignResponsibilityMock.mock.calls[0]?.slice(0, 2)).toEqual([
      "identity-1",
      "Preparation",
    ]);

    await user.click(
      screen.getByRole("button", { name: "Revocar Preparation a Ana" }),
    );
    expect(await screen.findByText("Preparation: No asignada")).toBeInTheDocument();
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

    await screen.findByText("GeneralConfiguration: Asignada");
    await user.click(
      screen.getByRole("button", {
        name: "Revocar GeneralConfiguration a Ana",
      }),
    );

    expect(
      await screen.findByText("Debe permanecer al menos una vía administrativa utilizable."),
    ).toBeInTheDocument();
    expect(screen.getByText("GeneralConfiguration: Asignada")).toBeInTheDocument();
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

    await screen.findByText("Ana");
    await user.click(screen.getByRole("button", { name: "Desactivar Ana" }));

    expect(await screen.findByText("Debe permanecer al menos una vía administrativa utilizable.")).toBeInTheDocument();
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
    activateIdentityMock.mockRejectedValueOnce(new IdentityAdministrationNetworkError());
    const { user } = renderPanel();

    await screen.findByText("Ana");
    await user.click(screen.getByRole("button", { name: "Activar Ana" }));
    await screen.findByRole("region", {
      name: "Actualización de Identity con resultado no confirmado",
    });
    const firstCall = activateIdentityMock.mock.calls[0];
    activateIdentityMock.mockResolvedValueOnce(identity({ isActive: true }));
    await user.click(
      screen.getByRole("button", { name: "Reintentar misma intención" }),
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

    await screen.findByText("Inactiva");
    await user.click(screen.getByRole("button", { name: "Activar Ana" }));

    expect(await screen.findByText("No se pudo actualizar la Identity. Revisá los datos e intentá nuevamente.")).toBeInTheDocument();
    expect(screen.getByText("Inactiva")).toBeInTheDocument();
  });

  it("uses a new key for a changed responsibility intention and does not refresh another actor", async () => {
    vi.spyOn(crypto, "randomUUID")
      .mockReturnValueOnce("11111111-1111-4111-8111-111111111111")
      .mockReturnValueOnce("22222222-2222-4222-8222-222222222222");
    listAdministrativeIdentitiesMock.mockResolvedValueOnce([
      identity({ identityId: "identity-2", operationalName: "Beto", responsibilities: [] }),
      identity({ responsibilities: ["GeneralConfiguration"] }),
    ]);
    assignResponsibilityMock
      .mockRejectedValueOnce(new IdentityAdministrationNetworkError())
      .mockResolvedValueOnce(
        identity({ identityId: "identity-2", operationalName: "Beto", responsibilities: ["CatalogConfiguration"] }),
      );
    const { onCurrentIdentityChanged, user } = renderPanel();

    await screen.findByText("Beto");
    await user.click(
      screen.getByRole("button", { name: "Asignar Preparation a Beto" }),
    );
    await screen.findByRole("region", {
      name: "Actualización de Identity con resultado no confirmado",
    });
    await user.click(screen.getByRole("button", { name: "Descartar e iniciar nueva" }));
    await user.click(
      screen.getByRole("button", { name: "Asignar CatalogConfiguration a Beto" }),
    );

    expect(await screen.findByText("CatalogConfiguration: Asignada")).toBeInTheDocument();
    expect(assignResponsibilityMock.mock.calls[0]?.[2]).not.toBe(
      assignResponsibilityMock.mock.calls[1]?.[2],
    );
    expect(onCurrentIdentityChanged).not.toHaveBeenCalled();
    expect(screen.getByText("GeneralConfiguration: Asignada")).toBeInTheDocument();
  });

  it("refreshes the current Identity after a successful self responsibility change", async () => {
    listAdministrativeIdentitiesMock.mockResolvedValueOnce([
      identity({ responsibilities: ["GeneralConfiguration", "CatalogConfiguration"] }),
    ]);
    revokeResponsibilityMock.mockResolvedValueOnce(
      identity({ responsibilities: ["CatalogConfiguration"] }),
    );
    const { onCurrentIdentityChanged, user } = renderPanel();

    await screen.findByText("GeneralConfiguration: Asignada");
    await user.click(
      screen.getByRole("button", {
        name: "Revocar GeneralConfiguration a Ana",
      }),
    );

    await waitFor(() => expect(onCurrentIdentityChanged).toHaveBeenCalledOnce());
    expect(screen.getByText("GeneralConfiguration: No asignada")).toBeInTheDocument();
  });

  it("reconciles successful self-deactivation before refreshing the current session", async () => {
    listAdministrativeIdentitiesMock.mockResolvedValueOnce([identity()]);
    deactivateIdentityMock.mockResolvedValueOnce(identity({ isActive: false }));
    const { onCurrentIdentityChanged, user } = renderPanel();

    await screen.findByText("Ana");
    await user.click(screen.getByRole("button", { name: "Desactivar Ana" }));

    expect(await screen.findByText("Inactiva")).toBeInTheDocument();
    await waitFor(() => expect(onCurrentIdentityChanged).toHaveBeenCalledOnce());
  });

  it("retires stale state after 403 and fences a late read after owner replacement", async () => {
    listAdministrativeIdentitiesMock.mockRejectedValueOnce(
      new IdentityAdministrationProblemError({ status: 403 }),
    );
    const first = renderPanel();
    await waitFor(() => expect(first.onForbidden).toHaveBeenCalledOnce());
    expect(screen.queryByRole("heading", { name: "Configuración general" })).not.toBeInTheDocument();

    let resolveOldRead: ((identities: AdministrativeIdentity[]) => void) | undefined;
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
