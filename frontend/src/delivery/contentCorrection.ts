import type { OrderDeliveryContent } from "./deliveryClient.ts";

// Missing or contradictory reads never authorize an ordinary correction.
export function maximumContentCorrection(
  item: OrderDeliveryContent,
): number | null {
  const q = item.confirmedQuantity;
  const r = item.removedByCorrectionQuantity;
  const c = item.cancelledQuantity ?? 0;
  const f = item.currentFulfillmentQuantity;
  if (
    ![q, r, c, f, item.deliveredQuantity].every(
      (n) => Number.isSafeInteger(n) && n! >= 0,
    ) ||
    q! <= 0 ||
    q! - r! - c !== f ||
    item.totalQuantity !== f ||
    item.deliveredQuantity > f! ||
    item.remainingQuantity !== f! - item.deliveredQuantity
  )
    return null;
  if (!item.requiresPreparationAtConfirmation) {
    return item.pendingQuantity == null &&
      item.inPreparationQuantity == null &&
      item.readyQuantity === null &&
      item.deliverableQuantity === f! - item.deliveredQuantity
      ? f! - item.deliveredQuantity
      : null;
  }
  const pending = item.pendingQuantity;
  const inPreparation = item.inPreparationQuantity;
  const ready = item.readyQuantity;
  if (
    ![pending, inPreparation, ready].every(
      (n) => Number.isSafeInteger(n) && n! >= 0,
    ) ||
    pending! + inPreparation! + ready! !== f ||
    ready! < item.deliveredQuantity ||
    item.deliverableQuantity !== ready! - item.deliveredQuantity
  )
    return null;
  return pending!;
}
