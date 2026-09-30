import { beforeEach, expect, it, vi } from "vitest";
import {
  ConfigurationLifecycleError,
  executeConfigurationLifecycle,
} from "./configurationLifecycleClient.ts";
const fetchMock = vi.fn();
beforeEach(() => {
  vi.stubGlobal("fetch", fetchMock);
  fetchMock
    .mockReset()
    .mockResolvedValue(
      new Response(
        JSON.stringify({
          id: "id",
          operationalName: "Name",
          isActive: false,
          isDeleted: false,
        }),
        { status: 200 },
      ),
    );
});
it.each(["rename", "retire", "reactivate", "delete"] as const)(
  "sends the explicit %s intention with CSRF and durable key",
  async (action) => {
    const body = {
      expectedCurrentOperationalName: "Name",
      expectedIsActive: true,
      ...(action === "rename" ? { newOperationalName: "Other" } : {}),
    };
    await executeConfigurationLifecycle(
      "contexts",
      "id",
      action,
      body,
      "key",
      "csrf",
    );
    expect(fetchMock).toHaveBeenCalledWith(
      `/api/operational-configuration/contexts/id${action === "delete" ? "" : "/" + (action === "rename" ? "operational-name-changes" : action)}`,
      expect.objectContaining({
        method: action === "delete" ? "DELETE" : "POST",
        credentials: "same-origin",
        body: JSON.stringify(body),
        headers: {
          "Content-Type": "application/json",
          "Idempotency-Key": "key",
          "X-NexoBar-CSRF": "csrf",
        },
      }),
    );
  },
);
it("retains status and dependency detail", async () => {
  fetchMock.mockResolvedValueOnce(
    new Response(JSON.stringify({ code: "dependency", detail: "In use" }), {
      status: 409,
    }),
  );
  await expect(
    executeConfigurationLifecycle(
      "preparation-responsibilities",
      "id",
      "delete",
      { expectedCurrentOperationalName: "Name", expectedIsActive: true },
      "key",
      "csrf",
    ),
  ).rejects.toEqual(
    new ConfigurationLifecycleError(409, "dependency", "In use"),
  );
});
