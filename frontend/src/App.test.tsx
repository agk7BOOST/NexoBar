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
        Retirar Catálogo
      </button>
    </section>
  ),
}));
vi.mock("./orderOperations/OrderWorkflow.tsx", () => ({
  OrderWorkflow: () => <section aria-label="Composición operacional" />,
}));
vi.mock("./orderOperations/OrderLookup.tsx", () => ({
  OrderLookup: () => null,
}));
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

describe("App capability-aware Catalog mounting", () => {
  beforeEach(() => {
    fetchMock.mockReset();
    vi.stubGlobal("fetch", fetchMock);
    getCurrentIdentityMock.mockReset();
  });

  it("does not request the administrative Catalog while unauthenticated", async () => {
    getCurrentIdentityMock.mockRejectedValueOnce({ status: 401 });
    render(<App />);

    await screen.findByRole("heading", { name: "Ingresar" });
    expect(fetchMock).not.toHaveBeenCalledWith(
      "/api/catalog/products",
      expect.anything(),
    );
    expect(
      screen.queryByLabelText("Catalog administrativo"),
    ).not.toBeInTheDocument();
  });

  it("does not mount administrative Catalog or request it without CatalogConfiguration", async () => {
    getCurrentIdentityMock.mockResolvedValueOnce(
      identity(["OrderOperationsAndBasicClosure"]),
    );
    render(<App />);

    await screen.findByLabelText("Composición operacional");
    expect(
      screen.queryByLabelText("Catalog administrativo"),
    ).not.toBeInTheDocument();
    expect(fetchMock).not.toHaveBeenCalledWith(
      "/api/catalog/products",
      expect.anything(),
    );
  });

  it("mounts administrative Catalog only for CatalogConfiguration", async () => {
    getCurrentIdentityMock.mockResolvedValueOnce(
      identity(["CatalogConfiguration"]),
    );
    render(<App />);

    expect(
      await screen.findByLabelText("Catalog administrativo"),
    ).toBeInTheDocument();
    expect(
      screen.queryByLabelText("Composición operacional"),
    ).not.toBeInTheDocument();
  });

  it("does not mount composition for OperationalIntervention alone", async () => {
    getCurrentIdentityMock.mockResolvedValueOnce(
      identity(["OperationalIntervention"]),
    );
    render(<App />);

    await waitFor(() =>
      expect(screen.queryByText("Cargando sesión…")).not.toBeInTheDocument(),
    );
    expect(
      screen.queryByLabelText("Composición operacional"),
    ).not.toBeInTheDocument();
    expect(
      screen.queryByLabelText("Catalog administrativo"),
    ).not.toBeInTheDocument();
  });

  it("unmounts capability-owned Product surfaces on logout", async () => {
    getCurrentIdentityMock.mockResolvedValueOnce(
      identity(["CatalogConfiguration", "OrderOperationsAndBasicClosure"]),
    );
    const user = userEvent.setup();
    render(<App />);

    await screen.findByLabelText("Catalog administrativo");
    await screen.findByLabelText("Composición operacional");
    await user.click(screen.getByRole("button", { name: "Salir" }));

    await screen.findByRole("heading", { name: "Ingresar" });
    expect(
      screen.queryByLabelText("Catalog administrativo"),
    ).not.toBeInTheDocument();
    expect(
      screen.queryByLabelText("Composición operacional"),
    ).not.toBeInTheDocument();
  });
});
