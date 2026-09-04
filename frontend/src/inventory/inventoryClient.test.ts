import { beforeEach, describe, expect, it, vi } from "vitest";
import {
  createInventoryItem,
  getInventoryMovementHistory,
  InventoryNetworkError,
  InventoryProblemError,
  listInventoryConfigurationItems,
  listInventoryOperationalItems,
  recordInventoryCount,
  recordInventoryEntry,
  recordInventoryWaste,
  reconcileInventoryCount,
  recordManualInventoryExit,
} from "./inventoryClient.ts";

const fetchMock = vi.fn<typeof fetch>();

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: {
      "Content-Type":
        status >= 400 ? "application/problem+json" : "application/json",
    },
  });
}

describe("inventoryClient", () => {
  beforeEach(() => {
    fetchMock.mockReset();
    vi.stubGlobal("fetch", fetchMock);
  });

  it("reads the exact Configuration route with the implicit auth cookie", async () => {
    fetchMock.mockResolvedValueOnce(
      jsonResponse([
        {
          itemId: "item-1",
          operationalName: "Harina",
          operationalUnit: "kg",
        },
      ]),
    );

    await expect(listInventoryConfigurationItems()).resolves.toEqual([
      {
        itemId: "item-1",
        operationalName: "Harina",
        operationalUnit: "kg",
      },
    ]);
    expect(fetchMock).toHaveBeenCalledWith(
      "/api/inventory/configuration/items",
      { credentials: "same-origin" },
    );
  });

  it("creates an Item with only the exact body and required headers", async () => {
    fetchMock.mockResolvedValueOnce(
      jsonResponse(
        {
          itemId: "item-1",
          operationalName: "Harina",
          operationalUnit: "kg",
          currentRegisteredQuantity: null,
          movementRevision: 0,
        },
        201,
      ),
    );

    await expect(
      createInventoryItem(
        { operationalName: "Harina", operationalUnit: "kg" },
        "11111111-1111-4111-8111-111111111111",
        "csrf-1",
      ),
    ).resolves.toMatchObject({ itemId: "item-1" });

    expect(fetchMock).toHaveBeenCalledWith("/api/inventory/items", {
      method: "POST",
      credentials: "same-origin",
      headers: {
        "Content-Type": "application/json",
        "Idempotency-Key": "11111111-1111-4111-8111-111111111111",
        "X-NexoBar-CSRF": "csrf-1",
      },
      body: JSON.stringify({
        operationalName: "Harina",
        operationalUnit: "kg",
      }),
    });
    const body = JSON.parse(
      String(fetchMock.mock.calls[0]?.[1]?.body),
    ) as Record<string, unknown>;
    expect(body).not.toHaveProperty("quantity");
    expect(body).not.toHaveProperty("productId");
    expect(body).not.toHaveProperty("actor");
  });

  it("retains null, zero, positive and negative operational quantities as strings", async () => {
    fetchMock.mockResolvedValueOnce(
      jsonResponse([
        {
          itemId: "uninitialized",
          operationalName: "Sin conteo",
          operationalUnit: "u",
          currentRegisteredQuantity: null,
          quantityEstablished: false,
          hasNegativeBalanceInconsistency: false,
          asOfMovementRevision: 0,
        },
        {
          itemId: "zero",
          operationalName: "Cero",
          operationalUnit: "u",
          currentRegisteredQuantity: "0",
          quantityEstablished: true,
          hasNegativeBalanceInconsistency: false,
          asOfMovementRevision: 1,
        },
        {
          itemId: "positive",
          operationalName: "Positivo",
          operationalUnit: "kg",
          currentRegisteredQuantity: "10.500",
          quantityEstablished: true,
          hasNegativeBalanceInconsistency: false,
          asOfMovementRevision: 2,
        },
        {
          itemId: "negative",
          operationalName: "Negativo",
          operationalUnit: "l",
          currentRegisteredQuantity: "-2.250",
          quantityEstablished: true,
          hasNegativeBalanceInconsistency: true,
          asOfMovementRevision: 3,
        },
      ]),
    );

    const items = await listInventoryOperationalItems();

    expect(items.map((item) => item.currentRegisteredQuantity)).toEqual([
      null,
      "0",
      "10.500",
      "-2.250",
    ]);
    expect(items[3]?.hasNegativeBalanceInconsistency).toBe(true);
    expect(fetchMock).toHaveBeenCalledWith("/api/inventory/operations/items", {
      credentials: "same-origin",
    });
  });

  it("records Count and Reconciliation with their exact string and observation contracts", async () => {
    fetchMock
      .mockResolvedValueOnce(
        jsonResponse(
          {
            countObservationId: "count-1",
            itemId: "item/id",
            observedQuantity: "0.000000000001",
            observedMovementRevision: 0,
            observedOperationalUnit: "kg",
            observedAt: "2026-09-04T12:00:00Z",
          },
          201,
        ),
      )
      .mockResolvedValueOnce(
        jsonResponse({
          itemId: "item/id",
          countObservationId: "count-1",
          outcome: "reconciled",
          movementId: "movement-1",
          occurredAt: "2026-09-04T12:01:00Z",
          previousRegisteredQuantity: null,
          observedQuantity: "0.000000000001",
          difference: null,
          resultingRegisteredQuantity: "0.000000000001",
          movementRevision: 1,
        }),
      );

    const observation = await recordInventoryCount(
      "item/id",
      { observedQuantity: "0.000000000001" },
      "11111111-1111-4111-8111-111111111111",
      "csrf-1",
    );
    const reconciliation = await reconcileInventoryCount(
      "item/id",
      { countObservationId: observation.countObservationId },
      "22222222-2222-4222-8222-222222222222",
      "csrf-1",
    );

    expect(fetchMock.mock.calls[0]?.[0]).toBe(
      "/api/inventory/items/item%2Fid/counts",
    );
    expect(fetchMock.mock.calls[0]?.[1]?.body).toBe(
      JSON.stringify({ observedQuantity: "0.000000000001" }),
    );
    expect(fetchMock.mock.calls[1]?.[0]).toBe(
      "/api/inventory/items/item%2Fid/reconcile",
    );
    expect(fetchMock.mock.calls[1]?.[1]?.body).toBe(
      JSON.stringify({ countObservationId: "count-1" }),
    );
    expect(observation.observedQuantity).toBe("0.000000000001");
    expect(reconciliation.previousRegisteredQuantity).toBeNull();
    expect(reconciliation.difference).toBeNull();
    expect(reconciliation.resultingRegisteredQuantity).toBe("0.000000000001");
  });

  it.each([
    [recordInventoryEntry, "entries", "Entry", "entry"],
    [recordManualInventoryExit, "manual-exits", "ManualExit", "manual_exit"],
    [recordInventoryWaste, "waste", "Waste", "waste"],
  ] as const)(
    "sends an exact decimal string to the distinct %s movement route",
    async (record, route, wireNature, parsedNature) => {
      fetchMock.mockResolvedValueOnce(
        jsonResponse({
          movementId: "movement-1",
          itemId: "item-1",
          nature: wireNature,
          quantity: "1.250000000000",
          previousRegisteredQuantity: "0.5",
          resultingRegisteredQuantity: "-0.750000000000",
          movementRevision: 2,
          occurredAt: "2026-09-04T12:00:00Z",
        }),
      );

      const result = await record(
        "item-1",
        { quantity: "1.250000000000" },
        "11111111-1111-4111-8111-111111111111",
        "csrf-1",
      );

      expect(fetchMock.mock.calls[0]?.[0]).toBe(
        `/api/inventory/items/item-1/${route}`,
      );
      expect(fetchMock.mock.calls[0]?.[1]?.body).toBe(
        JSON.stringify({ quantity: "1.250000000000" }),
      );
      expect(result.quantity).toBe("1.250000000000");
      expect(result.nature).toBe(parsedNature);
      expect(result.resultingRegisteredQuantity).toBe("-0.750000000000");
    },
  );

  it("reads History with an encoded Item, cursor and limit while retaining decimals", async () => {
    fetchMock.mockResolvedValueOnce(
      jsonResponse({
        itemId: "item/id",
        operationalName: "Aceite",
        operationalUnit: "l",
        movements: [
          {
            movementId: "movement-1",
            movementRevision: 8,
            nature: "manual_exit",
            quantity: "1.125000000000",
            signedEffect: "-1.125000000000",
            previousRegisteredQuantity: "10.500",
            resultingRegisteredQuantity: "9.375000000000",
            occurredAt: "2026-09-04T12:00:00Z",
            actorIdentityId: "actor-id",
            actorOperationalName: "Ana",
            reconciliation: null,
          },
        ],
        nextBeforeRevision: 8,
      }),
    );

    const result = await getInventoryMovementHistory("item/id", {
      beforeRevision: 10,
      limit: 20,
    });

    expect(fetchMock).toHaveBeenCalledWith(
      "/api/inventory/items/item%2Fid/movements?beforeRevision=10&limit=20",
      { credentials: "same-origin" },
    );
    expect(result.movements[0]).toMatchObject({
      quantity: "1.125000000000",
      signedEffect: "-1.125000000000",
      previousRegisteredQuantity: "10.500",
      resultingRegisteredQuantity: "9.375000000000",
    });
  });

  it("parses reconciliation metadata without manufacturing a baseline", async () => {
    fetchMock.mockResolvedValueOnce(
      jsonResponse({
        itemId: "item-1",
        operationalName: "Azúcar",
        operationalUnit: "kg",
        movements: [
          {
            movementId: "movement-1",
            movementRevision: 1,
            nature: "reconciliation",
            quantity: "0",
            signedEffect: null,
            previousRegisteredQuantity: null,
            resultingRegisteredQuantity: "0",
            occurredAt: "2026-09-04T12:00:00Z",
            actorIdentityId: "actor-id",
            actorOperationalName: "Ana",
            reconciliation: {
              observedQuantity: "0",
              difference: null,
              establishedQuantity: true,
            },
          },
        ],
        nextBeforeRevision: null,
      }),
    );

    const result = await getInventoryMovementHistory("item-1");

    expect(result.movements[0]).toMatchObject({
      signedEffect: null,
      previousRegisteredQuantity: null,
      reconciliation: {
        observedQuantity: "0",
        difference: null,
        establishedQuantity: true,
      },
    });
  });

  it("distinguishes Problem Details from a network failure", async () => {
    fetchMock
      .mockResolvedValueOnce(
        jsonResponse({ code: "inventory.operation.forbidden" }, 403),
      )
      .mockRejectedValueOnce(new TypeError("Failed to fetch"));

    await expect(listInventoryOperationalItems()).rejects.toMatchObject({
      status: 403,
      problem: { code: "inventory.operation.forbidden" },
    });
    await expect(listInventoryConfigurationItems()).rejects.toBeInstanceOf(
      InventoryNetworkError,
    );
    expect(InventoryProblemError).toBeDefined();
  });
});
