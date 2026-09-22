export interface AvailabilityAdministrationProduct {
  id: string;
  operationalName: string;
  isAvailable: boolean;
}

export interface ChangeProductAvailabilityRequest {
  expectedCurrentAvailability: boolean;
  newAvailability: boolean;
}

export interface ChangeProductAvailabilityResponse {
  productId: string;
  isAvailable: boolean;
}

export interface AvailabilityProblemDetails {
  type?: string;
  title?: string;
  status?: number;
  detail?: string;
  instance?: string;
  code?: string;
  productId?: string;
  currentAvailability?: boolean;
}

export class AvailabilityProblemError extends Error {
  readonly problem: AvailabilityProblemDetails;

  constructor(problem: AvailabilityProblemDetails) {
    super("The Product availability request was rejected.");
    this.name = "AvailabilityProblemError";
    this.problem = problem;
  }
}

export class AvailabilityNetworkError extends Error {
  constructor(options?: ErrorOptions) {
    super("The Product availability request did not receive a response.", options);
    this.name = "AvailabilityNetworkError";
  }
}

export interface ProductAvailabilityIntent {
  readonly productId: string;
  readonly request: ChangeProductAvailabilityRequest;
  readonly endpoint: string;
  readonly body: string;
  readonly idempotencyKey: string;
  readonly antiforgeryToken: string;
}

async function readProblem(response: Response): Promise<AvailabilityProblemDetails> {
  let problem: AvailabilityProblemDetails = {};
  try {
    if (response.headers.get("content-type")?.includes("application/problem+json")) {
      problem = (await response.json()) as AvailabilityProblemDetails;
    }
  } catch {
    // Preserve the authoritative HTTP status when the body is unavailable.
  }
  return { ...problem, status: response.status };
}

async function send(
  request: RequestInfo | URL,
  init?: RequestInit,
): Promise<Response> {
  try {
    return await fetch(request, init);
  } catch (cause) {
    throw new AvailabilityNetworkError({ cause });
  }
}

function mapProduct(value: unknown): AvailabilityAdministrationProduct {
  const product = value as AvailabilityAdministrationProduct;
  return {
    id: product.id,
    operationalName: product.operationalName,
    isAvailable: product.isAvailable,
  };
}

export async function listAvailabilityAdministrationProducts(): Promise<
  AvailabilityAdministrationProduct[]
> {
  const response = await send("/api/catalog/availability-administration-products", {
    credentials: "same-origin",
  });
  if (!response.ok) throw new AvailabilityProblemError(await readProblem(response));
  const products = (await response.json()) as unknown[];
  return products.map(mapProduct);
}

export function createProductAvailabilityIntent(
  productId: string,
  request: ChangeProductAvailabilityRequest,
  idempotencyKey: string,
  antiforgeryToken: string,
): ProductAvailabilityIntent {
  return Object.freeze({
    productId,
    request: Object.freeze({ ...request }),
    endpoint: `/api/catalog/products/${encodeURIComponent(productId)}/availability-changes`,
    body: JSON.stringify(request),
    idempotencyKey,
    antiforgeryToken,
  });
}

export async function sendProductAvailabilityIntent(
  intent: ProductAvailabilityIntent,
): Promise<ChangeProductAvailabilityResponse> {
  const response = await send(intent.endpoint, {
    method: "POST",
    credentials: "same-origin",
    headers: {
      "Content-Type": "application/json",
      "Idempotency-Key": intent.idempotencyKey,
      "X-NexoBar-CSRF": intent.antiforgeryToken,
    },
    body: intent.body,
  });
  if (!response.ok) {
    const problem = await readProblem(response);
    if (response.status === 408 || response.status >= 500) {
      throw new AvailabilityNetworkError();
    }
    throw new AvailabilityProblemError(problem);
  }
  return (await response.json()) as ChangeProductAvailabilityResponse;
}
