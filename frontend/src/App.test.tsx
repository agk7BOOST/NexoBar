import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";
import App from "./App.tsx";
import type { Product } from "./catalog/catalogClient.ts";

const fetchMock = vi.fn<typeof fetch>();

function jsonResponse(body: unknown, init?: ResponseInit): Response {
  return new Response(JSON.stringify(body), {
    status: 200,
    headers: { "Content-Type": "application/json" },
    ...init,
  });
}

function problemResponse(body: unknown, status: number): Response {
  return jsonResponse(body, {
    status,
    headers: { "Content-Type": "application/problem+json" },
  });
}

function product(overrides?: Partial<Product>): Product {
  return {
    id: "0198f00a-e324-7a75-a89f-7a1a30d39803",
    operationalName: "Agua tónica",
    price: "12345678901234567890.12345678",
    isActive: true,
    isAvailable: true,
    requiresPreparation: false,
    ...overrides,
  };
}

async function renderWithLoadedList(products: Product[] = []) {
  fetchMock.mockResolvedValueOnce(jsonResponse(products));
  const user = userEvent.setup();
  render(<App />);

  if (products.length === 0) {
    await screen.findByText("No hay productos vigentes.");
  } else {
    await screen.findByText(products[0]!.operationalName);
  }

  return user;
}

async function fillCreationForm(
  user: ReturnType<typeof userEvent.setup>,
  name = "Soda",
  price = "10.50",
) {
  await user.type(screen.getByLabelText("Nombre operacional"), name);
  await user.type(screen.getByLabelText("Precio"), price);
}

function creationRequestAt(callIndex: number) {
  const [, requestInit] = fetchMock.mock.calls[callIndex]!;

  return {
    body: JSON.parse(String(requestInit?.body)) as Record<string, unknown>,
    idempotencyKey: new Headers(requestInit?.headers).get("Idempotency-Key"),
  };
}

describe("Catálogo mínimo operativo", () => {
  beforeEach(() => {
    fetchMock.mockReset();
    vi.stubGlobal("fetch", fetchMock);
  });

  it("renders the initial catalog UI", async () => {
    await renderWithLoadedList();

    expect(
      screen.getByRole("heading", { name: "Catálogo de productos" }),
    ).toBeInTheDocument();
    expect(screen.getByLabelText("Nombre operacional")).toBeInTheDocument();
    expect(screen.getByLabelText("Precio")).toBeInTheDocument();
    expect(
      screen.getByRole("button", { name: "Crear producto" }),
    ).toBeInTheDocument();
  });

  it("loads products and renders the exact string price and availability", async () => {
    const listedProduct = product();

    await renderWithLoadedList([listedProduct]);

    expect(screen.getByText(listedProduct.price)).toBeInTheDocument();
    expect(screen.getByText("Disponible")).toBeInTheDocument();
    expect(fetchMock).toHaveBeenCalledWith("/api/catalog/products", undefined);
  });

  it("sends an explicit false preparation flag, string price, and UUID v4", async () => {
    const user = await renderWithLoadedList();
    fetchMock.mockResolvedValueOnce(jsonResponse(product(), { status: 201 }));
    fetchMock.mockResolvedValueOnce(jsonResponse([]));

    await fillCreationForm(user);
    await user.click(screen.getByRole("button", { name: "Crear producto" }));
    await screen.findByText("Producto creado correctamente.");

    const creationRequest = creationRequestAt(1);

    expect(creationRequest.body).toEqual({
      operationalName: "Soda",
      price: "10.50",
      requiresPreparation: false,
    });
    expect(typeof creationRequest.body.price).toBe("string");
    expect(creationRequest.idempotencyKey).toMatch(
      /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i,
    );
  });

  it("refreshes the product list after successful creation", async () => {
    const user = await renderWithLoadedList();
    const createdProduct = product({ operationalName: "Soda", price: "10.50" });
    fetchMock.mockResolvedValueOnce(
      jsonResponse(createdProduct, { status: 201 }),
    );
    fetchMock.mockResolvedValueOnce(jsonResponse([createdProduct]));

    await fillCreationForm(user);
    await user.click(screen.getByRole("button", { name: "Crear producto" }));

    expect(await screen.findByText("Soda")).toBeInTheDocument();
    expect(fetchMock).toHaveBeenCalledTimes(3);
    expect(fetchMock.mock.calls[2]?.[0]).toBe("/api/catalog/products");
  });

  it("shows a comprehensible functional error from Problem Details", async () => {
    const user = await renderWithLoadedList();
    fetchMock.mockResolvedValueOnce(
      problemResponse(
        {
          title: "Operational name already in use",
          status: 409,
          code: "catalog.product.operational_name_conflict",
        },
        409,
      ),
    );

    await fillCreationForm(user);
    await user.click(screen.getByRole("button", { name: "Crear producto" }));

    expect(
      await screen.findByText("Ya existe un producto vigente con ese nombre."),
    ).toBeInTheDocument();
    expect(
      screen.queryByText("Operational name already in use"),
    ).not.toBeInTheDocument();
  });

  it("shows a network failure as an unconfirmed result, not a functional rejection", async () => {
    const user = await renderWithLoadedList();
    fetchMock.mockRejectedValueOnce(new TypeError("Failed to fetch"));

    await fillCreationForm(user);
    await user.click(screen.getByRole("button", { name: "Crear producto" }));

    expect(
      await screen.findByText(/Resultado no confirmado:/),
    ).toBeInTheDocument();
    expect(
      screen.queryByText(/No se pudo crear el producto/),
    ).not.toBeInTheDocument();
    const pendingIntention = screen.getByRole("region", {
      name: "Intención con resultado no confirmado",
    });
    expect(pendingIntention).toHaveTextContent("Soda");
    expect(pendingIntention).toHaveTextContent("10.50");
    expect(
      screen.getByRole("button", { name: "Reintentar misma intención" }),
    ).toBeInTheDocument();
  });

  it("retries the exact uncertain intention and resolves it after known success", async () => {
    const user = await renderWithLoadedList();
    fetchMock.mockRejectedValueOnce(new TypeError("Failed to fetch"));

    await fillCreationForm(user);
    await user.click(screen.getByRole("button", { name: "Crear producto" }));
    await screen.findByText(/Resultado no confirmado:/);
    const firstRequest = creationRequestAt(1);

    const createdProduct = product({ operationalName: "Soda", price: "10.50" });
    fetchMock.mockResolvedValueOnce(
      jsonResponse(createdProduct, { status: 201 }),
    );
    fetchMock.mockResolvedValueOnce(jsonResponse([createdProduct]));

    await user.click(
      screen.getByRole("button", { name: "Reintentar misma intención" }),
    );
    await screen.findByText("Producto creado correctamente.");

    const retryRequest = creationRequestAt(2);
    expect(retryRequest).toEqual(firstRequest);
    expect(
      screen.queryByRole("button", { name: "Reintentar misma intención" }),
    ).not.toBeInTheDocument();
    expect(
      screen.getByRole("button", { name: "Crear producto" }),
    ).toBeEnabled();
  });

  it("does not retry a failed creation automatically", async () => {
    const user = await renderWithLoadedList();
    fetchMock.mockRejectedValueOnce(new TypeError("Failed to fetch"));

    await fillCreationForm(user);
    await user.click(screen.getByRole("button", { name: "Crear producto" }));
    await screen.findByText(/Resultado no confirmado:/);

    await new Promise((resolve) => setTimeout(resolve, 100));
    expect(fetchMock).toHaveBeenCalledTimes(2);
    expect(fetchMock.mock.calls[1]?.[1]?.method).toBe("POST");
    expect(
      screen.getByRole("button", { name: "Reintentar misma intención" }),
    ).toBeInTheDocument();
  });

  it("requires an explicit discard before changed fields become a new intention", async () => {
    const user = await renderWithLoadedList();
    fetchMock.mockRejectedValueOnce(new TypeError("Failed to fetch"));

    await fillCreationForm(user);
    await user.click(screen.getByRole("button", { name: "Crear producto" }));
    await screen.findByText(/Resultado no confirmado:/);
    const uncertainRequest = creationRequestAt(1);

    await user.clear(screen.getByLabelText("Nombre operacional"));
    await user.type(screen.getByLabelText("Nombre operacional"), "Agua");
    await user.clear(screen.getByLabelText("Precio"));
    await user.type(screen.getByLabelText("Precio"), "20.00");

    expect(
      screen.getByText(/Los cambios del formulario no alteran esta intención/),
    ).toBeInTheDocument();
    expect(
      screen.getByRole("button", { name: "Hay una intención pendiente" }),
    ).toBeDisabled();

    await user.click(
      screen.getByRole("button", { name: "Descartar e iniciar nueva" }),
    );
    fetchMock.mockResolvedValueOnce(
      problemResponse(
        {
          status: 409,
          code: "catalog.product.operational_name_conflict",
        },
        409,
      ),
    );

    await user.click(screen.getByRole("button", { name: "Crear producto" }));
    await screen.findByText("Ya existe un producto vigente con ese nombre.");

    const newRequest = creationRequestAt(2);
    expect(newRequest.body).toEqual({
      operationalName: "Agua",
      price: "20.00",
      requiresPreparation: false,
    });
    expect(newRequest.idempotencyKey).toMatch(
      /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i,
    );
    expect(newRequest.idempotencyKey).not.toBe(uncertainRequest.idempotencyKey);
  });
});
