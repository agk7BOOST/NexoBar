import { useCallback, useEffect, useRef } from "react";
import {
  useInventoryOperationConnectionGeneration,
  useInventoryOperationInvalidation,
} from "../notifications/NotificationSseProvider.tsx";

/** The mounted operational list alone owns this static freshness interest. */
export function InventoryOperationFreshnessSubscription({
  invalidate,
}: {
  invalidate: () => void;
}) {
  const invalidateRef = useRef(invalidate);
  invalidateRef.current = invalidate;
  const invalidateCurrent = useCallback(() => invalidateRef.current(), []);
  useInventoryOperationInvalidation(invalidateCurrent);
  const generation = useInventoryOperationConnectionGeneration();
  useEffect(() => {
    if (generation > 0) invalidateCurrent();
  }, [generation, invalidateCurrent]);
  return null;
}
