import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { CatalogPanel } from "./CatalogPanel.tsx";
import {
  CatalogNetworkError,
  CatalogProblemError,
  type Product,
} from "./catalogClient.ts";

const {
  changeProductPreparationConfigurationMock,
  changeProductPriceMock,
  createProductMock,
  deleteProductMock,
  getAntiforgeryTokenMock,
  listPreparationResponsibilityOptionsMock,
  listGroupsMock,
  listProductsMock,
} = vi.hoisted(() => ({
  changeProductPreparationConfigurationMock: vi.fn(),
  changeProductPriceMock: vi.fn(),
  createProductMock: vi.fn(),
  deleteProductMock: vi.fn(),
  getAntiforgeryTokenMock: vi.fn(),
  listPreparationResponsibilityOptionsMock: vi.fn(),
  listGroupsMock: vi.fn(),
  listProductsMock: vi.fn(),
}));

vi.mock("./catalogClient.ts", async (importOriginal) => {
  const original = await importOriginal<typeof import("./catalogClient.ts")>();
  return {
    ...original,
    changeProductPreparationConfiguration:
      changeProductPreparationConfigurationMock,
    changeProductPrice: changeProductPriceMock,
    createProduct: createProductMock,
    deleteProduct: deleteProductMock,
    listPreparationResponsibilityOptions:
      listPreparationResponsibilityOptionsMock,
    listGroups: listGroupsMock,
    listProducts: listProductsMock,
  };
});

describe("CatalogPanel - eliminación definitiva", () => {
  beforeEach(() => {
    deleteProductMock.mockReset();
    getAntiforgeryTokenMock.mockReset();
    getAntiforgeryTokenMock.mockResolvedValue("csrf-token");
    listProductsMock.mockReset();
    listPreparationResponsibilityOptionsMock.mockReset().mockResolvedValue([]);
    listGroupsMock.mockReset().mockResolvedValue([]);
  });

  it("distinguishes Delete from Retire and reloads Catalog after success", async () => {
    listProductsMock
      .mockResolvedValueOnce([product()])
      .mockResolvedValueOnce([]);
    deleteProductMock.mockResolvedValue({ productId: "product-1" });
    const user = userEvent.setup();
    render(<CatalogPanel onUnauthorized={vi.fn()} />);
    const remove = await screen.findByRole("button", {
      name: "Eliminar definitivamente Agua tónica",
    });
    expect(
      screen.getByRole("button", { name: "Retirar Agua tónica" }),
    ).toBeInTheDocument();
    await user.click(remove);
    expect(
      screen.getByText(/nunca participó en un Pedido confirmado/),
    ).toBeInTheDocument();
    await user.click(
      screen.getByRole("button", { name: "Confirmar eliminación definitiva" }),
    );
    await waitFor(() =>
      expect(
        screen.queryByRole("button", {
          name: "Eliminar definitivamente Agua tónica",
        }),
      ).not.toBeInTheDocument(),
    );
    expect(deleteProductMock).toHaveBeenCalledWith(
      "product-1",
      expect.any(String),
      "csrf-token",
    );
  });

  it("explains confirmed participation and does not retire automatically", async () => {
    deleteProductMock.mockRejectedValue(
      new CatalogProblemError({
        status: 409,
        code: "catalog.product.delete.confirmed_participation",
      }),
    );
    const { user } = renderPanel([product()]);
    await user.click(
      screen.getByRole("button", {
        name: "Eliminar definitivamente Agua tónica",
      }),
    );
    await user.click(
      screen.getByRole("button", { name: "Confirmar eliminación definitiva" }),
    );
    expect(
      await screen.findByText(/Podés retirarlo por separado/),
    ).toBeInTheDocument();
    expect(
      screen.getByRole("button", { name: "Retirar Agua tónica" }),
    ).toBeInTheDocument();
  });

  it("prioritizes Retirado and does not suggest retiring it again", async () => {
    deleteProductMock.mockRejectedValue(
      new CatalogProblemError({
        status: 409,
        code: "catalog.product.delete.confirmed_participation",
      }),
    );
    const { user } = renderPanel([
      product({ isActive: false, isAvailable: true }),
    ]);
    const row = screen.getByRole("row", { name: /Agua tónica/ });
    expect(within(row).getByText("Retirado").tagName).toBe("STRONG");
    expect(row).toHaveTextContent("Disponibilidad conservada: Disponible");
    expect(row).toHaveTextContent("No puede agregarse a pedidos");

    await user.click(
      within(row).getByRole("button", {
        name: "Eliminar definitivamente Agua tónica",
      }),
    );
    await user.click(
      screen.getByRole("button", { name: "Confirmar eliminación definitiva" }),
    );
    expect(await screen.findByText(/Ya está retirado/)).toBeVisible();
    expect(screen.queryByText(/Podés retirarlo/)).not.toBeInTheDocument();
  });

  it("keeps the same intent for an uncertain Delete retry", async () => {
    deleteProductMock
      .mockRejectedValueOnce(new CatalogNetworkError())
      .mockResolvedValueOnce({ productId: "product-1" });
    const { user } = renderPanel([product()]);
    await user.click(
      screen.getByRole("button", {
        name: "Eliminar definitivamente Agua tónica",
      }),
    );
    await user.click(
      screen.getByRole("button", { name: "Confirmar eliminación definitiva" }),
    );
    await user.click(
      await screen.findByRole("button", {
        name: "Reintentar misma eliminación",
      }),
    );
    await waitFor(() => expect(deleteProductMock).toHaveBeenCalledTimes(2));
    expect(deleteProductMock.mock.calls[1]).toEqual(
      deleteProductMock.mock.calls[0],
    );
  });
});

vi.mock("../identity/sessionClient.ts", async (importOriginal) => {
  const original =
    await importOriginal<typeof import("../identity/sessionClient.ts")>();
  return { ...original, getAntiforgeryToken: getAntiforgeryTokenMock };
});

function product(overrides?: Partial<Product>): Product {
  return {
    id: "product-1",
    operationalName: "Agua tónica",
    price: "10.00",
    isActive: true,
    isAvailable: true,
    requiresPreparation: false,
    preparationResponsibilityId: null,
    groupId: null,
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
    deleteProductMock.mockReset();
    changeProductPreparationConfigurationMock.mockReset();
    changeProductPriceMock.mockReset();
    getAntiforgeryTokenMock.mockReset();
    getAntiforgeryTokenMock.mockResolvedValue("csrf-token");
    listProductsMock.mockReset();
    listPreparationResponsibilityOptionsMock.mockReset();
    listPreparationResponsibilityOptionsMock.mockResolvedValue([]);
    listGroupsMock.mockReset();
    listGroupsMock.mockResolvedValue([]);
  });

  it("prioriza productos existentes y deja alta y grupos como acciones secundarias", async () => {
    const { user } = renderPanel([product()]);
    const products = screen.getByRole("heading", { name: "Productos" });
    const creation = screen.getByRole("heading", { name: "Crear producto" });
    const groups = screen.getByRole("heading", { name: "Grupos" });
    expect(products.compareDocumentPosition(creation)).toBe(
      Node.DOCUMENT_POSITION_FOLLOWING,
    );
    expect(products.compareDocumentPosition(groups)).toBe(
      Node.DOCUMENT_POSITION_FOLLOWING,
    );

    await user.click(screen.getByRole("button", { name: "Nuevo producto" }));
    expect(creation).toHaveFocus();
    await user.click(
      screen.getByRole("button", { name: "Administrar grupos" }),
    );
    expect(groups).toHaveFocus();
  });

  it("ofrece el alta al inicio cuando el catálogo está vacío", () => {
    renderPanel();
    const creation = screen.getByRole("heading", { name: "Crear producto" });
    const products = screen.getByRole("heading", { name: "Productos" });
    expect(creation.compareDocumentPosition(products)).toBe(
      Node.DOCUMENT_POSITION_FOLLOWING,
    );
  });

  it("presenta el listado vigente sin una acción de Composición", () => {
    const listedProduct = product();
    renderPanel([listedProduct]);

    const products = screen.getByRole("region", {
      name: "Productos",
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

  it("retira un éxito anterior al actualizar productos", async () => {
    createProductMock.mockResolvedValueOnce(product());
    const { user } = renderPanel();
    await fillCreation(user);
    await user.click(screen.getByRole("button", { name: "Crear producto" }));
    expect(
      await screen.findByText("Producto creado correctamente."),
    ).toBeVisible();
    await waitFor(() =>
      expect(
        screen.getByRole("button", { name: "Crear producto" }),
      ).toBeEnabled(),
    );

    await user.click(
      within(screen.getByRole("region", { name: "Productos" })).getByRole(
        "button",
        { name: "Actualizar" },
      ),
    );
    await waitFor(() =>
      expect(
        screen.queryByText("Producto creado correctamente."),
      ).not.toBeInTheDocument(),
    );
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

    await user.click(
      within(screen.getByRole("region", { name: "Productos" })).getByRole(
        "button",
        { name: "Actualizar" },
      ),
    );
    expect(uncertain).toBeInTheDocument();

    createProductMock.mockResolvedValueOnce(product());
    await user.click(
      screen.getByRole("button", { name: "Reintentar esta operación" }),
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
    changeProductPreparationConfigurationMock.mockReset();
    changeProductPriceMock.mockReset();
    getAntiforgeryTokenMock.mockReset();
    getAntiforgeryTokenMock.mockResolvedValue("csrf-token");
    listProductsMock.mockReset();
    listPreparationResponsibilityOptionsMock.mockReset();
    listPreparationResponsibilityOptionsMock.mockResolvedValue([]);
    listGroupsMock.mockReset();
    listGroupsMock.mockResolvedValue([]);
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

describe("CatalogPanel - configuración de preparación", () => {
  const kitchen = { id: "preparation-kitchen", operationalName: "Cocina" };
  const bar = { id: "preparation-bar", operationalName: "Barra" };

  beforeEach(() => {
    changeProductPreparationConfigurationMock.mockReset();
    getAntiforgeryTokenMock.mockReset();
    getAntiforgeryTokenMock.mockResolvedValue("csrf-token");
    listPreparationResponsibilityOptionsMock.mockReset();
    listPreparationResponsibilityOptionsMock.mockResolvedValue([kitchen, bar]);
    listGroupsMock.mockReset();
    listGroupsMock.mockResolvedValue([]);
  });

  async function openPreparationChange(
    user: ReturnType<typeof userEvent.setup>,
    listedProduct: Product,
  ) {
    await user.click(
      screen.getByRole("button", {
        name: `Configurar preparación de ${listedProduct.operationalName}`,
      }),
    );
  }

  it("loads Catalog-owned options and enables preparation with the observed null destination", async () => {
    const listedProduct = product();
    changeProductPreparationConfigurationMock.mockResolvedValueOnce(undefined);
    const { reloadProducts, user } = renderPanel([listedProduct]);
    await waitFor(() =>
      expect(listPreparationResponsibilityOptionsMock).toHaveBeenCalledOnce(),
    );
    await openPreparationChange(user, listedProduct);
    await user.click(screen.getByLabelText("Requiere preparación"));
    await user.selectOptions(
      screen.getByLabelText("Responsabilidad de preparación de destino"),
      kitchen.id,
    );
    await user.click(
      screen.getByRole("button", {
        name: "Confirmar configuración de preparación",
      }),
    );

    const [productId, request, key, token] =
      changeProductPreparationConfigurationMock.mock.calls[0]!;
    expect(productId).toBe(listedProduct.id);
    expect(request).toEqual({
      expectedCurrentPreparationResponsibilityId: null,
      newPreparationResponsibilityId: kitchen.id,
    });
    expect(key).toMatch(
      /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i,
    );
    expect(token).toBe("csrf-token");
    expect(reloadProducts).toHaveBeenCalledOnce();
  });

  it("changes an existing destination and can disable preparation", async () => {
    const listedProduct = product({
      requiresPreparation: true,
      preparationResponsibilityId: kitchen.id,
    });
    changeProductPreparationConfigurationMock
      .mockResolvedValueOnce(undefined)
      .mockResolvedValueOnce(undefined);
    const { user } = renderPanel([listedProduct]);
    await openPreparationChange(user, listedProduct);
    await user.selectOptions(
      screen.getByLabelText("Responsabilidad de preparación de destino"),
      bar.id,
    );
    await user.click(
      screen.getByRole("button", {
        name: "Confirmar configuración de preparación",
      }),
    );
    expect(
      changeProductPreparationConfigurationMock.mock.calls[0]?.[1],
    ).toEqual({
      expectedCurrentPreparationResponsibilityId: kitchen.id,
      newPreparationResponsibilityId: bar.id,
    });

    await openPreparationChange(user, listedProduct);
    await user.click(screen.getByLabelText("Requiere preparación"));
    await user.click(
      screen.getByRole("button", {
        name: "Confirmar configuración de preparación",
      }),
    );
    expect(
      changeProductPreparationConfigurationMock.mock.calls[1]?.[1],
    ).toEqual({
      expectedCurrentPreparationResponsibilityId: kitchen.id,
      newPreparationResponsibilityId: null,
    });
  });

  it("does not permit enabled preparation without a selected destination", async () => {
    const listedProduct = product();
    const { user } = renderPanel([listedProduct]);
    await openPreparationChange(user, listedProduct);
    await user.click(screen.getByLabelText("Requiere preparación"));

    expect(
      screen.getByText(
        "Seleccioná una responsabilidad de preparación antes de confirmar.",
      ),
    ).toBeInTheDocument();
    expect(
      screen.getByRole("button", {
        name: "Confirmar configuración de preparación",
      }),
    ).toBeDisabled();
    expect(changeProductPreparationConfigurationMock).not.toHaveBeenCalled();
  });

  it("reloads authoritative Products after success and after a concurrency conflict", async () => {
    const listedProduct = product();
    changeProductPreparationConfigurationMock
      .mockResolvedValueOnce(undefined)
      .mockRejectedValueOnce(
        new CatalogProblemError({
          status: 409,
          code: "catalog.product.preparation_configuration_concurrency_conflict",
        }),
      );
    const { reloadProducts, user } = renderPanel([listedProduct]);
    await openPreparationChange(user, listedProduct);
    await user.click(screen.getByLabelText("Requiere preparación"));
    await user.selectOptions(
      screen.getByLabelText("Responsabilidad de preparación de destino"),
      kitchen.id,
    );
    await user.click(
      screen.getByRole("button", {
        name: "Confirmar configuración de preparación",
      }),
    );
    await openPreparationChange(user, listedProduct);
    await user.click(screen.getByLabelText("Requiere preparación"));
    await user.selectOptions(
      screen.getByLabelText("Responsabilidad de preparación de destino"),
      kitchen.id,
    );
    await user.click(
      screen.getByRole("button", {
        name: "Confirmar configuración de preparación",
      }),
    );

    expect(
      await screen.findByText(/configuración de preparación cambió/),
    ).toBeInTheDocument();
    expect(reloadProducts).toHaveBeenCalledTimes(2);
    expect(
      screen.queryByRole("region", {
        name: "Configuración de preparación con resultado no confirmado",
      }),
    ).not.toBeInTheDocument();
  });

  it("refreshes Products and options when the selected destination is missing", async () => {
    const listedProduct = product();
    changeProductPreparationConfigurationMock.mockRejectedValueOnce(
      new CatalogProblemError({
        status: 409,
        code: "catalog.product.preparation_responsibility_not_found",
      }),
    );
    const { reloadProducts, user } = renderPanel([listedProduct]);
    await openPreparationChange(user, listedProduct);
    await user.click(screen.getByLabelText("Requiere preparación"));
    await user.selectOptions(
      screen.getByLabelText("Responsabilidad de preparación de destino"),
      kitchen.id,
    );
    await user.click(
      screen.getByRole("button", {
        name: "Confirmar configuración de preparación",
      }),
    );

    expect(
      await screen.findByText(/ya no está disponible/),
    ).toBeInTheDocument();
    expect(reloadProducts).toHaveBeenCalledOnce();
    await waitFor(() =>
      expect(listPreparationResponsibilityOptionsMock).toHaveBeenCalledTimes(2),
    );
  });

  it("retries an uncertain configuration with its exact body and key", async () => {
    const listedProduct = product({
      requiresPreparation: true,
      preparationResponsibilityId: kitchen.id,
    });
    changeProductPreparationConfigurationMock
      .mockRejectedValueOnce(new CatalogNetworkError())
      .mockResolvedValueOnce(undefined);
    const { user } = renderPanel([listedProduct]);
    await openPreparationChange(user, listedProduct);
    await user.selectOptions(
      screen.getByLabelText("Responsabilidad de preparación de destino"),
      bar.id,
    );
    await user.click(
      screen.getByRole("button", {
        name: "Confirmar configuración de preparación",
      }),
    );
    await screen.findByRole("region", {
      name: "Configuración de preparación con resultado no confirmado",
    });
    const firstCall = changeProductPreparationConfigurationMock.mock.calls[0];
    await user.click(
      screen.getByRole("button", {
        name: "Reintentar misma configuración de preparación",
      }),
    );
    expect(changeProductPreparationConfigurationMock.mock.calls[1]).toEqual(
      firstCall,
    );
  });
});

describe("CatalogPanel - lectura administrativa segura", () => {
  beforeEach(() => {
    listProductsMock.mockReset();
    listPreparationResponsibilityOptionsMock.mockReset();
    listPreparationResponsibilityOptionsMock.mockResolvedValue([]);
    listGroupsMock.mockReset();
    listGroupsMock.mockResolvedValue([]);
  });

  it("owns the administrative Product read when mounted", async () => {
    listProductsMock.mockResolvedValueOnce([product()]);
    render(<CatalogPanel onUnauthorized={vi.fn()} />);

    expect(await screen.findByText("Agua tónica")).toBeInTheDocument();
    expect(listProductsMock).toHaveBeenCalledOnce();
  });

  it("retires its state after an administrative 403", async () => {
    listProductsMock.mockRejectedValueOnce(
      new CatalogProblemError({ status: 403, code: "forbidden" }),
    );
    render(<CatalogPanel onUnauthorized={vi.fn()} />);

    await new Promise((resolve) => setTimeout(resolve, 0));
    expect(
      screen.queryByRole("heading", { name: "Crear producto" }),
    ).not.toBeInTheDocument();
  });

  it("fences a late administrative response after the Catalog owner is replaced", async () => {
    let resolveOldRead: ((products: Product[]) => void) | undefined;
    const oldRead = new Promise<Product[]>((resolve) => {
      resolveOldRead = resolve;
    });
    listProductsMock
      .mockReturnValueOnce(oldRead)
      .mockResolvedValueOnce([product({ operationalName: "Soda" })]);
    const { rerender } = render(
      <CatalogPanel key="identity-old" onUnauthorized={vi.fn()} />,
    );

    rerender(<CatalogPanel key="identity-new" onUnauthorized={vi.fn()} />);
    expect(await screen.findByText("Soda")).toBeInTheDocument();
    resolveOldRead?.([product({ operationalName: "Producto anterior" })]);

    await waitFor(() =>
      expect(screen.queryByText("Producto anterior")).not.toBeInTheDocument(),
    );
  });
});
