import { useCallback, useEffect, useRef } from "react";
import {
  useOrderConnectionGeneration,
  useOrderInvalidation,
} from "./PreparationSseProvider.tsx";

/** Each mounted Order read keeps its own authoritative state and refresh policy. */
export function ActiveOrderFreshnessSubscription({
  orderId,
  invalidate,
}: {
  orderId: string | null;
  invalidate: () => void;
}) {
  const invalidateRef = useRef(invalidate);
  invalidateRef.current = invalidate;
  const invalidateCurrent = useCallback(() => invalidateRef.current(), []);
  useOrderInvalidation(orderId, invalidateCurrent);
  const generation = useOrderConnectionGeneration();
  useEffect(() => {
    if (orderId !== null && generation > 0) invalidateCurrent();
  }, [generation, invalidateCurrent, orderId]);
  return null;
}
