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
  createIdentityMock,
  getAntiforgeryTokenMock,
  listAdministrativeIdentitiesMock,
  renameIdentityMock,
} = vi.hoisted(() => ({
  createIdentityMock: vi.fn(),
  getAntiforgeryTokenMock: vi.fn(),
  listAdministrativeIdentitiesMock: vi.fn(),
  renameIdentityMock: vi.fn(),
}));

vi.mock("./identityAdministrationClient.ts", async (importOriginal) => {
  const original = await importOriginal<
    typeof import("./identityAdministrationClient.ts")
  >();
  return {
    ...original,
    createIdentity: createIdentityMock,
    listAdministrativeIdentities: listAdministrativeIdentitiesMock,
    renameIdentity: renameIdentityMock,
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
  const onForbidden = vi.fn();
  const onUnauthorized = vi.fn();
  const user = userEvent.setup();
  const result = render(
    <GeneralConfigurationPanel
      onForbidden={onForbidden}
      onUnauthorized={onUnauthorized}
    />,
  );
  return { ...result, onForbidden, onUnauthorized, user };
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
    createIdentityMock.mockReset();
    getAntiforgeryTokenMock.mockReset();
    getAntiforgeryTokenMock.mockResolvedValue("csrf-token");
    listAdministrativeIdentitiesMock.mockReset();
    listAdministrativeIdentitiesMock.mockResolvedValue([]);
    renameIdentityMock.mockReset();
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
      <GeneralConfigurationPanel key="old" onForbidden={vi.fn()} onUnauthorized={vi.fn()} />,
    );
    rerender(
      <GeneralConfigurationPanel key="new" onForbidden={vi.fn()} onUnauthorized={vi.fn()} />,
    );
    expect(await screen.findByText("Beto")).toBeInTheDocument();
    resolveOldRead?.([identity({ operationalName: "Ana anterior" })]);
    await waitFor(() =>
      expect(screen.queryByText("Ana anterior")).not.toBeInTheDocument(),
    );
  });
});
