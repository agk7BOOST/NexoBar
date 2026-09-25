export interface ConfiguredContext {
  id: string;
  operationalName: string;
}
export interface CreateContextRequest {
  operationalName: string;
}
export interface ContextProblem {
  status: number;
  code?: string;
}
export class ContextConfigurationError extends Error {
  readonly problem: ContextProblem;
  constructor(problem: ContextProblem) {
    super("Context configuration request failed.");
    this.problem = problem;
  }
}

async function send(path: string, init?: RequestInit): Promise<Response> {
  const response = await fetch(path, { credentials: "same-origin", ...init });
  if (!response.ok) {
    let problem: ContextProblem = { status: response.status };
    if ((response.headers.get("content-type") ?? "").includes("problem+json")) {
      try {
        problem = {
          ...((await response.json()) as ContextProblem),
          status: response.status,
        };
      } catch {
        /* status remains available */
      }
    }
    throw new ContextConfigurationError(problem);
  }
  return response;
}

export async function listConfiguredContexts(): Promise<ConfiguredContext[]> {
  return (await (
    await send("/api/operational-configuration/contexts")
  ).json()) as ConfiguredContext[];
}

export async function createConfiguredContext(
  request: CreateContextRequest,
  idempotencyKey: string,
  antiforgeryToken: string,
): Promise<ConfiguredContext> {
  return (await (
    await send("/api/operational-configuration/contexts", {
      method: "POST",
      headers: {
        "Content-Type": "application/json",
        "Idempotency-Key": idempotencyKey,
        "X-NexoBar-CSRF": antiforgeryToken,
      },
      body: JSON.stringify(request),
    })
  ).json()) as ConfiguredContext;
}
