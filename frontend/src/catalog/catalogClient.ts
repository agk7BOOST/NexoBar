export interface CreateProductRequest {
  operationalName: string;
  price: string;
  requiresPreparation: false;
}

export interface Product {
  id: string;
  operationalName: string;
  price: string;
  isActive: boolean;
  isAvailable: boolean;
  requiresPreparation: boolean;
  preparationResponsibilityId: string | null;
}

/** Catalog-owned narrow lookup used only by Catalog configuration. */
export interface PreparationResponsibilityOption {
  id: string;
  operationalName: string;
}

/**
 * Narrow Product projection owned by Order composition.  It deliberately does
 * not expose administrative or preparation-configuration fields.
 */
export interface OperationalProduct {
  id: string;
  operationalName: string;
  price: string;
  isAvailable: boolean;
}

export interface ChangeProductPriceRequest {
  expectedCurrentPrice: string;
  newPrice: string;
}

export interface ChangeProductPriceResponse {
  productId: string;
  price: string;
}

export interface ChangeProductPreparationConfigurationRequest {
  expectedCurrentPreparationResponsibilityId: string | null;
  newPreparationResponsibilityId: string | null;
}

export interface ProblemDetails {
  type?: string;
  title?: string;
  status?: number;
  detail?: string;
  instance?: string;
  code?: string;
  field?: string;
  productId?: string;
  currentPrice?: string;
}

export class CatalogProblemError extends Error {
  readonly problem: ProblemDetails;

  constructor(problem: ProblemDetails) {
    super("The catalog rejected the request with Problem Details.");
    this.name = "CatalogProblemError";
    this.problem = problem;
  }
}

export class CatalogNetworkError extends Error {
  constructor(options?: ErrorOptions) {
    super("The catalog request did not receive a response.", options);
    this.name = "CatalogNetworkError";
  }
}

async function send(
  request: RequestInfo | URL,
  init?: RequestInit,
): Promise<Response> {
  try {
    return await fetch(request, init);
  } catch (error) {
    throw new CatalogNetworkError({ cause: error });
  }
}

async function readProblem(response: Response): Promise<ProblemDetails> {
  const contentType = response.headers.get("content-type") ?? "";

  if (contentType.includes("application/problem+json")) {
    return (await response.json()) as ProblemDetails;
  }

  return { status: response.status };
}

export async function listProducts(): Promise<Product[]> {
  const response = await send("/api/catalog/products", {
    credentials: "same-origin",
  });

  if (!response.ok) {
    throw new CatalogProblemError(await readProblem(response));
  }

  return (await response.json()) as Product[];
}

export async function listOperationalProducts(): Promise<OperationalProduct[]> {
  const response = await send("/api/catalog/operational-products", {
    credentials: "same-origin",
  });

  if (!response.ok) {
    throw new CatalogProblemError(await readProblem(response));
  }

  return (await response.json()) as OperationalProduct[];
}

export async function listPreparationResponsibilityOptions(): Promise<
  PreparationResponsibilityOption[]
> {
  const response = await send("/api/catalog/preparation-responsibilities", {
    credentials: "same-origin",
  });

  if (!response.ok) {
    throw new CatalogProblemError(await readProblem(response));
  }

  return (await response.json()) as PreparationResponsibilityOption[];
}

export async function createProduct(
  request: CreateProductRequest,
  idempotencyKey: string,
  antiforgeryToken: string,
): Promise<Product> {
  const response = await send("/api/catalog/products", {
    method: "POST",
    headers: {
      "Content-Type": "application/json",
      "Idempotency-Key": idempotencyKey,
      "X-NexoBar-CSRF": antiforgeryToken,
    },
    credentials: "same-origin",
    body: JSON.stringify(request),
  });

  if (!response.ok) {
    throw new CatalogProblemError(await readProblem(response));
  }

  return (await response.json()) as Product;
}

export async function changeProductPrice(
  productId: string,
  request: ChangeProductPriceRequest,
  idempotencyKey: string,
  antiforgeryToken: string,
): Promise<ChangeProductPriceResponse> {
  const response = await send(
    `/api/catalog/products/${encodeURIComponent(productId)}/price-changes`,
    {
      method: "POST",
      headers: {
        "Content-Type": "application/json",
        "Idempotency-Key": idempotencyKey,
        "X-NexoBar-CSRF": antiforgeryToken,
      },
      credentials: "same-origin",
      body: JSON.stringify(request),
    },
  );

  if (!response.ok) {
    throw new CatalogProblemError(await readProblem(response));
  }

  return (await response.json()) as ChangeProductPriceResponse;
}

export async function changeProductPreparationConfiguration(
  productId: string,
  request: ChangeProductPreparationConfigurationRequest,
  idempotencyKey: string,
  antiforgeryToken: string,
): Promise<void> {
  const response = await send(
    `/api/catalog/products/${encodeURIComponent(productId)}/preparation-configuration-changes`,
    {
      method: "POST",
      headers: {
        "Content-Type": "application/json",
        "Idempotency-Key": idempotencyKey,
        "X-NexoBar-CSRF": antiforgeryToken,
      },
      credentials: "same-origin",
      body: JSON.stringify(request),
    },
  );

  if (!response.ok) {
    throw new CatalogProblemError(await readProblem(response));
  }
}
