import { beforeEach, describe, expect, it, vi } from "vitest";
import {
  listPreparationWork,
  markPreparationQuantityReady,
  PreparationProblemError,
  startPreparationQuantity,
} from "./preparationClient.ts";

const fetchMock = vi.fn<typeof fetch>();

describe("preparationClient", () => {
  beforeEach(() => {
    fetchMock.mockReset();
    vi.stubGlobal("fetch", fetchMock);
  });

  it("queries Work for the exact selected destination", async () => {
    fetchMock.mockResolvedValueOnce(
      new Response("[]", {
        status: 200,
        headers: { "Content-Type": "application/json" },
      }),
    );

    await expect(listPreparationWork("destination-1")).resolves.toEqual([]);

    expect(fetchMock).toHaveBeenCalledWith(
      "/api/order-operations/preparation/work?preparationResponsibilityId=destination-1",
      { credentials: "same-origin" },
    );
  });

  it("sends the exact Start intention", async () => {
    fetchMock.mockResolvedValueOnce(
      new Response(
        JSON.stringify({
          workId: "work-1",
          historyId: "history-1",
          occurredAt: "2026-08-31T12:00:00Z",
          totalQuantity: 5,
          pendingQuantity: 3,
          inPreparationQuantity: 2,
          readyQuantity: 0,
        }),
        { status: 200, headers: { "Content-Type": "application/json" } },
      ),
    );

    await startPreparationQuantity({
      workId: "work/encoded",
      quantity: 2,
      idempotencyKey: "11111111-1111-4111-8111-111111111111",
      antiforgeryToken: "csrf-1",
    });

    expect(fetchMock).toHaveBeenCalledWith(
      "/api/order-operations/preparation/work/work%2Fencoded/start",
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

  it("sends the exact Ready intention and preserves Problem Details", async () => {
    fetchMock
      .mockResolvedValueOnce(
        new Response(
          JSON.stringify({
            workId: "work-1",
            historyId: "history-2",
            occurredAt: "2026-08-31T12:05:00Z",
            totalQuantity: 5,
            pendingQuantity: 1,
            inPreparationQuantity: 2,
            readyQuantity: 2,
          }),
          { status: 200, headers: { "Content-Type": "application/json" } },
        ),
      )
      .mockResolvedValueOnce(
        new Response(
          JSON.stringify({
            status: 409,
            code: "order_operations.preparation_ready.idempotency_key_conflict",
          }),
          {
            status: 409,
            headers: { "Content-Type": "application/problem+json" },
          },
        ),
      );

    const command = {
      workId: "work-1",
      quantity: 2,
      idempotencyKey: "22222222-2222-4222-8222-222222222222",
      antiforgeryToken: "csrf-2",
    };
    await expect(markPreparationQuantityReady(command)).resolves.toMatchObject({
      readyQuantity: 2,
    });
    await expect(markPreparationQuantityReady(command)).rejects.toMatchObject({
      status: 409,
      problem: {
        code: "order_operations.preparation_ready.idempotency_key_conflict",
      },
    });

    expect(fetchMock).toHaveBeenNthCalledWith(
      1,
      "/api/order-operations/preparation/work/work-1/ready",
      expect.objectContaining({
        body: JSON.stringify({ quantity: 2 }),
        headers: expect.objectContaining({
          "Idempotency-Key": command.idempotencyKey,
          "X-NexoBar-CSRF": "csrf-2",
        }),
      }),
    );
  });

  it("treats an uninterpretable response as uncertainty, not a known rejection", async () => {
    fetchMock.mockResolvedValueOnce(
      new Response("not-json", {
        status: 500,
        headers: { "Content-Type": "text/plain" },
      }),
    );

    const error = await startPreparationQuantity({
      workId: "work-1",
      quantity: 1,
      idempotencyKey: "33333333-3333-4333-8333-333333333333",
      antiforgeryToken: "csrf-3",
    }).catch((caught: unknown) => caught);

    expect(error).toBeInstanceOf(Error);
    expect(error).not.toBeInstanceOf(PreparationProblemError);
  });
});
