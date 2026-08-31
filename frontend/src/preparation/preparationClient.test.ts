import { beforeEach, describe, expect, it, vi } from "vitest";
import { listPreparationWork } from "./preparationClient.ts";

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
});
