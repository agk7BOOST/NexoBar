import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";
import App from "./App.tsx";
import type { CurrentIdentity } from "./identity/sessionClient.ts";

const { getCurrentIdentityMock } = vi.hoisted(() => ({
  getCurrentIdentityMock: vi.fn(),
}));
const fetchMock = vi.fn<typeof fetch>();

vi.mock("./identity/sessionClient.ts", async (importOriginal) => {
  const original =
    await importOriginal<typeof import("./identity/sessionClient.ts")>();
  return { ...original, getCurrentIdentity: getCurrentIdentityMock };
});

vi.mock("./catalog/CatalogPanel.tsx", () => ({
  CatalogPanel: ({ onUnauthorized }: { onUnauthorized: () => void }) => (
    <section aria-label="Catalog administrativo">
      <button type="button" onClick={onUnauthorized}>
        Retirar Catalog
      </button>
    </section>
  ),
}));
vi.mock("./generalConfiguration/GeneralConfigurationPanel.tsx", () => ({
  GeneralConfigurationPanel: ({
    onForbidden,
  }: {
    onForbidden: () => void;
  }) => (
    <section aria-label="Configuracion general administrativa">
      <button type="button" onClick={onForbidden}>
        Refrescar configuracion general
      </button>
    </section>
  ),
}));
vi.mock("./orderOperations/OrderWorkflow.tsx", () => ({
  OrderWorkflow: () => <section aria-label="Composicion operacional" />,
}));
vi.mock("./orderOperations/OrderLookup.tsx", () => ({ OrderLookup: () => null }));
vi.mock("./orderOperations/OperationalInterventionPanel.tsx", () => ({
  OperationalInterventionPanel: () => null,
}));
vi.mock("./preparation/PreparationPanel.tsx", () => ({
  PreparationPanel: () => null,
}));
vi.mock("./inventory/InventoryPanel.tsx", () => ({
  InventoryPanel: () => null,
}));
vi.mock("./delivery/DeliveryPanel.tsx", () => ({ DeliveryPanel: () => null }));
vi.mock("./identity/SessionBar.tsx", () => ({
  SessionBar: ({ onLoggedOut }: { onLoggedOut: () => void }) => (
    <button type="button" onClick={onLoggedOut}>
      Salir
    </button>
  ),
}));
vi.mock("./notifications/NotificationSseProvider.tsx", () => ({
  NotificationSseProvider: ({ children }: { children: React.ReactNode }) =>
    children,
}));

function identity(responsibilities: string[]): CurrentIdentity {
  return { identityId: "identity-1", operationalName: "Ana", responsibilities };
}

describe("App capability-aware administrative mounting", () => {
  beforeEach(() => {
    fetchMock.mockReset();
    vi.stubGlobal("fetch", fetchMock);
    getCurrentIdentityMock.mockReset();
  });

  it("does not mount General Configuration while unauthenticated", async () => {
    getCurrentIdentityMock.mockRejectedValueOnce({ status: 401 });
    render(<App />);

    await screen.findByRole("heading", { name: "Ingresar" });
    expect(
      screen.queryByLabelText("Configuracion general administrativa"),
    ).not.toBeInTheDocument();
    expect(fetchMock).not.toHaveBeenCalledWith(
      "/api/identities",
      expect.anything(),
    );
    expect(screen.queryByLabelText("Catalog administrativo")).not.toBeInTheDocument();
  });

  it("does not mount CatalogConfiguration without that responsibility", async () => {
    getCurrentIdentityMock.mockResolvedValueOnce(
      identity(["OrderOperationsAndBasicClosure"]),
    );
    render(<App />);

    await screen.findByLabelText("Composicion operacional");
    expect(screen.queryByLabelText("Catalog administrativo")).not.toBeInTheDocument();
  });

  it("mounts CatalogConfiguration only for its current responsibility", async () => {
    getCurrentIdentityMock.mockResolvedValueOnce(identity(["CatalogConfiguration"]));
    render(<App />);

    expect(await screen.findByLabelText("Catalog administrativo")).toBeInTheDocument();
  });

  it("does not mount or preload General Configuration without GeneralConfiguration", async () => {
    getCurrentIdentityMock.mockResolvedValueOnce(identity(["CatalogConfiguration"]));
    render(<App />);

    await screen.findByLabelText("Catalog administrativo");
    expect(
      screen.queryByLabelText("Configuracion general administrativa"),
    ).not.toBeInTheDocument();
    expect(fetchMock).not.toHaveBeenCalledWith(
      "/api/identities",
      expect.anything(),
    );
  });

  it("mounts General Configuration only for GeneralConfiguration", async () => {
    getCurrentIdentityMock.mockResolvedValueOnce(identity(["GeneralConfiguration"]));
    render(<App />);

    expect(
      await screen.findByLabelText("Configuracion general administrativa"),
    ).toBeInTheDocument();
    expect(
      screen.queryByLabelText("Catalog administrativo"),
    ).not.toBeInTheDocument();
  });

  it("clears the General Configuration surface on logout", async () => {
    getCurrentIdentityMock.mockResolvedValueOnce(identity(["GeneralConfiguration"]));
    const user = userEvent.setup();
    render(<App />);

    await screen.findByLabelText("Configuracion general administrativa");
    await user.click(screen.getByRole("button", { name: "Salir" }));

    await screen.findByRole("heading", { name: "Ingresar" });
    expect(
      screen.queryByLabelText("Configuracion general administrativa"),
    ).not.toBeInTheDocument();
  });

  it("clears other capability-owned administrative surfaces on logout", async () => {
    getCurrentIdentityMock.mockResolvedValueOnce(
      identity(["CatalogConfiguration", "OrderOperationsAndBasicClosure"]),
    );
    const user = userEvent.setup();
    render(<App />);

    await screen.findByLabelText("Catalog administrativo");
    await screen.findByLabelText("Composicion operacional");
    await user.click(screen.getByRole("button", { name: "Salir" }));

    await screen.findByRole("heading", { name: "Ingresar" });
    expect(screen.queryByLabelText("Catalog administrativo")).not.toBeInTheDocument();
    expect(screen.queryByLabelText("Composicion operacional")).not.toBeInTheDocument();
  });

  it("retires General Configuration after a refreshed current Identity loses the responsibility", async () => {
    getCurrentIdentityMock
      .mockResolvedValueOnce(identity(["GeneralConfiguration"]))
      .mockResolvedValueOnce(identity(["CatalogConfiguration"]));
    const user = userEvent.setup();
    render(<App />);

    await screen.findByLabelText("Configuracion general administrativa");
    await user.click(
      screen.getByRole("button", { name: "Refrescar configuracion general" }),
    );

    await waitFor(() =>
      expect(
        screen.queryByLabelText("Configuracion general administrativa"),
      ).not.toBeInTheDocument(),
    );
    expect(screen.getByLabelText("Catalog administrativo")).toBeInTheDocument();
  });
});
