import { useEffect } from "react";
import {
  usePreparationConnectionGeneration,
  usePreparationDestinationInvalidation,
} from "../notifications/NotificationSseProvider.tsx";

/** Only the actively displayed destination requests freshness interest. */
export function PreparationFreshnessSubscription({ destinationId, invalidate }: {
  destinationId: string;
  invalidate: () => void;
}) {
  usePreparationDestinationInvalidation(destinationId, invalidate);
  const generation = usePreparationConnectionGeneration();
  useEffect(() => {
    if (generation > 0) invalidate();
  }, [generation, invalidate]);
  return null;
}
