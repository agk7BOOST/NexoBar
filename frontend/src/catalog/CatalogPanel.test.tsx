import { render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { CatalogPanel } from "./CatalogPanel.tsx";
import {
  CatalogNetworkError,
  CatalogProblemError,
  type Product,
} from "./catalogClient.ts";

const { changeProductPriceMock, createProductMock } = vi.hoisted(() => ({
  changeProductPriceMock: vi.fn(),
  createProductMock: vi.fn(),
}));

vi.mock("./catalogClient.ts", async (importOriginal) => {
  const original = await importOriginal<typeof import("./catalogClient.ts")>();
  return {
    ...original,
    changeProductPrice: changeProductPriceMock,
    createProduct: createProductMock,
  };
});

function product(overrides?: Partial<Product>): Product {
  return {
    id: "product-1",
    operationalName: "Agua tónica",
    price: "10.00",
    isActive: true,
    isAvailable: true,
    requiresPreparation: false,
    ...overrides,
  };
}

function renderPanel(products: Product[] = []) {
  const reloadProducts = vi.fn().mockResolvedValue(undefined);
  const user = userEvent.setup();
  render(
    <CatalogPanel
      products={products}
      isLoading={false}
      loadError={null}
      reloadProducts={reloadProducts}
    />,
  );
  return { reloadProducts, user };
}

async function fillCreation(
  user: ReturnType<typeof userEvent.setup>,
  name = "Soda",
  price = "10.50",
) {
  await user.type(screen.getByLabelText("Nombre operacional"), name);
  await user.type(screen.getByLabelText("Precio"), price);
}

async function openPriceChange(
  user: ReturnType<typeof userEvent.setup>,
  listedProduct: Product,
  newPrice: string,
) {
  await user.click(
    screen.getByRole("button", {
      name: `Cambiar precio de ${listedProduct.operationalName}`,
    }),
  );
  await user.type(screen.getByLabelText("Nuevo precio"), newPrice);
}

describe("CatalogPanel - alta y listado", () => {
  beforeEach(() => {
    createProductMock.mockReset();
    changeProductPriceMock.mockReset();
  });

  it("presenta el listado vigente sin una acción de Composición", () => {
    const listedProduct = product();
    renderPanel([listedProduct]);

    const products = screen.getByRole("region", {
      name: "Productos vigentes",
    });
    expect(products).toHaveTextContent(listedProduct.operationalName);
    expect(products).toHaveTextContent("10.00");
    expect(products).toHaveTextContent("Disponible");
    expect(
      within(products).queryByRole("button", { name: /Agregar/ }),
    ).not.toBeInTheDocument();
  });

  it("crea con false, precio string, UUID v4 y recarga Catalog", async () => {
    createProductMock.mockResolvedValueOnce(product());
    const { reloadProducts, user } = renderPanel();
    await fillCreation(user);

    await user.click(screen.getByRole("button", { name: "Crear producto" }));
    expect(
      await screen.findByText("Producto creado correctamente."),
    ).toBeInTheDocument();

    const [request, key] = createProductMock.mock.calls[0]!;
    expect(request).toEqual({
      operationalName: "Soda",
      price: "10.50",
      requiresPreparation: false,
    });
    expect(typeof request.price).toBe("string");
    expect(key).toMatch(
      /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i,
    );
    expect(reloadProducts).toHaveBeenCalledOnce();
    expect(screen.getByLabelText("Nombre operacional")).toHaveValue("");
    expect(screen.getByLabelText("Precio")).toHaveValue("");
  });

  it("traduce Problem Details y conserva los datos", async () => {
    createProductMock.mockRejectedValueOnce(
      new CatalogProblemError({
        status: 409,
        code: "catalog.product.operational_name_conflict",
      }),
    );
    const { user } = renderPanel();
    await fillCreation(user);

    await user.click(screen.getByRole("button", { name: "Crear producto" }));

    expect(
      await screen.findByText("Ya existe un producto vigente con ese nombre."),
    ).toBeInTheDocument();
    expect(screen.getByLabelText("Nombre operacional")).toHaveValue("Soda");
  });

  it("congela una creación incierta, no reintenta y reusa request/key manualmente", async () => {
    createProductMock.mockRejectedValueOnce(new CatalogNetworkError());
    const { user } = renderPanel();
    await fillCreation(user);
    await user.click(screen.getByRole("button", { name: "Crear producto" }));

    const uncertain = await screen.findByRole("region", {
      name: "Intención con resultado no confirmado",
    });
    expect(uncertain).toHaveTextContent("Soda");
    expect(uncertain).toHaveTextContent("10.50");
    const firstCall = createProductMock.mock.calls[0];
    await new Promise((resolve) => setTimeout(resolve, 50));
    expect(createProductMock).toHaveBeenCalledOnce();

    createProductMock.mockResolvedValueOnce(product());
    await user.click(
      screen.getByRole("button", { name: "Reintentar misma intención" }),
    );
    await screen.findByText("Producto creado correctamente.");
    expect(createProductMock.mock.calls[1]).toEqual(firstCall);
  });

  it("requiere discard para convertir campos cambiados en una intención nueva", async () => {
    createProductMock.mockRejectedValueOnce(new CatalogNetworkError());
    const { user } = renderPanel();
    await fillCreation(user);
    await user.click(screen.getByRole("button", { name: "Crear producto" }));
    await screen.findByRole("region", {
      name: "Intención con resultado no confirmado",
    });
    const firstKey = createProductMock.mock.calls[0]?.[1];

    await user.clear(screen.getByLabelText("Nombre operacional"));
    await user.type(screen.getByLabelText("Nombre operacional"), "Agua");
    await user.clear(screen.getByLabelText("Precio"));
    await user.type(screen.getByLabelText("Precio"), "20.00");
    expect(
      screen.getByText(/Los cambios del formulario no alteran/),
    ).toBeInTheDocument();

    await user.click(
      screen.getByRole("button", { name: "Descartar e iniciar nueva" }),
    );
    createProductMock.mockResolvedValueOnce(product());
    await user.click(screen.getByRole("button", { name: "Crear producto" }));
    await screen.findByText("Producto creado correctamente.");

    expect(createProductMock.mock.calls[1]?.[0]).toEqual({
      operationalName: "Agua",
      price: "20.00",
      requiresPreparation: false,
    });
    expect(createProductMock.mock.calls[1]?.[1]).not.toBe(firstKey);
  });
});

describe("CatalogPanel - cambio de Precio", () => {
  beforeEach(() => {
    createProductMock.mockReset();
    changeProductPriceMock.mockReset();
  });

  it("congela expectedCurrentPrice, acepta zero string y recarga tras éxito", async () => {
    const listedProduct = product({ price: "10.00" });
    changeProductPriceMock.mockResolvedValueOnce({
      productId: listedProduct.id,
      price: "0",
    });
    const { reloadProducts, user } = renderPanel([listedProduct]);
    await openPriceChange(user, listedProduct, "0");

    expect(
      screen.getByText("Precio vigente observado").parentElement,
    ).toHaveTextContent("10.00");
    await user.click(
      screen.getByRole("button", { name: "Confirmar cambio de Precio" }),
    );

    expect(changeProductPriceMock).toHaveBeenCalledOnce();
    const [productId, request, key] = changeProductPriceMock.mock.calls[0]!;
    expect(productId).toBe(listedProduct.id);
    expect(request).toEqual({
      expectedCurrentPrice: "10.00",
      newPrice: "0",
    });
    expect(typeof request.newPrice).toBe("string");
    expect(key).toMatch(
      /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i,
    );
    expect(
      await screen.findByText(/actualizado correctamente/),
    ).toBeInTheDocument();
    expect(reloadProducts).toHaveBeenCalledOnce();
    expect(screen.queryByLabelText("Nuevo precio")).not.toBeInTheDocument();
  });

  it("muestra currentPrice, termina la intención y recarga en conflicto", async () => {
    const listedProduct = product();
    changeProductPriceMock.mockRejectedValueOnce(
      new CatalogProblemError({
        status: 409,
        code: "catalog.product.price_concurrency_conflict",
        currentPrice: "12.00",
      }),
    );
    const { reloadProducts, user } = renderPanel([listedProduct]);
    await openPriceChange(user, listedProduct, "15.00");
    await user.click(
      screen.getByRole("button", { name: "Confirmar cambio de Precio" }),
    );

    expect(
      await screen.findByText(/Precio vigente es 12.00/),
    ).toBeInTheDocument();
    expect(reloadProducts).toHaveBeenCalledOnce();
    expect(
      screen.queryByRole("region", {
        name: "Cambio de Precio con resultado no confirmado",
      }),
    ).not.toBeInTheDocument();
    expect(screen.queryByLabelText("Nuevo precio")).not.toBeInTheDocument();
  });

  it("congela el cambio incierto, no reintenta y reusa product/request/key", async () => {
    const listedProduct = product();
    changeProductPriceMock.mockRejectedValueOnce(new CatalogNetworkError());
    const { user } = renderPanel([listedProduct]);
    await openPriceChange(user, listedProduct, "12.00");
    await user.click(
      screen.getByRole("button", { name: "Confirmar cambio de Precio" }),
    );

    const uncertain = await screen.findByRole("region", {
      name: "Cambio de Precio con resultado no confirmado",
    });
    expect(uncertain).toHaveTextContent("10.00");
    expect(uncertain).toHaveTextContent("12.00");
    const firstCall = changeProductPriceMock.mock.calls[0];
    await new Promise((resolve) => setTimeout(resolve, 50));
    expect(changeProductPriceMock).toHaveBeenCalledOnce();

    changeProductPriceMock.mockResolvedValueOnce({
      productId: listedProduct.id,
      price: "12.00",
    });
    await user.click(
      screen.getByRole("button", {
        name: "Reintentar mismo cambio de Precio",
      }),
    );
    await screen.findByText(/actualizado correctamente/);
    expect(changeProductPriceMock.mock.calls[1]).toEqual(firstCall);
  });

  it("discard habilita una nueva intención con key nueva", async () => {
    const listedProduct = product();
    changeProductPriceMock.mockRejectedValueOnce(new CatalogNetworkError());
    const { user } = renderPanel([listedProduct]);
    await openPriceChange(user, listedProduct, "12.00");
    await user.click(
      screen.getByRole("button", { name: "Confirmar cambio de Precio" }),
    );
    await screen.findByRole("region", {
      name: "Cambio de Precio con resultado no confirmado",
    });
    const firstKey = changeProductPriceMock.mock.calls[0]?.[2];

    await user.click(
      screen.getByRole("button", { name: "Descartar cambio incierto" }),
    );
    changeProductPriceMock.mockResolvedValueOnce({
      productId: listedProduct.id,
      price: "13.00",
    });
    await openPriceChange(user, listedProduct, "13.00");
    await user.click(
      screen.getByRole("button", { name: "Confirmar cambio de Precio" }),
    );
    await screen.findByText(/actualizado correctamente/);

    expect(changeProductPriceMock.mock.calls[1]?.[2]).not.toBe(firstKey);
  });
});
