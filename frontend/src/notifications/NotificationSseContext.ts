import { createContext } from "react";
import type { NotificationSseTransport } from "./NotificationSseTransport.ts";

export const NotificationSseContext =
  createContext<NotificationSseTransport | null>(null);
