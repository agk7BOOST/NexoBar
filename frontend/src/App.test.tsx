import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";
import App from "./App.tsx";
import type { Product } from "./catalog/catalogClient.ts";

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
  fetchMock.mockResolvedValueOnce(jsonResponse([listedProduct]));
  const user = userEvent.setup();
  render(<App />);
  await screen.findByText("Products: 1");
  return user;
}

describe("App coordination", () => {
  beforeEach(() => {
    fetchMock.mockReset();
    vi.stubGlobal("fetch", fetchMock);
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
    expect(fetchMock).toHaveBeenCalledTimes(3);
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

  it("initial current-session 401 shows login without hiding anonymous flows", async () => {
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
    expect(screen.getByLabelText("Workflow coordinado")).toBeInTheDocument();
  });

  it("shows current Identity and returns to login after protected 401", async () => {
    fetchMock
      .mockResolvedValueOnce(jsonResponse([listedProduct]))
      .mockResolvedValueOnce(
        jsonResponse({ identityId: "identity-1", operationalName: "Ana" }),
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
        jsonResponse({ identityId: "identity-1", operationalName: "Ana" }),
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
        jsonResponse({ identityId: "identity-1", operationalName: "Ana" }),
      )
      .mockResolvedValueOnce(jsonResponse([]));

    render(<App />);

    expect(
      await screen.findByLabelText("Inventario coordinado"),
    ).toBeInTheDocument();
  });
});
