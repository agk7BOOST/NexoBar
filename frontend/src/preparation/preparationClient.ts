export interface PreparationWork {
  workId: string;
  preparationResponsibilityId: string;
  operationalReference: string;
  context: string;
  incorporationId: string;
  incorporationOrdinal: number;
  productId: string;
  productOperationalName: string;
  instruction: string | null;
  totalQuantity: number;
  pendingQuantity: number;
  inPreparationQuantity: number;
  readyQuantity: number;
  confirmedAt: string;
}

export class PreparationProblemError extends Error {
  readonly status: number;

  constructor(status: number) {
    super("The Preparation request was rejected.");
    this.name = "PreparationProblemError";
    this.status = status;
  }
}

export async function listPreparationWork(
  preparationResponsibilityId: string,
): Promise<PreparationWork[]> {
  const query = new URLSearchParams({ preparationResponsibilityId });
  const response = await fetch(
    `/api/order-operations/preparation/work?${query.toString()}`,
    { credentials: "same-origin" },
  );
  if (!response.ok) {
    throw new PreparationProblemError(response.status);
  }

  return (await response.json()) as PreparationWork[];
}
