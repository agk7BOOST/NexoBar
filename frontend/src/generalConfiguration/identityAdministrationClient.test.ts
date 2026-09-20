import { beforeEach, describe, expect, it, vi } from "vitest";
import {
  createIdentity,
  IdentityAdministrationNetworkError,
  IdentityAdministrationProblemError,
  listAdministrativeIdentities,
  renameIdentity,
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
