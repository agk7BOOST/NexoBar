export interface AdministrativeIdentity {
  identityId: string;
  operationalName: string;
  isActive: boolean;
  hasLocalCredential: boolean;
  loginIdentifier: string | null;
  responsibilities: string[];
  preparationEnablements: string[];
}

export interface CreateIdentityRequest {
  operationalName: string;
}

export interface RenameIdentityRequest {
  operationalName: string;
}

export interface IdentityAdministrationProblemDetails {
  title?: string;
  status?: number;
  detail?: string;
  code?: string;
}

export class IdentityAdministrationProblemError extends Error {
  readonly problem: IdentityAdministrationProblemDetails;

  constructor(problem: IdentityAdministrationProblemDetails) {
    super("The Identity administration request was rejected.");
    this.name = "IdentityAdministrationProblemError";
    this.problem = problem;
  }
}

export class IdentityAdministrationNetworkError extends Error {
  constructor(options?: ErrorOptions) {
    super("The Identity administration request did not receive a response.", options);
    this.name = "IdentityAdministrationNetworkError";
  }
}

async function send(
  request: RequestInfo | URL,
  init?: RequestInit,
): Promise<Response> {
  try {
    return await fetch(request, init);
  } catch (error) {
    throw new IdentityAdministrationNetworkError({ cause: error });
  }
}

async function readProblem(
  response: Response,
): Promise<IdentityAdministrationProblemDetails> {
  const contentType = response.headers.get("content-type") ?? "";
  return contentType.includes("application/problem+json")
    ? ((await response.json()) as IdentityAdministrationProblemDetails)
    : { status: response.status };
}

async function requireSuccess(response: Response): Promise<void> {
  if (!response.ok) {
    throw new IdentityAdministrationProblemError(await readProblem(response));
  }
}

export async function listAdministrativeIdentities(): Promise<
  AdministrativeIdentity[]
> {
  const response = await send("/api/identities", {
    credentials: "same-origin",
  });
  await requireSuccess(response);
  return (await response.json()) as AdministrativeIdentity[];
}

export async function createIdentity(
  request: CreateIdentityRequest,
  idempotencyKey: string,
  antiforgeryToken: string,
): Promise<AdministrativeIdentity> {
  const response = await send("/api/identities", {
    method: "POST",
    credentials: "same-origin",
    headers: {
      "Content-Type": "application/json",
      "Idempotency-Key": idempotencyKey,
      "X-NexoBar-CSRF": antiforgeryToken,
    },
    body: JSON.stringify(request),
  });
  await requireSuccess(response);
  return (await response.json()) as AdministrativeIdentity;
}

export async function renameIdentity(
  identityId: string,
  request: RenameIdentityRequest,
  idempotencyKey: string,
  antiforgeryToken: string,
): Promise<AdministrativeIdentity> {
  const response = await send(
    `/api/identities/${encodeURIComponent(identityId)}/change-operational-name`,
    {
      method: "POST",
      credentials: "same-origin",
      headers: {
        "Content-Type": "application/json",
        "Idempotency-Key": idempotencyKey,
        "X-NexoBar-CSRF": antiforgeryToken,
      },
      body: JSON.stringify(request),
    },
  );
  await requireSuccess(response);
  return (await response.json()) as AdministrativeIdentity;
}
