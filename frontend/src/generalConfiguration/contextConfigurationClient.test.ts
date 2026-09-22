import { beforeEach, describe, expect, it, vi } from "vitest";
import { createConfiguredContext, listConfiguredContexts } from "./contextConfigurationClient.ts";

const request = vi.fn<typeof fetch>();
beforeEach(() => { request.mockReset(); vi.stubGlobal("fetch", request); });

describe("Context configuration client", () => {
  it("lists administrative Contexts from the configured route", async () => {
    request.mockResolvedValueOnce(new Response(JSON.stringify([{ id: "ctx-a", operationalName: "Mesa A" }]), { status: 200 }));
    await expect(listConfiguredContexts()).resolves.toEqual([{ id: "ctx-a", operationalName: "Mesa A" }]);
    expect(request.mock.calls[0]?.[0]).toBe("/api/operational-configuration/contexts");
  });
  it("creates with operationalName and preserves idempotency and antiforgery headers", async () => {
    request.mockResolvedValueOnce(new Response(JSON.stringify({ id: "ctx-server", operationalName: "Barra" }), { status: 201 }));
    await createConfiguredContext({ operationalName: "Barra" }, "key-1", "csrf-token");
    const [path, init] = request.mock.calls[0]!;
    expect(path).toBe("/api/operational-configuration/contexts");
    expect(JSON.parse(String(init?.body))).toEqual({ operationalName: "Barra" });
    expect(init?.headers).toEqual(expect.objectContaining({ "Idempotency-Key": "key-1", "X-NexoBar-CSRF": "csrf-token" }));
  });
});
