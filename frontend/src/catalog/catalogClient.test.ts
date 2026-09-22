import { beforeEach, describe, expect, it, vi } from "vitest";
import {
  CatalogNetworkError,
  CatalogProblemError,
  changeProductPrice,
  changeProductPreparationConfiguration,
  createProduct,
  listOperationalProducts,
  listProducts,
  listPreparationResponsibilityOptions,
  type OperationalProduct,
  type ChangeProductPriceResponse,
  type Product,
} from "./catalogClient.ts";

const fetchMock = vi.fn<typeof fetch>();

describe("Catalog reads", () => {
  beforeEach(() => {
    fetchMock.mockReset();
    vi.stubGlobal("fetch", fetchMock);
  });

  it("uses the secured administrative Product read", async () => {
    fetchMock.mockResolvedValueOnce(new Response("[]", { status: 200 }));
    await expect(listProducts()).resolves.toEqual([]);
    expect(fetchMock).toHaveBeenCalledWith("/api/catalog/products", {
      credentials: "same-origin",
    });
  });

  it("uses the distinct secured operational Product read and narrow model", async () => {
    const products: OperationalProduct[] = [
      {
        id: "product-1",
        operationalName: "Agua",
        price: "10.00",
        isAvailable: true,
      },
    ];
    fetchMock.mockResolvedValueOnce(
      new Response(JSON.stringify(products), { status: 200 }),
    );

    await expect(listOperationalProducts()).resolves.toEqual(products);
    expect(fetchMock).toHaveBeenCalledWith(
      "/api/catalog/operational-products",
      {
        credentials: "same-origin",
      },
    );
  });

  it("uses the Catalog-owned Preparation Responsibility lookup", async () => {
    const options = [{ id: "preparation-1", operationalName: "Cocina" }];
    fetchMock.mockResolvedValueOnce(
      new Response(JSON.stringify(options), { status: 200 }),
    );

    await expect(listPreparationResponsibilityOptions()).resolves.toEqual(
      options,
    );
    expect(fetchMock).toHaveBeenCalledWith(
      "/api/catalog/preparation-responsibilities",
      { credentials: "same-origin" },
    );
  });
});

describe("createProduct", () => {
  beforeEach(() => {
    fetchMock.mockReset();
    vi.stubGlobal("fetch", fetchMock);
  });

  it("envía el contrato de creación con URL y headers exactos", async () => {
    const response: Product = {
      id: "product-1",
      operationalName: "Soda",
      price: "10.50",
      isActive: true,
      isAvailable: true,
      requiresPreparation: false,
      preparationResponsibilityId: null,
    };
    fetchMock.mockResolvedValueOnce(
      new Response(JSON.stringify(response), {
        status: 201,
        headers: { "Content-Type": "application/json" },
      }),
    );

    await expect(
      createProduct(
        {
          operationalName: "Soda",
          price: "10.50",
          requiresPreparation: false,
        },
        "product-creation-key",
        "csrf-token",
      ),
    ).resolves.toEqual(response);

    const [url, init] = fetchMock.mock.calls[0]!;
    expect(url).toBe("/api/catalog/products");
    expect(init?.method).toBe("POST");
    const headers = new Headers(init?.headers);
    expect(headers.get("Content-Type")).toBe("application/json");
    expect(headers.get("Idempotency-Key")).toBe("product-creation-key");
    expect(headers.get("X-NexoBar-CSRF")).toBe("csrf-token");
    expect(init?.credentials).toBe("same-origin");
    expect(JSON.parse(String(init?.body))).toEqual({
      operationalName: "Soda",
      price: "10.50",
      requiresPreparation: false,
    });
  });

  it("convierte una respuesta Problem Details en CatalogProblemError", async () => {
    fetchMock.mockResolvedValueOnce(
      new Response(
        JSON.stringify({
          status: 409,
          code: "catalog.product.operational_name_conflict",
          field: "operationalName",
        }),
        {
          status: 409,
          headers: { "Content-Type": "application/problem+json" },
        },
      ),
    );

    const error = await createProduct(
      {
        operationalName: "Soda",
        price: "10.50",
        requiresPreparation: false,
      },
      "key",
      "csrf-token",
    ).catch((caught: unknown) => caught);

    expect(error).toBeInstanceOf(CatalogProblemError);
    expect((error as CatalogProblemError).problem).toEqual({
      status: 409,
      code: "catalog.product.operational_name_conflict",
      field: "operationalName",
    });
  });

  it("convierte un rechazo de fetch en CatalogNetworkError", async () => {
    fetchMock.mockRejectedValueOnce(new TypeError("Failed to fetch"));

    await expect(
      createProduct(
        {
          operationalName: "Soda",
          price: "10.50",
          requiresPreparation: false,
        },
        "key",
        "csrf-token",
      ),
    ).rejects.toBeInstanceOf(CatalogNetworkError);
  });
});

describe("changeProductPrice", () => {
  beforeEach(() => {
    fetchMock.mockReset();
    vi.stubGlobal("fetch", fetchMock);
  });

  it("envía la ruta, body string y Idempotency-Key exactos", async () => {
    const response: ChangeProductPriceResponse = {
      productId: "product/id",
      price: "0",
    };
    fetchMock.mockResolvedValueOnce(
      new Response(JSON.stringify(response), {
        status: 200,
        headers: { "Content-Type": "application/json" },
      }),
    );

    await expect(
      changeProductPrice(
        "product/id",
        { expectedCurrentPrice: "10.00", newPrice: "0" },
        "idempotency-key",
        "csrf-token",
      ),
    ).resolves.toEqual(response);

    const [url, init] = fetchMock.mock.calls[0]!;
    expect(url).toBe("/api/catalog/products/product%2Fid/price-changes");
    expect(init?.method).toBe("POST");
    expect(new Headers(init?.headers).get("Idempotency-Key")).toBe(
      "idempotency-key",
    );
    expect(new Headers(init?.headers).get("X-NexoBar-CSRF")).toBe("csrf-token");
    expect(init?.credentials).toBe("same-origin");
    expect(JSON.parse(String(init?.body))).toEqual({
      expectedCurrentPrice: "10.00",
      newPrice: "0",
    });
  });

  it("preserva currentPrice del Problem Details", async () => {
    fetchMock.mockResolvedValueOnce(
      new Response(
        JSON.stringify({
          status: 409,
          code: "catalog.product.price_concurrency_conflict",
          currentPrice: "12.00",
        }),
        {
          status: 409,
          headers: { "Content-Type": "application/problem+json" },
        },
      ),
    );

    const error = await changeProductPrice(
      "product",
      { expectedCurrentPrice: "10.00", newPrice: "15.00" },
      "key",
      "csrf-token",
    ).catch((caught: unknown) => caught);

    expect(error).toBeInstanceOf(CatalogProblemError);
    expect((error as CatalogProblemError).problem.currentPrice).toBe("12.00");
  });

  it("distingue un fallo de red de Problem Details", async () => {
    fetchMock.mockRejectedValueOnce(new TypeError("Failed to fetch"));

    await expect(
      changeProductPrice(
        "product",
        { expectedCurrentPrice: "10", newPrice: "12" },
        "key",
        "csrf-token",
      ),
    ).rejects.toBeInstanceOf(CatalogNetworkError);
  });
});

describe("changeProductPreparationConfiguration", () => {
  beforeEach(() => {
    fetchMock.mockReset();
    vi.stubGlobal("fetch", fetchMock);
  });

  it("sends the exact observed destination and durable mutation headers", async () => {
    fetchMock.mockResolvedValueOnce(new Response(null, { status: 200 }));

    await expect(
      changeProductPreparationConfiguration(
        "product/id",
        {
          expectedCurrentPreparationResponsibilityId: "preparation-old",
          newPreparationResponsibilityId: "preparation-new",
        },
        "preparation-key",
        "csrf-token",
      ),
    ).resolves.toBeUndefined();

    const [url, init] = fetchMock.mock.calls[0]!;
    expect(url).toBe(
      "/api/catalog/products/product%2Fid/preparation-configuration-changes",
    );
    expect(init?.method).toBe("POST");
    expect(new Headers(init?.headers).get("Idempotency-Key")).toBe(
      "preparation-key",
    );
    expect(new Headers(init?.headers).get("X-NexoBar-CSRF")).toBe(
      "csrf-token",
    );
    expect(JSON.parse(String(init?.body))).toEqual({
      expectedCurrentPreparationResponsibilityId: "preparation-old",
      newPreparationResponsibilityId: "preparation-new",
    });
  });
});
