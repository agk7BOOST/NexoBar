import { beforeEach, describe, expect, it, vi } from "vitest";
import {
  confirmFirst,
  confirmSubsequent,
  getOrder,
  OrderOperationsNetworkError,
  OrderOperationsProblemError,
  type FirstConfirmationResponse,
  type OrderResponse,
  type SubsequentConfirmationResponse,
} from "./orderOperationsClient.ts";

const fetchMock = vi.fn<typeof fetch>();

describe("confirmFirst", () => {
  beforeEach(() => {
    fetchMock.mockReset();
    vi.stubGlobal("fetch", fetchMock);
  });

  it("envía exclusivamente Contexto e items con el contrato HTTP esperado", async () => {
    const response: FirstConfirmationResponse = {
      operationalReference: "order-reference",
      context: "Mesa 7",
      firstIncorporation: {
        id: "incorporation-1",
        confirmedAt: "2026-08-29T14:30:00Z",
        items: [{ productId: "product-1", quantity: 2, appliedPrice: "10.50" }],
      },
    };
    fetchMock.mockResolvedValueOnce(
      new Response(JSON.stringify(response), {
        status: 201,
        headers: { "Content-Type": "application/json" },
      }),
    );

    await expect(
      confirmFirst(
        {
          context: "Mesa 7",
          items: [{ productId: "product-1", quantity: 2 }],
        },
        "first-confirmation-key",
      ),
    ).resolves.toEqual(response);

    const [url, init] = fetchMock.mock.calls[0]!;
    expect(url).toBe("/api/order-operations/first-confirmations");
    expect(init?.method).toBe("POST");
    const headers = new Headers(init?.headers);
    expect(headers.get("Content-Type")).toBe("application/json");
    expect(headers.get("Idempotency-Key")).toBe("first-confirmation-key");
    expect(JSON.parse(String(init?.body))).toEqual({
      context: "Mesa 7",
      items: [{ productId: "product-1", quantity: 2 }],
    });
    expect(String(init?.body)).not.toMatch(
      /price|name|availability|requiresPreparation/i,
    );
  });

  it("convierte Problem Details y conserva sus extensiones", async () => {
    fetchMock.mockResolvedValueOnce(
      new Response(
        JSON.stringify({
          status: 409,
          code: "order_operations.first_confirmation.product_unavailable",
          productId: "product-1",
        }),
        {
          status: 409,
          headers: { "Content-Type": "application/problem+json" },
        },
      ),
    );

    const error = await confirmFirst(
      { context: "Mesa 7", items: [{ productId: "product-1", quantity: 1 }] },
      "key",
    ).catch((caught: unknown) => caught);

    expect(error).toBeInstanceOf(OrderOperationsProblemError);
    expect(error).not.toBeInstanceOf(OrderOperationsNetworkError);
    expect((error as OrderOperationsProblemError).problem).toEqual({
      status: 409,
      code: "order_operations.first_confirmation.product_unavailable",
      productId: "product-1",
    });
  });

  it("convierte un rechazo de fetch en incertidumbre de red distinguible", async () => {
    fetchMock.mockRejectedValueOnce(new TypeError("Failed to fetch"));

    const error = await confirmFirst(
      { context: "Mesa 7", items: [{ productId: "product-1", quantity: 1 }] },
      "key",
    ).catch((caught: unknown) => caught);

    expect(error).toBeInstanceOf(OrderOperationsNetworkError);
    expect(error).not.toBeInstanceOf(OrderOperationsProblemError);
  });
});

describe("getOrder", () => {
  beforeEach(() => {
    fetchMock.mockReset();
    vi.stubGlobal("fetch", fetchMock);
  });

  it("usa una ruta relativa segura y conserva los valores textuales del response", async () => {
    const response: OrderResponse = {
      operationalReference: "reference-from-response",
      context: "Mesa 7",
      incorporations: [
        {
          id: "incorporation-1",
          ordinal: 1,
          confirmedAt: "2026-08-29T14:30:00Z",
          items: [
            {
              productId: "product-1",
              quantity: 2,
              appliedPrice: "10.50",
            },
          ],
        },
      ],
    };
    fetchMock.mockResolvedValueOnce(
      new Response(JSON.stringify(response), {
        status: 200,
        headers: { "Content-Type": "application/json" },
      }),
    );

    const result = await getOrder("reference with/slash");

    expect(fetchMock).toHaveBeenCalledWith(
      "/api/order-operations/orders/reference%20with%2Fslash",
    );
    expect(result).toEqual(response);
    expect(result.incorporations[0]?.items[0]?.appliedPrice).toBe("10.50");
  });
});

describe("confirmSubsequent", () => {
  beforeEach(() => {
    fetchMock.mockReset();
    vi.stubGlobal("fetch", fetchMock);
  });

  it("envía solo items a la referencia opaca codificada", async () => {
    const response: SubsequentConfirmationResponse = {
      operationalReference: "reference with/slash",
      incorporation: {
        id: "incorporation-2",
        ordinal: 2,
        confirmedAt: "2026-08-29T16:00:00Z",
        items: [{ productId: "product-1", quantity: 2, appliedPrice: "12.00" }],
      },
    };
    fetchMock.mockResolvedValueOnce(
      new Response(JSON.stringify(response), {
        status: 201,
        headers: { "Content-Type": "application/json" },
      }),
    );

    await expect(
      confirmSubsequent(
        "reference with/slash",
        { items: [{ productId: "product-1", quantity: 2 }] },
        "same-key",
      ),
    ).resolves.toEqual(response);

    const [url, init] = fetchMock.mock.calls[0]!;
    expect(url).toBe(
      "/api/order-operations/orders/reference%20with%2Fslash/confirmations",
    );
    expect(init?.method).toBe("POST");
    expect(new Headers(init?.headers).get("Idempotency-Key")).toBe("same-key");
    expect(JSON.parse(String(init?.body))).toEqual({
      items: [{ productId: "product-1", quantity: 2 }],
    });
    expect(String(init?.body)).not.toMatch(
      /context|price|name|availability|requiresPreparation/i,
    );
  });

  it("distingue Problem Details de incertidumbre de red", async () => {
    fetchMock.mockResolvedValueOnce(
      new Response(
        JSON.stringify({
          status: 409,
          code: "order_operations.subsequent_confirmation.idempotency_key_conflict",
        }),
        {
          status: 409,
          headers: { "Content-Type": "application/problem+json" },
        },
      ),
    );

    await expect(
      confirmSubsequent("reference", { items: [] }, "key"),
    ).rejects.toBeInstanceOf(OrderOperationsProblemError);

    fetchMock.mockRejectedValueOnce(new TypeError("Failed to fetch"));
    await expect(
      confirmSubsequent("reference", { items: [] }, "key"),
    ).rejects.toBeInstanceOf(OrderOperationsNetworkError);
  });
});
