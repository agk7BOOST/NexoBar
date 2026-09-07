import type { OrderDeliveryContent } from "./deliveryClient.ts";
import type { PreparationWork } from "../preparation/preparationClient.ts";

// Missing or contradictory reads never authorize an ordinary correction.
export function maximumContentCorrection(
  item: OrderDeliveryContent,
  works: PreparationWork[],
): number | null {
  const q = item.confirmedQuantity;
  const r = item.removedByCorrectionQuantity;
  const f = item.currentFulfillmentQuantity;
  if (
    ![q, r, f, item.deliveredQuantity].every(
      (n) => Number.isSafeInteger(n) && n! >= 0,
    ) ||
    q! <= 0 ||
    q! - r! !== f ||
    item.totalQuantity !== f ||
    item.deliveredQuantity > f! ||
    item.remainingQuantity !== f! - item.deliveredQuantity
  )
    return null;
  const matches = works.filter(
    (work) =>
      work.incorporationId === item.incorporationId &&
      work.contentOrdinal === item.contentOrdinal,
  );
  if (!item.requiresPreparationAtConfirmation) {
    return matches.length === 0 &&
      item.readyQuantity === null &&
      item.deliverableQuantity === f! - item.deliveredQuantity
      ? f! - item.deliveredQuantity
      : null;
  }
  if (matches.length !== 1) return null;
  const work = matches[0];
  if (
    ![
      work.pendingQuantity,
      work.inPreparationQuantity,
      work.readyQuantity,
    ].every((n) => Number.isSafeInteger(n) && n >= 0) ||
    work.totalQuantity !== f ||
    work.pendingQuantity + work.inPreparationQuantity + work.readyQuantity !==
      f ||
    work.readyQuantity !== item.readyQuantity ||
    work.readyQuantity < item.deliveredQuantity ||
    item.deliverableQuantity !== work.readyQuantity - item.deliveredQuantity
  )
    return null;
  return work.pendingQuantity;
}
