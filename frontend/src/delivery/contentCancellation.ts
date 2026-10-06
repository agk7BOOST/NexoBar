import type { OrderDeliveryContent } from "./deliveryClient.ts";
import { maximumContentCorrection } from "./contentCorrection.ts";

export function maximumContentCancellation(
  item: OrderDeliveryContent,
): number | null {
  // Cancellation requires an authoritative C, including explicit zero.
  if (
    typeof item.cancelledQuantity !== "number" ||
    !Number.isSafeInteger(item.cancelledQuantity) ||
    item.cancelledQuantity < 0
  )
    return null;
  return maximumContentCorrection(item);
}
