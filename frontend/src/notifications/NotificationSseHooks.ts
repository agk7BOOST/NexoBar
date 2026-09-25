import {
  useCallback,
  useContext,
  useEffect,
  useSyncExternalStore,
} from "react";
import { NotificationSseContext } from "./NotificationSseContext.ts";
import type {
  NotificationSseTransport,
  InventoryOperationChanged,
  OrderChanged,
  PreparationDestinationChanged,
} from "./NotificationSseTransport.ts";

function useNotificationSseTransport(): NotificationSseTransport {
  const transport = useContext(NotificationSseContext);
  if (transport === null) {
    throw new Error("Notification SSE subscriptions require the App provider.");
  }
  return transport;
}

function useConnectionGeneration(
  transport: NotificationSseTransport | null,
): number {
  const subscribe = useCallback(
    (onConnected: () => void) =>
      transport?.onConnected(onConnected) ?? (() => undefined),
    [transport],
  );
  const getSnapshot = useCallback(
    () => transport?.connectionGeneration ?? 0,
    [transport],
  );
  return useSyncExternalStore(subscribe, getSnapshot, getSnapshot);
}

export function usePreparationDestinationInvalidation(
  destinationId: string,
  onInvalidated: (notification: PreparationDestinationChanged) => void,
): void {
  const transport = useNotificationSseTransport();
  useEffect(
    () => transport.subscribe(destinationId, onInvalidated),
    [destinationId, onInvalidated, transport],
  );
}

export function usePreparationConnectionGeneration(): number {
  return useConnectionGeneration(useNotificationSseTransport());
}

export function useOrderInvalidation(
  orderId: string | null,
  onInvalidated: (notification: OrderChanged) => void,
): void {
  const transport = useContext(NotificationSseContext);
  useEffect(() => {
    if (transport === null || orderId === null) return;
    return transport.subscribeOrder(orderId, onInvalidated);
  }, [orderId, onInvalidated, transport]);
}

export function useOrderConnectionGeneration(): number {
  return useConnectionGeneration(useContext(NotificationSseContext));
}

export function useInventoryOperationInvalidation(
  onInvalidated: (notification: InventoryOperationChanged) => void,
): void {
  const transport = useContext(NotificationSseContext);
  useEffect(() => {
    if (transport === null) return;
    return transport.subscribeInventoryOperation(onInvalidated);
  }, [onInvalidated, transport]);
}

export function useInventoryOperationConnectionGeneration(): number {
  return useConnectionGeneration(useContext(NotificationSseContext));
}
