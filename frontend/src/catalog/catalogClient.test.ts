import { beforeEach, describe, expect, it, vi } from "vitest";
import {
  CatalogNetworkError,
  CatalogProblemError,
  changeProductPrice,
  createProduct,
  type ChangeProductPriceResponse,
  type Product,
} from "./catalogClient.ts";

const fetchMock = vi.fn<typeof fetch>();

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
      ),
    ).resolves.toEqual(response);

    const [url, init] = fetchMock.mock.calls[0]!;
    expect(url).toBe("/api/catalog/products");
    expect(init?.method).toBe("POST");
    const headers = new Headers(init?.headers);
    expect(headers.get("Content-Type")).toBe("application/json");
    expect(headers.get("Idempotency-Key")).toBe("product-creation-key");
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
      ),
    ).resolves.toEqual(response);

    const [url, init] = fetchMock.mock.calls[0]!;
    expect(url).toBe("/api/catalog/products/product%2Fid/price-changes");
    expect(init?.method).toBe("POST");
    expect(new Headers(init?.headers).get("Idempotency-Key")).toBe(
      "idempotency-key",
    );
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
      ),
    ).rejects.toBeInstanceOf(CatalogNetworkError);
  });
});
