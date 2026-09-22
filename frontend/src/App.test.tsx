import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";
import App from "./App.tsx";
import {
  SessionProblemError,
  type CurrentIdentity,
} from "./identity/sessionClient.ts";

const { getCurrentIdentityMock, orderWorkflowPropsMock } = vi.hoisted(() => ({
  getCurrentIdentityMock: vi.fn(),
  orderWorkflowPropsMock: vi.fn(),
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
      <p>Editor de preparación de producto</p>
      <button type="button" onClick={onUnauthorized}>
        Retirar Catalog
      </button>
    </section>
  ),
}));
vi.mock("./generalConfiguration/GeneralConfigurationPanel.tsx", () => ({
  GeneralConfigurationPanel: ({
    onCurrentIdentityChanged,
    onForbidden,
  }: {
    onCurrentIdentityChanged: () => Promise<void>;
    onForbidden: () => void;
  }) => (
    <section aria-label="Configuracion general administrativa">
      <p>Crear responsabilidad de preparación</p>
      <button type="button" onClick={() => void onCurrentIdentityChanged()}>
        Reconciliar mutacion propia
      </button>
      <button type="button" onClick={onForbidden}>
        Refrescar configuracion general
      </button>
    </section>
  ),
}));
vi.mock("./orderOperations/OrderWorkflow.tsx", () => ({
  OrderWorkflow: (props: unknown) => {
    orderWorkflowPropsMock(props);
    return <section aria-label="Composicion operacional" />;
  },
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
    orderWorkflowPropsMock.mockReset();
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
    expect(fetchMock).not.toHaveBeenCalledWith(
      "/api/operational-configuration/preparation-responsibilities",
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

  it("does not expose an Order workflow to an OperationalIntervention-only Identity", async () => {
    getCurrentIdentityMock.mockResolvedValueOnce(
      identity(["OperationalIntervention"]),
    );
    render(<App />);

    await screen.findByRole("button", { name: "Salir" });
    expect(screen.queryByLabelText("Composicion operacional")).not.toBeInTheDocument();
  });

  it("derives the unavailable-Product intervention presentation capability from both current responsibilities", async () => {
    getCurrentIdentityMock.mockResolvedValueOnce(
      identity([
        "OrderOperationsAndBasicClosure",
        "OperationalIntervention",
      ]),
    );
    render(<App />);

    await screen.findByLabelText("Composicion operacional");
    expect(orderWorkflowPropsMock).toHaveBeenCalledWith(
      expect.objectContaining({
        canRequestUnavailableProductException: true,
      }),
    );
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
    expect(fetchMock).not.toHaveBeenCalledWith(
      "/api/operational-configuration/preparation-responsibilities",
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
    expect(
      screen.getByText("Crear responsabilidad de preparación"),
    ).toBeInTheDocument();
    expect(
      screen.queryByText("Editor de preparación de producto"),
    ).not.toBeInTheDocument();
  });

  it("keeps the General Configuration creation surface absent for a CatalogConfiguration-only Identity", async () => {
    getCurrentIdentityMock.mockResolvedValueOnce(identity(["CatalogConfiguration"]));
    render(<App />);

    expect(
      await screen.findByText("Editor de preparación de producto"),
    ).toBeInTheDocument();
    expect(
      screen.queryByText("Crear responsabilidad de preparación"),
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

  it("returns to login when a self-deactivation reconciliation finds the revoked session", async () => {
    getCurrentIdentityMock
      .mockResolvedValueOnce(identity(["GeneralConfiguration"]))
      .mockRejectedValueOnce(new SessionProblemError(401, { status: 401 }));
    const user = userEvent.setup();
    render(<App />);

    await screen.findByLabelText("Configuracion general administrativa");
    await user.click(
      screen.getByRole("button", { name: "Reconciliar mutacion propia" }),
    );

    await screen.findByRole("heading", { name: "Ingresar" });
    expect(
      screen.queryByLabelText("Configuracion general administrativa"),
    ).not.toBeInTheDocument();
  });
});
