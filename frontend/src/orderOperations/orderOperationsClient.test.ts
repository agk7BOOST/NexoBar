import { beforeEach, describe, expect, it, vi } from "vitest";
import { getOrder, type OrderResponse } from "./orderOperationsClient.ts";

const fetchMock = vi.fn<typeof fetch>();

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
