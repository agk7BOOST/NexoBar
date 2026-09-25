import { useEffect, useState, type ReactNode } from "react";
import { NotificationSseContext } from "./NotificationSseContext.ts";
import { NotificationSseTransport } from "./NotificationSseTransport.ts";

interface NotificationSseProviderProps {
  identityId: string | null;
  children: ReactNode;
}

export function NotificationSseProvider({
  identityId,
  children,
}: NotificationSseProviderProps) {
  const [transport] = useState(() => new NotificationSseTransport());

  useEffect(() => {
    transport.setActiveIdentity(identityId);
    return () => transport.setActiveIdentity(null);
  }, [identityId, transport]);

  useEffect(() => () => transport.dispose(), [transport]);

  return (
    <NotificationSseContext.Provider value={transport}>
      {children}
    </NotificationSseContext.Provider>
  );
}
