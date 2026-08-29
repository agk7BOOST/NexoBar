import { render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";
import App from "./App.tsx";
import type { Product } from "./catalog/catalogClient.ts";
import type { FirstConfirmationResponse } from "./orderOperations/orderOperationsClient.ts";

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

function confirmationResponse(
  listedProduct: Product,
  overrides?: Partial<FirstConfirmationResponse>,
): FirstConfirmationResponse {
  return {
    operationalReference: "01990f3e-5e90-7000-8000-000000000001",
    context: "Mesa 7",
    firstIncorporation: {
      id: "01990f3e-5e90-7000-8000-000000000002",
      confirmedAt: "2026-08-29T14:30:00Z",
      items: [
        {
          productId: listedProduct.id,
          quantity: 2,
          appliedPrice: "10.50",
        },
      ],
    },
    ...overrides,
  };
}

function confirmationRequestAt(callIndex: number) {
  const [url, requestInit] = fetchMock.mock.calls[callIndex]!;

  return {
    url,
    method: requestInit?.method,
    body: JSON.parse(String(requestInit?.body)) as Record<string, unknown>,
    idempotencyKey: new Headers(requestInit?.headers).get("Idempotency-Key"),
  };
}

async function prepareConfirmation(
  user: ReturnType<typeof userEvent.setup>,
  listedProduct: Product,
  quantity = 1,
) {
  await user.type(screen.getByLabelText("Contexto"), "Mesa 7");
  const addButton = screen.getByRole("button", {
    name: `Agregar ${listedProduct.operationalName} a la composición`,
  });

  for (let count = 0; count < quantity; count += 1) {
    await user.click(addButton);
  }
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

describe("Composición efímera", () => {
  beforeEach(() => {
    fetchMock.mockReset();
    vi.stubGlobal("fetch", fetchMock);
  });

  function compositionRegion() {
    return screen.getByRole("region", { name: "Composición" });
  }

  it("empieza vacía", async () => {
    await renderWithLoadedList([product()]);

    expect(
      within(compositionRegion()).getByText("La Composición está vacía."),
    ).toBeInTheDocument();
  });

  it("agrega un Producto disponible con cantidad 1", async () => {
    const listedProduct = product();
    const user = await renderWithLoadedList([listedProduct]);

    await user.click(
      screen.getByRole("button", {
        name: `Agregar ${listedProduct.operationalName} a la composición`,
      }),
    );

    expect(
      within(compositionRegion()).getByLabelText(
        `Cantidad de ${listedProduct.operationalName}`,
      ),
    ).toHaveTextContent("1");
  });

  it("vuelve a agregar el mismo Producto incrementando a 2 sin duplicarlo", async () => {
    const listedProduct = product();
    const user = await renderWithLoadedList([listedProduct]);
    const addButton = screen.getByRole("button", {
      name: `Agregar ${listedProduct.operationalName} a la composición`,
    });

    await user.click(addButton);
    await user.click(addButton);

    const composition = within(compositionRegion());
    expect(
      composition.getByLabelText(
        `Cantidad de ${listedProduct.operationalName}`,
      ),
    ).toHaveTextContent("2");
    expect(
      composition.getAllByText(listedProduct.operationalName),
    ).toHaveLength(1);
  });

  it("aumenta la cantidad en 1", async () => {
    const listedProduct = product();
    const user = await renderWithLoadedList([listedProduct]);
    await user.click(
      screen.getByRole("button", {
        name: `Agregar ${listedProduct.operationalName} a la composición`,
      }),
    );

    await user.click(
      screen.getByRole("button", {
        name: `Aumentar cantidad de ${listedProduct.operationalName}`,
      }),
    );

    expect(
      within(compositionRegion()).getByLabelText(
        `Cantidad de ${listedProduct.operationalName}`,
      ),
    ).toHaveTextContent("2");
  });

  it("disminuye la cantidad en 1", async () => {
    const listedProduct = product();
    const user = await renderWithLoadedList([listedProduct]);
    const addButton = screen.getByRole("button", {
      name: `Agregar ${listedProduct.operationalName} a la composición`,
    });
    await user.click(addButton);
    await user.click(addButton);

    await user.click(
      screen.getByRole("button", {
        name: `Disminuir cantidad de ${listedProduct.operationalName}`,
      }),
    );

    expect(
      within(compositionRegion()).getByLabelText(
        `Cantidad de ${listedProduct.operationalName}`,
      ),
    ).toHaveTextContent("1");
  });

  it("elimina la entrada al disminuir desde 1", async () => {
    const listedProduct = product();
    const user = await renderWithLoadedList([listedProduct]);
    await user.click(
      screen.getByRole("button", {
        name: `Agregar ${listedProduct.operationalName} a la composición`,
      }),
    );

    await user.click(
      screen.getByRole("button", {
        name: `Disminuir cantidad de ${listedProduct.operationalName}`,
      }),
    );

    expect(
      within(compositionRegion()).getByText("La Composición está vacía."),
    ).toBeInTheDocument();
  });

  it("retira completamente una entrada", async () => {
    const listedProduct = product();
    const user = await renderWithLoadedList([listedProduct]);
    await user.click(
      screen.getByRole("button", {
        name: `Agregar ${listedProduct.operationalName} a la composición`,
      }),
    );

    await user.click(
      screen.getByRole("button", {
        name: `Retirar ${listedProduct.operationalName} de la composición`,
      }),
    );

    expect(
      within(compositionRegion()).getByText("La Composición está vacía."),
    ).toBeInTheDocument();
  });

  it("impide agregar un Producto no disponible", async () => {
    const unavailableProduct = product({ isAvailable: false });
    const user = await renderWithLoadedList([unavailableProduct]);
    const addButton = screen.getByRole("button", {
      name: `Agregar ${unavailableProduct.operationalName} a la composición`,
    });

    expect(addButton).toBeDisabled();
    await user.click(addButton);
    expect(
      within(compositionRegion()).getByText("La Composición está vacía."),
    ).toBeInTheDocument();
  });

  it("edita la Composición sin producir requests de mutación", async () => {
    const listedProduct = product();
    const user = await renderWithLoadedList([listedProduct]);
    await user.click(
      screen.getByRole("button", {
        name: `Agregar ${listedProduct.operationalName} a la composición`,
      }),
    );
    await user.click(
      screen.getByRole("button", {
        name: `Aumentar cantidad de ${listedProduct.operationalName}`,
      }),
    );
    await user.click(
      screen.getByRole("button", {
        name: `Disminuir cantidad de ${listedProduct.operationalName}`,
      }),
    );
    await user.click(
      screen.getByRole("button", {
        name: `Retirar ${listedProduct.operationalName} de la composición`,
      }),
    );

    expect(fetchMock).toHaveBeenCalledTimes(1);
    expect(fetchMock.mock.calls[0]).toEqual([
      "/api/catalog/products",
      undefined,
    ]);
  });

  it("muestra el precio vigente como informativo y no confirmado ni aplicado", async () => {
    const listedProduct = product({ price: "9876543210.12345678" });
    const user = await renderWithLoadedList([listedProduct]);
    await user.click(
      screen.getByRole("button", {
        name: `Agregar ${listedProduct.operationalName} a la composición`,
      }),
    );

    const composition = within(compositionRegion());
    expect(composition.getByText(listedProduct.price)).toBeInTheDocument();
    expect(
      composition.getByText(
        /son informativos y aún no están confirmados ni aplicados/,
      ),
    ).toBeInTheDocument();
  });

  it("mantiene cantidades enteras positivas mediante sus controles", async () => {
    const listedProduct = product();
    const user = await renderWithLoadedList([listedProduct]);
    await user.click(
      screen.getByRole("button", {
        name: `Agregar ${listedProduct.operationalName} a la composición`,
      }),
    );
    await user.click(
      screen.getByRole("button", {
        name: `Aumentar cantidad de ${listedProduct.operationalName}`,
      }),
    );

    const quantity = within(compositionRegion()).getByLabelText(
      `Cantidad de ${listedProduct.operationalName}`,
    );
    expect(quantity).toHaveTextContent(/^2$/);
    expect(Number.isInteger(Number(quantity.textContent))).toBe(true);
    expect(Number(quantity.textContent)).toBeGreaterThan(0);
  });
});

describe("Primera Confirmación autoritativa", () => {
  beforeEach(() => {
    fetchMock.mockReset();
    vi.stubGlobal("fetch", fetchMock);
  });

  function compositionRegion() {
    return screen.getByRole("region", { name: "Composición" });
  }

  it("solo habilita Confirmar con Contexto no vacío y Composición no vacía", async () => {
    const listedProduct = product();
    const user = await renderWithLoadedList([listedProduct]);
    const confirmButton = screen.getByRole("button", {
      name: "Confirmar Composición",
    });

    expect(confirmButton).toBeDisabled();
    await user.type(screen.getByLabelText("Contexto"), "   ");
    await user.click(
      screen.getByRole("button", {
        name: `Agregar ${listedProduct.operationalName} a la composición`,
      }),
    );
    expect(confirmButton).toBeDisabled();

    await user.clear(screen.getByLabelText("Contexto"));
    await user.type(screen.getByLabelText("Contexto"), "Mesa 7");
    expect(confirmButton).toBeEnabled();
  });

  it("envía solo Contexto, ProductId y quantity con Idempotency-Key UUID v4", async () => {
    const listedProduct = product({ price: "99.99" });
    const user = await renderWithLoadedList([listedProduct]);
    fetchMock.mockResolvedValueOnce(
      jsonResponse(confirmationResponse(listedProduct), { status: 201 }),
    );

    await prepareConfirmation(user, listedProduct, 2);
    await user.click(
      screen.getByRole("button", { name: "Confirmar Composición" }),
    );
    await screen.findByText(
      "Primera Confirmación realizada. Se creó el Pedido.",
    );

    const request = confirmationRequestAt(1);
    expect(request.url).toBe("/api/order-operations/first-confirmations");
    expect(request.method).toBe("POST");
    expect(request.body).toEqual({
      context: "Mesa 7",
      items: [{ productId: listedProduct.id, quantity: 2 }],
    });
    expect(request.idempotencyKey).toMatch(
      /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i,
    );
    expect(JSON.stringify(request.body)).not.toMatch(
      /operationalName|price|isAvailable|requiresPreparation/,
    );
  });

  it("muestra el Pedido creado y usa quantity y appliedPrice del response", async () => {
    const listedProduct = product({ price: "99.99" });
    const confirmed = confirmationResponse(listedProduct);
    const user = await renderWithLoadedList([listedProduct]);
    fetchMock.mockResolvedValueOnce(jsonResponse(confirmed, { status: 201 }));

    await prepareConfirmation(user, listedProduct, 2);
    await user.click(
      screen.getByRole("button", { name: "Confirmar Composición" }),
    );

    const summary = await screen.findByRole("region", {
      name: "Pedido recién creado",
    });
    expect(summary).toHaveTextContent(confirmed.operationalReference);
    expect(summary).toHaveTextContent(confirmed.context);
    expect(summary).toHaveTextContent(confirmed.firstIncorporation.id);
    expect(within(summary).getByRole("time")).toHaveAttribute(
      "datetime",
      confirmed.firstIncorporation.confirmedAt,
    );
    expect(summary).toHaveTextContent("2");
    expect(summary).toHaveTextContent("10.50");
    expect(summary).not.toHaveTextContent("99.99");
    expect(
      within(compositionRegion()).getByText("La Composición está vacía."),
    ).toBeInTheDocument();
    expect(
      screen.queryByRole("button", { name: "Reintentar misma Confirmación" }),
    ).not.toBeInTheDocument();
  });

  it("resuelve un Problem Details, conserva la Composición y usa una key nueva después", async () => {
    const listedProduct = product();
    const user = await renderWithLoadedList([listedProduct]);
    fetchMock.mockResolvedValueOnce(
      problemResponse(
        {
          status: 409,
          code: "order_operations.first_confirmation.product_unavailable",
          productId: listedProduct.id,
        },
        409,
      ),
    );

    await prepareConfirmation(user, listedProduct);
    await user.click(
      screen.getByRole("button", { name: "Confirmar Composición" }),
    );
    expect(await screen.findByText(/ya no está disponible/)).toHaveTextContent(
      listedProduct.operationalName,
    );
    const rejectedRequest = confirmationRequestAt(1);
    expect(
      within(compositionRegion()).getByLabelText(
        `Cantidad de ${listedProduct.operationalName}`,
      ),
    ).toHaveTextContent("1");

    fetchMock.mockResolvedValueOnce(
      problemResponse(
        {
          status: 409,
          code: "order_operations.first_confirmation.product_unavailable",
        },
        409,
      ),
    );
    await user.click(
      screen.getByRole("button", { name: "Confirmar Composición" }),
    );
    await screen.findByText(
      "Un Producto de la Composición ya no está disponible.",
    );

    expect(confirmationRequestAt(2).idempotencyKey).not.toBe(
      rejectedRequest.idempotencyKey,
    );
  });

  it("conserva el snapshot incierto, no reintenta y bloquea toda su edición", async () => {
    const listedProduct = product();
    const user = await renderWithLoadedList([listedProduct]);
    fetchMock.mockRejectedValueOnce(new TypeError("Failed to fetch"));

    await prepareConfirmation(user, listedProduct, 2);
    await user.click(
      screen.getByRole("button", { name: "Confirmar Composición" }),
    );

    const uncertain = await screen.findByRole("region", {
      name: "Confirmación con resultado no confirmado",
    });
    expect(uncertain).toHaveTextContent("Mesa 7");
    expect(uncertain).toHaveTextContent("Cantidad: 2");
    expect(screen.getByLabelText("Contexto")).toBeDisabled();
    expect(
      screen.getByRole("button", {
        name: `Agregar ${listedProduct.operationalName} a la composición`,
      }),
    ).toBeDisabled();
    expect(
      screen.getByRole("button", {
        name: `Aumentar cantidad de ${listedProduct.operationalName}`,
      }),
    ).toBeDisabled();

    await new Promise((resolve) => setTimeout(resolve, 100));
    expect(fetchMock).toHaveBeenCalledTimes(2);
  });

  it("reintenta manualmente el mismo snapshot y key y limpia al tener éxito", async () => {
    const listedProduct = product();
    const user = await renderWithLoadedList([listedProduct]);
    fetchMock.mockRejectedValueOnce(new TypeError("Failed to fetch"));

    await prepareConfirmation(user, listedProduct, 2);
    await user.click(
      screen.getByRole("button", { name: "Confirmar Composición" }),
    );
    await screen.findByText(/no sabemos si el Pedido fue creado/);
    const uncertainRequest = confirmationRequestAt(1);

    fetchMock.mockResolvedValueOnce(
      jsonResponse(confirmationResponse(listedProduct), { status: 201 }),
    );
    await user.click(
      screen.getByRole("button", { name: "Reintentar misma Confirmación" }),
    );
    await screen.findByRole("region", { name: "Pedido recién creado" });

    expect(confirmationRequestAt(2)).toEqual(uncertainRequest);
    expect(
      within(compositionRegion()).getByText("La Composición está vacía."),
    ).toBeInTheDocument();
    expect(
      screen.queryByRole("button", { name: "Reintentar misma Confirmación" }),
    ).not.toBeInTheDocument();
  });

  it("descarta la intención incierta, advierte y crea una key nueva después", async () => {
    const listedProduct = product();
    const user = await renderWithLoadedList([listedProduct]);
    fetchMock.mockRejectedValueOnce(new TypeError("Failed to fetch"));

    await prepareConfirmation(user, listedProduct);
    await user.click(
      screen.getByRole("button", { name: "Confirmar Composición" }),
    );
    await screen.findByText(/no sabemos si el Pedido fue creado/);
    const uncertainRequest = confirmationRequestAt(1);

    await user.click(
      screen.getByRole("button", { name: "Descartar intención incierta" }),
    );
    expect(
      screen.getByText(/El resultado previo sigue sin confirmarse/),
    ).toBeInTheDocument();
    expect(screen.getByLabelText("Contexto")).toBeEnabled();
    expect(
      screen.getByRole("button", {
        name: `Aumentar cantidad de ${listedProduct.operationalName}`,
      }),
    ).toBeEnabled();

    fetchMock.mockResolvedValueOnce(
      problemResponse(
        {
          status: 409,
          code: "order_operations.first_confirmation.product_not_current",
        },
        409,
      ),
    );
    await user.click(
      screen.getByRole("button", { name: "Confirmar Composición" }),
    );
    await screen.findByText(/ya no está vigente/);

    expect(confirmationRequestAt(2).idempotencyKey).not.toBe(
      uncertainRequest.idempotencyKey,
    );
  });

  it("presenta la nueva Composición como una operación nueva", async () => {
    const listedProduct = product();
    const user = await renderWithLoadedList([listedProduct]);
    fetchMock.mockResolvedValueOnce(
      jsonResponse(confirmationResponse(listedProduct), { status: 201 }),
    );

    await prepareConfirmation(user, listedProduct, 2);
    await user.click(
      screen.getByRole("button", { name: "Confirmar Composición" }),
    );
    await screen.findByRole("region", { name: "Pedido recién creado" });

    expect(
      within(compositionRegion()).getByText(
        /operación nueva e independiente del Pedido recién creado/,
      ),
    ).toBeInTheDocument();
    expect(screen.queryByText(/Agregar al Pedido/i)).not.toBeInTheDocument();
  });
});
