import { beforeEach, describe, expect, it, vi } from "vitest";
import {
  AvailabilityProblemError,
  createProductAvailabilityIntent,
  listAvailabilityAdministrationProducts,
  sendProductAvailabilityIntent,
} from "./availabilityClient.ts";

const fetchMock = vi.fn<typeof fetch>();

describe("Availability Intervention client", () => {
  beforeEach(() => {
    fetchMock.mockReset();
    vi.stubGlobal("fetch", fetchMock);
  });

  it("uses the dedicated narrow Product read and maps only its fields", async () => {
    fetchMock.mockResolvedValueOnce(
      new Response(JSON.stringify([{ id: "p1", operationalName: "Agua", isAvailable: true, price: "10" }]), { status: 200 }),
    );

    await expect(listAvailabilityAdministrationProducts()).resolves.toEqual([
      { id: "p1", operationalName: "Agua", isAvailable: true },
    ]);
    expect(fetchMock).toHaveBeenCalledWith(
      "/api/catalog/availability-administration-products",
      { credentials: "same-origin" },
    );
  });

  it.each([
    [true, false],
    [false, true],
  ])("sends exact availability change body %s to %s", async (expected, next) => {
    fetchMock.mockResolvedValueOnce(
      new Response(JSON.stringify({ productId: "p1", isAvailable: next }), { status: 200 }),
    );
    const intent = createProductAvailabilityIntent(
      "p1",
      { expectedCurrentAvailability: expected, newAvailability: next },
      "availability-key",
      "csrf-token",
    );

    await sendProductAvailabilityIntent(intent);
    const [url, init] = fetchMock.mock.calls[0]!;
    expect(url).toBe("/api/catalog/products/p1/availability-changes");
    expect(init?.method).toBe("POST");
    expect(JSON.parse(String(init?.body))).toEqual({
      expectedCurrentAvailability: expected,
      newAvailability: next,
    });
    expect(new Headers(init?.headers).get("Idempotency-Key")).toBe("availability-key");
    expect(new Headers(init?.headers).get("X-NexoBar-CSRF")).toBe("csrf-token");
  });

  it("maps stable Problem Details including the server current availability", async () => {
    fetchMock.mockResolvedValueOnce(
      new Response(JSON.stringify({
        code: "catalog.product.availability_concurrency_conflict",
        productId: "p1",
        currentAvailability: true,
      }), {
        status: 409,
        headers: { "Content-Type": "application/problem+json" },
      }),
    );
    const intent = createProductAvailabilityIntent("p1", {
      expectedCurrentAvailability: false,
      newAvailability: true,
    }, "key", "csrf");

    const error = await sendProductAvailabilityIntent(intent).catch((caught: unknown) => caught);
    expect(error).toMatchObject({
      problem: {
        status: 409,
        code: "catalog.product.availability_concurrency_conflict",
        productId: "p1",
        currentAvailability: true,
      },
    });
    expect(error).toBeInstanceOf(AvailabilityProblemError);
  });

  it("preserves the exact product, body, key and token for a durable retry", async () => {
    fetchMock
      .mockRejectedValueOnce(new TypeError("network lost"))
      .mockResolvedValueOnce(new Response(JSON.stringify({ productId: "p1", isAvailable: false }), { status: 200 }));
    const intent = createProductAvailabilityIntent("p1", {
      expectedCurrentAvailability: true,
      newAvailability: false,
    }, "stable-key", "stable-csrf");

    await expect(sendProductAvailabilityIntent(intent)).rejects.toThrow();
    await sendProductAvailabilityIntent(intent);
    expect(fetchMock.mock.calls[1]?.[0]).toBe(fetchMock.mock.calls[0]?.[0]);
    expect(fetchMock.mock.calls[1]?.[1]?.body).toBe(fetchMock.mock.calls[0]?.[1]?.body);
    expect(new Headers(fetchMock.mock.calls[1]?.[1]?.headers).get("Idempotency-Key")).toBe("stable-key");
  });
});
