export interface CurrentIdentity {
  identityId: string;
  operationalName: string;
}

export interface PreparationDestination {
  preparationResponsibilityId: string;
  operationalName: string;
}

export interface SessionProblemDetails {
  title?: string;
  status?: number;
  detail?: string;
  code?: string;
}

export class SessionProblemError extends Error {
  readonly status: number;
  readonly problem: SessionProblemDetails;

  constructor(status: number, problem: SessionProblemDetails) {
    super("The identity session request was rejected.");
    this.name = "SessionProblemError";
    this.status = status;
    this.problem = problem;
  }
}

let requestToken: string | null = null;

export function discardAntiforgeryToken(): void {
  requestToken = null;
}

async function readProblem(response: Response): Promise<SessionProblemDetails> {
  const contentType = response.headers.get("content-type") ?? "";
  return contentType.includes("application/problem+json")
    ? ((await response.json()) as SessionProblemDetails)
    : { status: response.status };
}

async function requireSuccess(response: Response): Promise<void> {
  if (!response.ok) {
    throw new SessionProblemError(response.status, await readProblem(response));
  }
}

async function getAntiforgeryToken(): Promise<string> {
  if (requestToken !== null) {
    return requestToken;
  }

  const response = await fetch("/api/security/antiforgery", {
    credentials: "same-origin",
  });
  await requireSuccess(response);
  const payload = (await response.json()) as { requestToken: string };
  requestToken = payload.requestToken;
  return requestToken;
}

export async function getCurrentIdentity(): Promise<CurrentIdentity> {
  const response = await fetch("/api/identity-sessions/current", {
    credentials: "same-origin",
  });
  await requireSuccess(response);
  return (await response.json()) as CurrentIdentity;
}

export async function login(
  loginIdentifier: string,
  secret: string,
): Promise<CurrentIdentity> {
  const antiforgeryToken = await getAntiforgeryToken();
  const response = await fetch("/api/identity-sessions", {
    method: "POST",
    credentials: "same-origin",
    headers: {
      "Content-Type": "application/json",
      "X-NexoBar-CSRF": antiforgeryToken,
    },
    body: JSON.stringify({ loginIdentifier, secret }),
  });
  await requireSuccess(response);
  const identity = (await response.json()) as CurrentIdentity;
  discardAntiforgeryToken();
  return identity;
}

export async function logout(): Promise<void> {
  const antiforgeryToken = await getAntiforgeryToken();
  const response = await fetch("/api/identity-sessions/current", {
    method: "DELETE",
    credentials: "same-origin",
    headers: { "X-NexoBar-CSRF": antiforgeryToken },
  });
  await requireSuccess(response);
  discardAntiforgeryToken();
}

export async function listPreparationDestinations(): Promise<
  PreparationDestination[]
> {
  const response = await fetch(
    "/api/identity-sessions/current/preparation-destinations",
    { credentials: "same-origin" },
  );
  await requireSuccess(response);
  return (await response.json()) as PreparationDestination[];
}
