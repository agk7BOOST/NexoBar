import { render } from "@testing-library/react";
import type { ReactElement, ReactNode } from "react";
import { NotificationSseProvider } from "../notifications/NotificationSseProvider.tsx";

/** Mount HTTP-focused panel tests under the same required App provider. */
export function renderWithNotificationSseProvider(ui: ReactElement) {
  return render(ui, {
    wrapper: ({ children }: { children: ReactNode }) => (
      <NotificationSseProvider identityId={null}>
        {children}
      </NotificationSseProvider>
    ),
  });
}
