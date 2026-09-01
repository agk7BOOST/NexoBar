import { beforeEach, describe, expect, it, vi } from "vitest";
import {
  deliverQuantity,
  DeliveryProblemError,
  getOrderDelivery,
} from "./deliveryClient.ts";

const fetchMock = vi.fn<typeof fetch>();

const deliveryPayload = {
  orderId: "order-id",
  operationalReference: "order/reference",
  currentContext: "Mesa 4",
  contents: [
    {
      incorporationId: "incorporation-1",
      incorporationOrdinal: 1,
      contentOrdinal: 2,
      productId: "product-1",
      productOperationalName: "Hamburguesa",
      instruction: "Sin cebolla",
      totalQuantity: 5,
      requiresPreparationAtConfirmation: true,
      readyQuantity: 3,
      deliveredQuantity: 1,
      deliverableQuantity: 2,
      remainingQuantity: 4,
    },
  ],
};

describe("deliveryClient", () => {
  beforeEach(() => {
    fetchMock.mockReset();
    vi.stubGlobal("fetch", fetchMock);
  });

  it("queries the exact Delivery route with the session cookie implicitly", async () => {
    fetchMock.mockResolvedValueOnce(
      new Response(JSON.stringify(deliveryPayload), {
        status: 200,
        headers: { "Content-Type": "application/json" },
      }),
    );

    await expect(getOrderDelivery("order/reference")).resolves.toEqual(
      deliveryPayload,
    );

    expect(fetchMock).toHaveBeenCalledWith(
      "/api/order-operations/orders/order%2Freference/delivery",
      { credentials: "same-origin" },
    );
  });

  it("parses direct Contents without manufacturing Ready", async () => {
    const directPayload = {
      ...deliveryPayload,
      contents: [
        {
          ...deliveryPayload.contents[0],
          requiresPreparationAtConfirmation: false,
          readyQuantity: null,
        },
      ],
    };
    fetchMock.mockResolvedValueOnce(
      new Response(JSON.stringify(directPayload), { status: 200 }),
    );

    const result = await getOrderDelivery("order-1");

    expect(result.contents[0].readyQuantity).toBeNull();
    expect(result.contents[0].productOperationalName).toBe("Hamburguesa");
  });

  it("sends only the exact target and quantity with idempotency and antiforgery", async () => {
    fetchMock.mockResolvedValueOnce(
      new Response(
        JSON.stringify({
          incorporationId: "incorporation/1",
          contentOrdinal: 7,
          historyId: "history-1",
          occurredAt: "2026-08-31T12:00:00Z",
          deliveredQuantity: 2,
        }),
        { status: 200 },
      ),
    );

    await deliverQuantity({
      incorporationId: "incorporation/1",
      contentOrdinal: 7,
      quantity: 2,
      idempotencyKey: "11111111-1111-4111-8111-111111111111",
      antiforgeryToken: "csrf-1",
    });

    expect(fetchMock).toHaveBeenCalledWith(
      "/api/order-operations/incorporations/incorporation%2F1/contents/7/deliver",
      {
        method: "POST",
        credentials: "same-origin",
        headers: {
          "Content-Type": "application/json",
          "Idempotency-Key": "11111111-1111-4111-8111-111111111111",
          "X-NexoBar-CSRF": "csrf-1",
        },
        body: JSON.stringify({ quantity: 2 }),
      },
    );

    const options = fetchMock.mock.calls[0][1];
    const body = JSON.parse(String(options?.body)) as Record<string, unknown>;
    expect(body).toEqual({ quantity: 2 });
    expect(body).not.toHaveProperty("actor");
    expect(body).not.toHaveProperty("destination");
    expect(body).not.toHaveProperty("productId");
    expect(body).not.toHaveProperty("readyQuantity");
  });

  it("preserves an interpretable Problem response", async () => {
    fetchMock.mockResolvedValueOnce(
      new Response(
        JSON.stringify({
          status: 409,
          code: "order_operations.delivery.idempotency_key_conflict",
        }),
        {
          status: 409,
          headers: { "Content-Type": "application/problem+json" },
        },
      ),
    );

    await expect(
      deliverQuantity({
        incorporationId: "incorporation-1",
        contentOrdinal: 1,
        quantity: 1,
        idempotencyKey: "22222222-2222-4222-8222-222222222222",
        antiforgeryToken: "csrf-2",
      }),
    ).rejects.toMatchObject({
      status: 409,
      problem: {
        code: "order_operations.delivery.idempotency_key_conflict",
      },
    });
  });

  it("does not classify an uninterpretable response as a known rejection", async () => {
    fetchMock.mockResolvedValueOnce(
      new Response("not-json", {
        status: 500,
        headers: { "Content-Type": "text/plain" },
      }),
    );

    const error = await deliverQuantity({
      incorporationId: "incorporation-1",
      contentOrdinal: 1,
      quantity: 1,
      idempotencyKey: "33333333-3333-4333-8333-333333333333",
      antiforgeryToken: "csrf-3",
    }).catch((caught: unknown) => caught);

    expect(error).toBeInstanceOf(Error);
    expect(error).not.toBeInstanceOf(DeliveryProblemError);
  });
});
