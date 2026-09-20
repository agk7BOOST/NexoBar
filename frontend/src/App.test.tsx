import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";
import App from "./App.tsx";
import type { Product } from "./catalog/catalogClient.ts";
import type { CurrentIdentity } from "./identity/sessionClient.ts";

const fetchMock = vi.fn<typeof fetch>();

interface CatalogPanelMockProps {
  products: Product[];
  reloadProducts: () => Promise<void>;
}

interface OrderWorkflowMockProps {
  activeOperationalReference: string | null;
  requestedTarget?: { operationalReference: string; sequence: number };
  onActivateOrder: (reference: string) => void;
  onOrderChanged: (reference: string) => void;
}

interface OrderLookupMockProps {
  activeOperationalReference: string | null;
  requestedLookup?: { operationalReference: string; sequence: number };
  onContinueOrder: (reference: string) => void;
}

interface SessionBarMockProps {
  identity: CurrentIdentity;
  onLoggedOut: () => void;
}

vi.mock("./catalog/CatalogPanel.tsx", () => ({
  CatalogPanel: ({ products, reloadProducts }: CatalogPanelMockProps) => (
    <section aria-label="Catalog coordinado">
      <span>Products: {products.length}</span>
      <button type="button" onClick={() => void reloadProducts()}>
        Simular Price Change exitoso
      </button>
    </section>
  ),
}));

vi.mock("./orderOperations/OrderWorkflow.tsx", () => ({
  OrderWorkflow: ({
    activeOperationalReference,
    requestedTarget,
    onActivateOrder,
    onOrderChanged,
  }: OrderWorkflowMockProps) => (
    <section aria-label="Workflow coordinado">
      <span>Activo: {activeOperationalReference ?? "ninguno"}</span>
      <span>
        Destino solicitado: {requestedTarget?.operationalReference ?? "ninguno"}
      </span>
      <button
        type="button"
        onClick={() => {
          onActivateOrder("order-created");
          onOrderChanged("order-created");
        }}
      >
        Simular Primera Confirmación
      </button>
      {requestedTarget && (
        <button
          type="button"
          onClick={() => onActivateOrder(requestedTarget.operationalReference)}
        >
          Aceptar destino solicitado
        </button>
      )}
      {activeOperationalReference && (
        <button
          type="button"
          onClick={() => onOrderChanged(activeOperationalReference)}
        >
          Simular Confirmación posterior
        </button>
      )}
    </section>
  ),
}));

vi.mock("./orderOperations/OrderLookup.tsx", () => ({
  OrderLookup: ({
    activeOperationalReference,
    requestedLookup,
    onContinueOrder,
  }: OrderLookupMockProps) => (
    <section aria-label="Lookup coordinado">
      <span>Lookup activo: {activeOperationalReference ?? "ninguno"}</span>
      <span>
        Lookup solicitado: {requestedLookup?.operationalReference ?? "ninguno"}
      </span>
      <span>Sequence: {requestedLookup?.sequence ?? 0}</span>
      <button type="button" onClick={() => onContinueOrder("order-consulted")}>
        Simular Continuar
      </button>
    </section>
  ),
}));

vi.mock("./inventory/InventoryPanel.tsx", () => ({
  InventoryPanel: () => <section aria-label="Inventario coordinado" />,
}));

vi.mock("./identity/SessionBar.tsx", () => ({
  SessionBar: ({ identity, onLoggedOut }: SessionBarMockProps) => (
    <section aria-label="Identity actual">
      <span>{identity.operationalName}</span>
      <span aria-label="Responsabilidades actuales">
        {identity.responsibilities.join(",")}
      </span>
      <button type="button" onClick={onLoggedOut}>
        Simular cierre de sesión
      </button>
    </section>
  ),
}));

vi.mock("./notifications/NotificationSseProvider.tsx", () => ({
  NotificationSseProvider: ({ children }: { children: React.ReactNode }) => children,
  usePreparationDestinationInvalidation: () => undefined,
  usePreparationConnectionGeneration: () => 0,
  useOrderInvalidation: () => undefined,
  useOrderConnectionGeneration: () => 0,
  useInventoryOperationInvalidation: () => undefined,
  useInventoryOperationConnectionGeneration: () => 0,
}));

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: {
      "Content-Type":
        status >= 400 ? "application/problem+json" : "application/json",
    },
  });
}

const listedProduct: Product = {
  id: "product-1",
  operationalName: "Agua",
  price: "10.00",
  isActive: true,
  isAvailable: true,
  requiresPreparation: false,
};

async function renderLoadedApp() {
  fetchMock
    .mockResolvedValueOnce(jsonResponse([listedProduct]))
    .mockResolvedValueOnce(
      jsonResponse({
        identityId: "identity-1",
        operationalName: "Ana",
        responsibilities: [],
      }),
    )
    .mockResolvedValueOnce(jsonResponse([]));
  const user = userEvent.setup();
  render(<App />);
  await screen.findByText("Products: 1");
  await screen.findByLabelText("Workflow coordinado");
  return user;
}

describe("App coordination", () => {
  beforeEach(() => {
    fetchMock.mockReset();
    vi.stubGlobal("fetch", fetchMock);
  });

  it("keeps one authenticated Preparation subtree across sibling rerenders", async () => {
    const errors = vi.spyOn(console, "error");
    try {
      const user = await renderLoadedApp();
      await screen.findByText("No hay destinos de preparación habilitados para esta Identity.");
      fetchMock.mockResolvedValue(jsonResponse([listedProduct]));
      await user.click(screen.getByRole("button", { name: "Simular Price Change exitoso" }));
      await waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(4));

      expect(screen.getAllByRole("region", { name: "Identity actual" })).toHaveLength(1);
      expect(screen.getAllByRole("region", { name: "Preparación" })).toHaveLength(1);
      expect(screen.getByRole("button", { name: "Actualizar preparación" })).toBeEnabled();
      expect(screen.queryByText("Cargando destinos…")).not.toBeInTheDocument();
      expect(errors.mock.calls.filter(args => String(args[0]).includes("same key"))).toEqual([]);
    } finally {
      errors.mockRestore();
    }
  });

  it("Primera Confirmación activa el Pedido y solicita su lookup", async () => {
    const user = await renderLoadedApp();

    await user.click(
      screen.getByRole("button", { name: "Simular Primera Confirmación" }),
    );

    expect(screen.getByText("Activo: order-created")).toBeInTheDocument();
    expect(
      screen.getByText("Lookup activo: order-created"),
    ).toBeInTheDocument();
    expect(
      screen.getByText("Lookup solicitado: order-created"),
    ).toBeInTheDocument();
    expect(screen.getByText("Sequence: 1")).toBeInTheDocument();
  });

  it("Continuar desde lookup solicita el destino y el workflow lo activa", async () => {
    const user = await renderLoadedApp();

    await user.click(screen.getByRole("button", { name: "Simular Continuar" }));
    expect(
      screen.getByText("Destino solicitado: order-consulted"),
    ).toBeInTheDocument();
    expect(screen.getByText("Activo: ninguno")).toBeInTheDocument();

    await user.click(
      screen.getByRole("button", { name: "Aceptar destino solicitado" }),
    );
    expect(screen.getByText("Activo: order-consulted")).toBeInTheDocument();
    expect(
      screen.getByText("Lookup activo: order-consulted"),
    ).toBeInTheDocument();
  });

  it("Price Change exitoso recarga el único recurso Catalog", async () => {
    const user = await renderLoadedApp();
    fetchMock.mockResolvedValueOnce(
      jsonResponse([listedProduct, listedProduct]),
    );

    await user.click(
      screen.getByRole("button", { name: "Simular Price Change exitoso" }),
    );

    expect(await screen.findByText("Products: 2")).toBeInTheDocument();
    expect(fetchMock).toHaveBeenCalledTimes(4);
  });

  it("Confirmación posterior incrementa sequence para refrescar el mismo Pedido", async () => {
    const user = await renderLoadedApp();
    await user.click(
      screen.getByRole("button", { name: "Simular Primera Confirmación" }),
    );
    expect(screen.getByText("Sequence: 1")).toBeInTheDocument();

    await user.click(
      screen.getByRole("button", { name: "Simular Confirmación posterior" }),
    );

    expect(
      screen.getByText("Lookup solicitado: order-created"),
    ).toBeInTheDocument();
    expect(screen.getByText("Sequence: 2")).toBeInTheDocument();
  });

  it("initial current-session 401 keeps anonymous reads but hides authenticated workflow", async () => {
    fetchMock
      .mockResolvedValueOnce(jsonResponse([listedProduct]))
      .mockResolvedValueOnce(
        jsonResponse({ code: "authentication_required" }, 401),
      );

    render(<App />);

    expect(
      await screen.findByRole("heading", { name: "Ingresar" }),
    ).toBeInTheDocument();
    expect(screen.getByLabelText("Catalog coordinado")).toBeInTheDocument();
    expect(
      screen.queryByLabelText("Workflow coordinado"),
    ).not.toBeInTheDocument();
  });

  it("shows current Identity and returns to login after protected 401", async () => {
    fetchMock
      .mockResolvedValueOnce(jsonResponse([listedProduct]))
      .mockResolvedValueOnce(
        jsonResponse({
          identityId: "identity-1",
          operationalName: "Ana",
          responsibilities: [],
        }),
      )
      .mockResolvedValueOnce(
        jsonResponse([
          {
            preparationResponsibilityId: "destination-1",
            operationalName: "Cocina",
          },
        ]),
      )
      .mockResolvedValueOnce(jsonResponse({ code: "invalid_session" }, 401));

    render(<App />);

    expect(
      await screen.findByRole("heading", { name: "Ingresar" }),
    ).toBeInTheDocument();
    expect(screen.queryByText("Ana")).not.toBeInTheDocument();
  });

  it("keeps current Identity after protected 403", async () => {
    fetchMock
      .mockResolvedValueOnce(jsonResponse([listedProduct]))
      .mockResolvedValueOnce(
        jsonResponse({
          identityId: "identity-1",
          operationalName: "Ana",
          responsibilities: [],
        }),
      )
      .mockResolvedValueOnce(
        jsonResponse([
          {
            preparationResponsibilityId: "destination-1",
            operationalName: "Cocina",
          },
        ]),
      )
      .mockResolvedValueOnce(jsonResponse({ code: "forbidden" }, 403));

    render(<App />);

    expect(await screen.findByRole("alert")).toHaveTextContent(
      "Esta Identity no tiene autorización para esa preparación.",
    );
    expect(screen.getByText("Ana")).toBeInTheDocument();
    expect(
      screen.queryByRole("heading", { name: "Ingresar" }),
    ).not.toBeInTheDocument();
  });

  it("adds Inventory access only within an authenticated session", async () => {
    fetchMock
      .mockResolvedValueOnce(jsonResponse([listedProduct]))
      .mockResolvedValueOnce(
        jsonResponse({
          identityId: "identity-1",
          operationalName: "Ana",
          responsibilities: [],
        }),
      )
      .mockResolvedValueOnce(jsonResponse([]));

    render(<App />);

    expect(
      await screen.findByLabelText("Inventario coordinado"),
    ).toBeInTheDocument();
  });

  it("clears and replaces current responsibility context across session changes", async () => {
    fetchMock
      .mockResolvedValueOnce(jsonResponse([listedProduct]))
      .mockResolvedValueOnce(
        jsonResponse({ code: "authentication_required" }, 401),
      )
      .mockResolvedValueOnce(jsonResponse({ requestToken: "csrf-ana" }))
      .mockResolvedValueOnce(
        jsonResponse({
          identityId: "identity-ana",
          operationalName: "Ana",
          responsibilities: ["CatalogConfiguration"],
        }),
      )
      .mockResolvedValueOnce(jsonResponse([]))
      .mockResolvedValueOnce(jsonResponse({ requestToken: "csrf-beto" }))
      .mockResolvedValueOnce(
        jsonResponse({
          identityId: "identity-beto",
          operationalName: "Beto",
          responsibilities: ["OrderOperationsAndBasicClosure"],
        }),
      )
      .mockResolvedValueOnce(jsonResponse([]));
    const user = userEvent.setup();
    render(<App />);

    await screen.findByRole("heading", { name: "Ingresar" });
    await user.type(screen.getByLabelText("Identificador de acceso"), "ana");
    await user.type(screen.getByLabelText("Secreto"), "secret");
    await user.click(screen.getByRole("button", { name: "Ingresar" }));

    expect(
      await screen.findByLabelText("Responsabilidades actuales"),
    ).toHaveTextContent("CatalogConfiguration");

    await user.click(
      screen.getByRole("button", { name: "Simular cierre de sesión" }),
    );
    expect(
      await screen.findByRole("heading", { name: "Ingresar" }),
    ).toBeInTheDocument();
    expect(
      screen.queryByLabelText("Responsabilidades actuales"),
    ).not.toBeInTheDocument();

    await user.type(screen.getByLabelText("Identificador de acceso"), "beto");
    await user.type(screen.getByLabelText("Secreto"), "secret");
    await user.click(screen.getByRole("button", { name: "Ingresar" }));

    expect(
      await screen.findByLabelText("Responsabilidades actuales"),
    ).toHaveTextContent("OrderOperationsAndBasicClosure");
    expect(screen.getByLabelText("Responsabilidades actuales")).not.toHaveTextContent(
      "CatalogConfiguration",
    );
  });
});
