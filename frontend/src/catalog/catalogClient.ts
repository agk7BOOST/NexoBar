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
  groupId?: string | null;
}

export interface CatalogGroup {
  id: string;
  operationalName: string;
}

export interface CreateGroupRequest {
  operationalName: string;
}

export interface ChangeProductGroupRequest {
  expectedCurrentGroupId: string | null;
  newGroupId: string | null;
}

export interface ChangeProductOperationalNameRequest {
  expectedCurrentOperationalName: string;
  newOperationalName: string;
}

export interface ProductGroupResponse {
  productId: string;
  groupId: string | null;
}

export interface ProductOperationalNameResponse {
  productId: string;
  operationalName: string;
}

export interface ProductLifecycleResponse {
  productId: string;
  isActive: boolean;
  isAvailable: boolean;
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
  currentGroupId?: string | null;
  currentOperationalName?: string;
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

export async function listGroups(): Promise<CatalogGroup[]> {
  const response = await send("/api/catalog/groups", {
    credentials: "same-origin",
  });
  if (!response.ok) throw new CatalogProblemError(await readProblem(response));
  return (await response.json()) as CatalogGroup[];
}

export async function createGroup(
  request: CreateGroupRequest,
  idempotencyKey: string,
  antiforgeryToken: string,
): Promise<CatalogGroup> {
  const response = await send("/api/catalog/groups", {
    method: "POST",
    headers: {
      "Content-Type": "application/json",
      "Idempotency-Key": idempotencyKey,
      "X-NexoBar-CSRF": antiforgeryToken,
    },
    credentials: "same-origin",
    body: JSON.stringify(request),
  });
  if (!response.ok) throw new CatalogProblemError(await readProblem(response));
  return (await response.json()) as CatalogGroup;
}

async function postCatalogCommand<T>(
  path: string,
  request: object | null,
  idempotencyKey: string,
  antiforgeryToken: string,
): Promise<T> {
  const response = await send(path, {
    method: "POST",
    headers: {
      ...(request === null ? {} : { "Content-Type": "application/json" }),
      "Idempotency-Key": idempotencyKey,
      "X-NexoBar-CSRF": antiforgeryToken,
    },
    credentials: "same-origin",
    ...(request === null ? {} : { body: JSON.stringify(request) }),
  });
  if (!response.ok) throw new CatalogProblemError(await readProblem(response));
  return (await response.json()) as T;
}

export function changeProductGroup(
  productId: string,
  request: ChangeProductGroupRequest,
  idempotencyKey: string,
  antiforgeryToken: string,
): Promise<ProductGroupResponse> {
  return postCatalogCommand(
    `/api/catalog/products/${encodeURIComponent(productId)}/group-changes`,
    request,
    idempotencyKey,
    antiforgeryToken,
  );
}

export function changeProductOperationalName(
  productId: string,
  request: ChangeProductOperationalNameRequest,
  idempotencyKey: string,
  antiforgeryToken: string,
): Promise<ProductOperationalNameResponse> {
  return postCatalogCommand(
    `/api/catalog/products/${encodeURIComponent(productId)}/operational-name-changes`,
    request,
    idempotencyKey,
    antiforgeryToken,
  );
}

export function retireProduct(
  productId: string,
  idempotencyKey: string,
  antiforgeryToken: string,
): Promise<ProductLifecycleResponse> {
  return postCatalogCommand(
    `/api/catalog/products/${encodeURIComponent(productId)}/retire`,
    null,
    idempotencyKey,
    antiforgeryToken,
  );
}

export function reactivateProduct(
  productId: string,
  idempotencyKey: string,
  antiforgeryToken: string,
): Promise<ProductLifecycleResponse> {
  return postCatalogCommand(
    `/api/catalog/products/${encodeURIComponent(productId)}/reactivate`,
    null,
    idempotencyKey,
    antiforgeryToken,
  );
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
