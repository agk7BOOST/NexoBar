import { beforeEach, describe, expect, it, vi } from "vitest";
import {
  correctPreparationReady,
  correctPreparationStart,
} from "./preparationClient.ts";

const fetchMock = vi.fn<typeof fetch>();

function successResponse() {
  return new Response(
    JSON.stringify({
      workId: "work-1",
      historyId: "history-1",
      occurredAt: "2026-09-10T12:00:00Z",
      totalQuantity: 5,
      pendingQuantity: 2,
      inPreparationQuantity: 2,
      readyQuantity: 1,
    }),
    { status: 200, headers: { "Content-Type": "application/json" } },
  );
}

describe("Preparation Correction client", () => {
  beforeEach(() => {
    fetchMock.mockReset();
    vi.stubGlobal("fetch", fetchMock);
  });

  it.each([
    ["Correct Start", correctPreparationStart, "correct-start"],
    ["Correct Ready", correctPreparationReady, "correct-ready"],
  ])("sends the exact %s intention", async (_name, command, endpoint) => {
    fetchMock.mockResolvedValueOnce(successResponse());

    await command({
      workId: "work/encoded",
      quantity: 2,
      idempotencyKey: "11111111-1111-4111-8111-111111111111",
      antiforgeryToken: "csrf-1",
    });

    expect(fetchMock).toHaveBeenCalledWith(
      `/api/order-operations/preparation/work/work%2Fencoded/${endpoint}`,
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
  });
});
