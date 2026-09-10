import { beforeEach, describe, expect, it, vi } from "vitest";
import { OrderOperationsNetworkError, OrderOperationsProblemError } from "./orderOperationsClient.ts";
import { createInterventionIntent, getInterventionTarget, sendInterventionIntent, type InterventionTarget } from "./operationalInterventionClient.ts";

export const target: InterventionTarget = {
  orderId: "01991e32-2a00-7000-8000-000000000001",
  incorporationId: "01991e32-2a00-7000-8000-000000000002",
  contentOrdinal: 2, workId: "01991e32-2a00-7000-8000-000000000003",
  productId: "01991e32-2a00-7000-8000-000000000004",
  productOperationalName: "Hamburguesa", instruction: "Sin cebolla",
  confirmedQuantity: 12, removedByCorrectionQuantity: 1, cancelledQuantity: 1,
  fulfillmentQuantity: 10, totalQuantity: 10, pendingQuantity: 2,
  inPreparationQuantity: 3, readyQuantity: 5, deliveredQuantity: 2, isFrozen: false,
  intervenableInPreparationQuantity: 3, intervenableReadyQuantity: 3,
};
const fetchMock = vi.fn<typeof fetch>();
function json(value: unknown, status = 200) {
  return new Response(JSON.stringify(value), { status, headers: { "Content-Type": status >= 400 ? "application/problem+json" : "application/json" } });
}
beforeEach(() => { fetchMock.mockReset(); vi.stubGlobal("fetch", fetchMock); });

describe("OperationalIntervention HTTP", () => {
  it("uses only the exact narrow lookup and preserves its quantities", async () => {
    fetchMock.mockResolvedValue(json(target));
    expect(await getInterventionTarget(target)).toEqual(target);
    expect(fetchMock).toHaveBeenCalledExactlyOnceWith(
      `/api/order-operations/intervention/incorporations/${target.incorporationId}/contents/2`, { credentials: "same-origin" });
  });

  it.each(["in-preparation", "ready"] as const)("freezes and resends the exact %s endpoint, target, body, key and token", async stage => {
    const intent = createInterventionIntent(target, stage, 3, "original-csrf");
    expect(Object.isFrozen(intent)).toBe(true);
    expect(intent).toMatchObject({ stage, workId: target.workId, incorporationId: target.incorporationId, contentOrdinal: 2, quantity: 3 });
    expect(intent.idempotencyKey).toMatch(/^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/);
    fetchMock.mockRejectedValueOnce(new TypeError("network"));
    await expect(sendInterventionIntent(intent)).rejects.toBeInstanceOf(OrderOperationsNetworkError);
    fetchMock.mockResolvedValueOnce(json({ totalQuantity: 7 }));
    await sendInterventionIntent(intent);
    expect(fetchMock.mock.calls[1]).toEqual(fetchMock.mock.calls[0]);
    expect(fetchMock.mock.calls[1]).toEqual([
      `/api/order-operations/intervention/work/${target.workId}/${stage}`,
      { method: "POST", credentials: "same-origin", headers: { "Content-Type": "application/json", "Idempotency-Key": intent.idempotencyKey, "X-NexoBar-CSRF": "original-csrf" }, body: '{"quantity":3}' },
    ]);
  });

  it.each([408, 500, 502, 503, 504])("treats HTTP %s as an uncertain command", async status => {
    fetchMock.mockResolvedValue(json({}, status));
    await expect(sendInterventionIntent(createInterventionIntent(target, "ready", 1, "csrf"))).rejects.toBeInstanceOf(OrderOperationsNetworkError);
  });
  it.each([400, 401, 403, 404, 409])("preserves HTTP %s despite malformed or misleading Problem Details", async status => {
    fetchMock.mockResolvedValue(json({ status: 200, code: "rejected" }, status));
    await expect(sendInterventionIntent(createInterventionIntent(target, "ready", 1, "csrf"))).rejects.toMatchObject({ problem: { status, code: "rejected" } });
    fetchMock.mockResolvedValue(new Response("{", { status, headers: { "Content-Type": "application/problem+json" } }));
    await expect(sendInterventionIntent(createInterventionIntent(target, "ready", 1, "csrf"))).rejects.toMatchObject({ problem: { status } });
  });
  it("keeps a known inconsistent-State rejection distinct from uncertainty", async () => {
    fetchMock.mockResolvedValue(json({ code: "order_operations.intervention.state_inconsistent" }, 500));
    await expect(sendInterventionIntent(createInterventionIntent(target, "ready", 1, "csrf"))).rejects.toBeInstanceOf(OrderOperationsProblemError);
  });
  it.each([
    { contentOrdinal: 1 }, { incorporationId: "another" }, { cancelledQuantity: undefined },
    { deliveredQuantity: 6 }, { totalQuantity: 9 }, { pendingQuantity: -1 },
  ])("rejects malformed or mismatched target State %j", async change => {
    fetchMock.mockResolvedValue(json({ ...target, ...change }));
    await expect(getInterventionTarget(target)).rejects.toBeInstanceOf(OrderOperationsNetworkError);
  });
});
