import { beforeEach, describe, expect, it, vi } from "vitest";
import {
  activateIdentity,
  assignResponsibility,
  createIdentity,
  createPreparationResponsibility,
  deactivateIdentity,
  deleteIdentity,
  grantPreparationEnablement,
  IdentityAdministrationNetworkError,
  IdentityAdministrationProblemError,
  listAdministrativeIdentities,
  listPreparationResponsibilities,
  renameIdentity,
  revokeResponsibility,
  revokePreparationEnablement,
  setLocalCredential,
  type AdministrativeIdentity,
} from "./identityAdministrationClient.ts";

const fetchMock = vi.fn<typeof fetch>();

function identity(overrides?: Partial<AdministrativeIdentity>): AdministrativeIdentity {
  return {
    identityId: "identity-1",
    operationalName: "Ana",
    isActive: true,
    hasLocalCredential: true,
    loginIdentifier: "ana",
    responsibilities: ["GeneralConfiguration"],
    preparationEnablements: [],
    ...overrides,
  };
}

describe("identityAdministrationClient", () => {
  beforeEach(() => {
    fetchMock.mockReset();
    vi.stubGlobal("fetch", fetchMock);
  });

  it("uses the secured administrative Identity read and exact model", async () => {
    const identities = [identity()];
    fetchMock.mockResolvedValueOnce(
      new Response(JSON.stringify(identities), {
        headers: { "Content-Type": "application/json" },
      }),
    );

    await expect(listAdministrativeIdentities()).resolves.toEqual(identities);
    expect(fetchMock).toHaveBeenCalledWith("/api/identities", {
      credentials: "same-origin",
    });
  });

  it("sends definitive Delete with antiforgery and durable intent", async () => {
    fetchMock.mockResolvedValueOnce(new Response(JSON.stringify(identity()), {
      headers: { "Content-Type": "application/json" },
    }));
    await expect(deleteIdentity("identity-1", "key-1", "csrf-1")).resolves.toMatchObject({ identityId: "identity-1" });
    const [url, init] = fetchMock.mock.calls[0]!;
    expect(url).toBe("/api/identities/identity-1");
    expect(init?.method).toBe("DELETE");
    expect(init?.credentials).toBe("same-origin");
    expect(new Headers(init?.headers).get("Idempotency-Key")).toBe("key-1");
    expect(new Headers(init?.headers).get("X-NexoBar-CSRF")).toBe("csrf-1");
    expect(init?.body).toBeUndefined();
  });

  it("uses the secured OperationalConfiguration administrative lookup", async () => {
    const responsibilities = [{ id: "preparation-1", operationalName: "Cocina" }];
    fetchMock.mockResolvedValueOnce(
      new Response(JSON.stringify(responsibilities), {
        headers: { "Content-Type": "application/json" },
      }),
    );

    await expect(listPreparationResponsibilities()).resolves.toEqual(
      responsibilities,
    );
    expect(fetchMock).toHaveBeenCalledWith(
      "/api/operational-configuration/preparation-responsibilities",
      { credentials: "same-origin" },
    );
  });

  it("creates an Identity with the exact body, antiforgery, and idempotency headers", async () => {
    fetchMock.mockResolvedValueOnce(
      new Response(
        JSON.stringify(identity({ operationalName: "Nueva", isActive: false })),
        {
        status: 201,
        headers: { "Content-Type": "application/json" },
        },
      ),
    );

    await expect(
      createIdentity({ operationalName: "Nueva" }, "key-1", "csrf-1"),
    ).resolves.toMatchObject({ operationalName: "Nueva" });

    const [url, init] = fetchMock.mock.calls[0]!;
    expect(url).toBe("/api/identities");
    expect(init?.method).toBe("POST");
    expect(init?.credentials).toBe("same-origin");
    expect(new Headers(init?.headers).get("X-NexoBar-CSRF")).toBe("csrf-1");
    expect(new Headers(init?.headers).get("Idempotency-Key")).toBe("key-1");
    expect(JSON.parse(String(init?.body))).toEqual({ operationalName: "Nueva" });
  });

  it("creates a Preparation Responsibility with the exact durable command", async () => {
    fetchMock.mockResolvedValueOnce(
      new Response(
        JSON.stringify({ id: "preparation-1", operationalName: "Cocina" }),
        { status: 201, headers: { "Content-Type": "application/json" } },
      ),
    );

    await expect(
      createPreparationResponsibility(
        { operationalName: "Cocina" },
        "preparation-key",
        "csrf-token",
      ),
    ).resolves.toEqual({ id: "preparation-1", operationalName: "Cocina" });

    const [url, init] = fetchMock.mock.calls[0]!;
    expect(url).toBe(
      "/api/operational-configuration/preparation-responsibilities",
    );
    expect(init?.method).toBe("POST");
    expect(new Headers(init?.headers).get("Idempotency-Key")).toBe(
      "preparation-key",
    );
    expect(new Headers(init?.headers).get("X-NexoBar-CSRF")).toBe(
      "csrf-token",
    );
    expect(JSON.parse(String(init?.body))).toEqual({
      operationalName: "Cocina",
    });
  });

  it("renames with the exact encoded route and mutation headers", async () => {
    fetchMock.mockResolvedValueOnce(
      new Response(JSON.stringify(identity({ operationalName: "Renombrada" })), {
        headers: { "Content-Type": "application/json" },
      }),
    );

    await renameIdentity("identity/id", { operationalName: "Renombrada" }, "key-2", "csrf-2");

    const [url, init] = fetchMock.mock.calls[0]!;
    expect(url).toBe("/api/identities/identity%2Fid/change-operational-name");
    expect(init?.method).toBe("POST");
    expect(new Headers(init?.headers).get("X-NexoBar-CSRF")).toBe("csrf-2");
    expect(new Headers(init?.headers).get("Idempotency-Key")).toBe("key-2");
    expect(JSON.parse(String(init?.body))).toEqual({
      operationalName: "Renombrada",
    });
  });

  it("activates and deactivates through the exact secured routes", async () => {
    fetchMock
      .mockResolvedValueOnce(
        new Response(JSON.stringify(identity({ isActive: true })), {
          headers: { "Content-Type": "application/json" },
        }),
      )
      .mockResolvedValueOnce(
        new Response(JSON.stringify(identity({ isActive: false })), {
          headers: { "Content-Type": "application/json" },
        }),
      );

    await activateIdentity("identity/id", "activate-key", "csrf-activate");
    await deactivateIdentity("identity/id", "deactivate-key", "csrf-deactivate");

    for (const [call, route, key, token] of [
      [fetchMock.mock.calls[0], "/api/identities/identity%2Fid/activate", "activate-key", "csrf-activate"],
      [fetchMock.mock.calls[1], "/api/identities/identity%2Fid/deactivate", "deactivate-key", "csrf-deactivate"],
    ] as const) {
      const [url, init] = call!;
      expect(url).toBe(route);
      expect(init?.method).toBe("POST");
      expect(init?.credentials).toBe("same-origin");
      expect(new Headers(init?.headers).get("X-NexoBar-CSRF")).toBe(token);
      expect(new Headers(init?.headers).get("Idempotency-Key")).toBe(key);
    }
  });

  it("assigns and revokes exact closed responsibility codes through secured routes", async () => {
    fetchMock
      .mockResolvedValueOnce(
        new Response(JSON.stringify(identity({ responsibilities: ["Preparation"] })), {
          headers: { "Content-Type": "application/json" },
        }),
      )
      .mockResolvedValueOnce(
        new Response(JSON.stringify(identity({ responsibilities: [] })), {
          headers: { "Content-Type": "application/json" },
        }),
      );

    await assignResponsibility("identity-1", "Preparation", "assign-key", "csrf-assign");
    await revokeResponsibility("identity-1", "Preparation", "revoke-key", "csrf-revoke");

    expect(fetchMock.mock.calls[0]?.[0]).toBe(
      "/api/identities/identity-1/responsibilities/Preparation/assign",
    );
    expect(fetchMock.mock.calls[1]?.[0]).toBe(
      "/api/identities/identity-1/responsibilities/Preparation/revoke",
    );
    for (const [call, key, token] of [
      [fetchMock.mock.calls[0], "assign-key", "csrf-assign"],
      [fetchMock.mock.calls[1], "revoke-key", "csrf-revoke"],
    ] as const) {
      const [, init] = call!;
      expect(init?.method).toBe("POST");
      expect(init?.credentials).toBe("same-origin");
      expect(new Headers(init?.headers).get("X-NexoBar-CSRF")).toBe(token);
      expect(new Headers(init?.headers).get("Idempotency-Key")).toBe(key);
    }
  });

  it("grants and revokes preparation enablements through exact secured routes", async () => {
    fetchMock
      .mockResolvedValueOnce(
        new Response(JSON.stringify(identity({ preparationEnablements: ["preparation/id"] })), {
          headers: { "Content-Type": "application/json" },
        }),
      )
      .mockResolvedValueOnce(
        new Response(JSON.stringify(identity({ preparationEnablements: [] })), {
          headers: { "Content-Type": "application/json" },
        }),
      );

    await grantPreparationEnablement(
      "identity/id",
      "preparation/id",
      "grant-key",
      "csrf-grant",
    );
    await revokePreparationEnablement(
      "identity/id",
      "preparation/id",
      "revoke-key",
      "csrf-revoke",
    );

    expect(fetchMock.mock.calls[0]?.[0]).toBe(
      "/api/identities/identity%2Fid/preparation-enablement/preparation%2Fid/grant",
    );
    expect(fetchMock.mock.calls[1]?.[0]).toBe(
      "/api/identities/identity%2Fid/preparation-enablement/preparation%2Fid/revoke",
    );
    for (const [call, key, token] of [
      [fetchMock.mock.calls[0], "grant-key", "csrf-grant"],
      [fetchMock.mock.calls[1], "revoke-key", "csrf-revoke"],
    ] as const) {
      const [, init] = call!;
      expect(init?.method).toBe("POST");
      expect(init?.credentials).toBe("same-origin");
      expect(new Headers(init?.headers).get("X-NexoBar-CSRF")).toBe(token);
      expect(new Headers(init?.headers).get("Idempotency-Key")).toBe(key);
    }
  });

  it("sets a local credential with exact secure request semantics", async () => {
    fetchMock.mockResolvedValueOnce(
      new Response(JSON.stringify(identity({ hasLocalCredential: true, loginIdentifier: "ana" })), {
        headers: { "Content-Type": "application/json" },
      }),
    );
    await setLocalCredential("identity/id", { loginIdentifier: "ana", secret: "new-secret" }, "key-credential", "csrf-credential");
    const [url, init] = fetchMock.mock.calls[0]!;
    expect(url).toBe("/api/identities/identity%2Fid/credential");
    expect(init?.credentials).toBe("same-origin");
    expect(new Headers(init?.headers).get("X-NexoBar-CSRF")).toBe("csrf-credential");
    expect(new Headers(init?.headers).get("Idempotency-Key")).toBe("key-credential");
    expect(JSON.parse(String(init?.body))).toEqual({ loginIdentifier: "ana", secret: "new-secret" });
  });

  it("preserves Problem Details and distinguishes a network failure", async () => {
    fetchMock.mockResolvedValueOnce(
      new Response(JSON.stringify({ status: 409, code: "idempotency_conflict" }), {
        status: 409,
        headers: { "Content-Type": "application/problem+json" },
      }),
    );
    const problem = await createIdentity({ operationalName: "Nueva" }, "key", "csrf").catch(
      (error: unknown) => error,
    );
    expect(problem).toBeInstanceOf(IdentityAdministrationProblemError);
    expect((problem as IdentityAdministrationProblemError).problem.code).toBe(
      "idempotency_conflict",
    );

    fetchMock.mockRejectedValueOnce(new TypeError("Failed to fetch"));
    await expect(listAdministrativeIdentities()).rejects.toBeInstanceOf(
      IdentityAdministrationNetworkError,
    );
  });
});
