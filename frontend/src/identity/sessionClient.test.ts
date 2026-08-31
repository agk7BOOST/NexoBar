import { beforeEach, describe, expect, it, vi } from "vitest";
import {
  discardAntiforgeryToken,
  getCurrentIdentity,
  listPreparationDestinations,
  login,
  logout,
} from "./sessionClient.ts";

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

describe("sessionClient", () => {
  beforeEach(() => {
    fetchMock.mockReset();
    vi.stubGlobal("fetch", fetchMock);
    discardAntiforgeryToken();
  });

  it("reads the current Identity without exposing session material", async () => {
    fetchMock.mockResolvedValueOnce(
      jsonResponse({ identityId: "identity-1", operationalName: "Ana" }),
    );

    await expect(getCurrentIdentity()).resolves.toEqual({
      identityId: "identity-1",
      operationalName: "Ana",
    });
    expect(fetchMock).toHaveBeenCalledWith("/api/identity-sessions/current", {
      credentials: "same-origin",
    });
  });

  it("obtains antiforgery, sends exact login, and invalidates the cached token", async () => {
    fetchMock
      .mockResolvedValueOnce(jsonResponse({ requestToken: "csrf-1" }))
      .mockResolvedValueOnce(
        jsonResponse({ identityId: "identity-1", operationalName: "Ana" }),
      )
      .mockResolvedValueOnce(jsonResponse({ requestToken: "csrf-2" }))
      .mockResolvedValueOnce(
        jsonResponse({ identityId: "identity-1", operationalName: "Ana" }),
      );

    await login("ana", "secret");
    await login("ana", "secret");

    expect(fetchMock).toHaveBeenNthCalledWith(2, "/api/identity-sessions", {
      method: "POST",
      credentials: "same-origin",
      headers: {
        "Content-Type": "application/json",
        "X-NexoBar-CSRF": "csrf-1",
      },
      body: JSON.stringify({ loginIdentifier: "ana", secret: "secret" }),
    });
    expect(fetchMock).toHaveBeenNthCalledWith(3, "/api/security/antiforgery", {
      credentials: "same-origin",
    });
  });

  it("uses antiforgery for exact logout and discards it after success", async () => {
    fetchMock
      .mockResolvedValueOnce(jsonResponse({ requestToken: "csrf-logout" }))
      .mockResolvedValueOnce(new Response(null, { status: 204 }))
      .mockResolvedValueOnce(jsonResponse({ requestToken: "csrf-next" }))
      .mockResolvedValueOnce(new Response(null, { status: 204 }));

    await logout();
    await logout();

    expect(fetchMock).toHaveBeenNthCalledWith(
      2,
      "/api/identity-sessions/current",
      {
        method: "DELETE",
        credentials: "same-origin",
        headers: { "X-NexoBar-CSRF": "csrf-logout" },
      },
    );
    expect(fetchMock).toHaveBeenNthCalledWith(3, "/api/security/antiforgery", {
      credentials: "same-origin",
    });
  });

  it("preserves 401 and 403 status for coordinator behavior", async () => {
    fetchMock
      .mockResolvedValueOnce(jsonResponse({ code: "invalid" }, 401))
      .mockResolvedValueOnce(jsonResponse({ code: "forbidden" }, 403));

    await expect(getCurrentIdentity()).rejects.toMatchObject({
      status: 401,
    });
    await expect(listPreparationDestinations()).rejects.toMatchObject({
      status: 403,
    });
  });
});
