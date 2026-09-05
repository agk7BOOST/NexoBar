import {
  act,
  fireEvent,
  render,
  screen,
  waitFor,
  within,
} from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { discardAntiforgeryToken } from "../identity/sessionClient.ts";
import { OrderLookup } from "./OrderLookup.tsx";
import type { OrderResponse } from "./orderOperationsClient.ts";

const reference = "01991e32-2a00-7000-8000-000000000001";
const uuidV4 =
  /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/;
const fetchMock = vi.fn<typeof fetch>();
const mutation = vi.fn<typeof fetch>();
const onUnauthorized = vi.fn();
let current: OrderResponse;
let reads: number;
let failRead: boolean;

function json(value: unknown, status = 200) {
  return new Response(JSON.stringify(value), {
    status,
    headers: { "Content-Type": "application/json" },
  });
}
function rejected(status: number, code = "forbidden") {
  // Intentionally omit status in Problem Details: the HTTP status governs handling.
  return new Response(
    JSON.stringify({ code: `order_operations.liquidation.${code}` }),
    { status, headers: { "Content-Type": "application/problem+json" } },
  );
}
function liquidated(mode = "Simple"): OrderResponse {
  return {
    ...current,
    isLiquidationEligible: false,
    liquidationBlockers: ["already_liquidated"],
    isLiquidated: true,
    isFrozen: true,
    liquidatedAmount: "9007199254740993.25",
    liquidationMode: mode,
    declaredPaymentMedium: mode === "Simple" ? "Bono vecinal" : null,
    isClosureEligible: true,
  };
}
async function open() {
  const user = userEvent.setup();
  render(
    <OrderLookup
      products={[]}
      activeOperationalReference={null}
      onContinueOrder={vi.fn()}
      identityId="identity"
      onUnauthorized={onUnauthorized}
    />,
  );
  await user.type(screen.getByLabelText("Referencia operacional"), reference);
  await user.click(screen.getByRole("button", { name: "Buscar Pedido" }));
  await screen.findByRole("region", { name: "Liquidación y Cierre" });
  return user;
}
async function simple(
  user: ReturnType<typeof userEvent.setup>,
  medium = "Bono vecinal",
) {
  await user.type(screen.getByLabelText("Medio de pago declarado"), medium);
  await user.click(screen.getByRole("button", { name: "Liquidar" }));
}

describe("Liquidación y Cierre desde el Pedido autoritativo", () => {
  beforeEach(() => {
    discardAntiforgeryToken();
    mutation.mockReset();
    onUnauthorized.mockReset();
    reads = 0;
    failRead = false;
    current = {
      operationalReference: reference,
      context: "Mesa 7",
      incorporations: [],
      functionalAmount: "9007199254740993.25",
      isLiquidationEligible: true,
      liquidationBlockers: [],
      isLiquidated: false,
      isFrozen: false,
      liquidatedAmount: null,
      liquidationMode: null,
      declaredPaymentMedium: null,
      isClosureEligible: false,
      isClosed: false,
      closedAt: null,
    };
    fetchMock.mockReset();
    fetchMock.mockImplementation(async (url, init) => {
      if (url === "/api/security/antiforgery")
        return json({ requestToken: "csrf-ending" });
      if (init?.method === "POST") return mutation(url, init);
      reads++;
      if (failRead) throw new TypeError("offline");
      return json(current);
    });
    vi.stubGlobal("fetch", fetchMock);
  });

  it("muestra importe exacto, elegibilidad y consecuencia sin reconstruir Historia", async () => {
    await open();
    expect(screen.getByText("9007199254740993.25")).toBeVisible();
    expect(
      screen.getByText("Después de Liquidar, el Pedido quedará congelado."),
    ).toBeVisible();
    expect(screen.getByRole("button", { name: "Liquidar" })).toBeEnabled();
    expect(
      screen.queryByRole("button", { name: "Cerrar Pedido" }),
    ).not.toBeInTheDocument();
    expect(screen.queryByRole("combobox")).not.toBeInTheDocument();
  });

  it.each([
    ["pending_composition", /Hay una Composición pendiente/],
    ["unresolved_fulfillment", /Quedan contenidos sin entregar/],
    ["already_liquidated", /ya está liquidado/],
    ["state_inconsistent", /Estado del Pedido es inconsistente/],
  ])("explica el bloqueo %s", async (code, wording) => {
    current = {
      ...current,
      isLiquidationEligible: false,
      liquidationBlockers: [code as string],
    };
    await open();
    expect(screen.getByText(wording)).toBeVisible();
    expect(screen.getByRole("button", { name: "Liquidar" })).toBeDisabled();
    expect(
      screen.getByRole("button", {
        name: "Registrar cobro gestionado externamente",
      }),
    ).toBeDisabled();
  });

  it("envía Simple con medio libre, trim exterior, UUIDv4 y CSRF y luego refresca", async () => {
    mutation.mockImplementation(async () => {
      current = liquidated();
      return json({ isFrozen: true, occurredAt: "2026-09-05T17:59:00Z" });
    });
    const user = await open();
    await simple(user, "  Bono  Vecinal / ABC  ");
    await screen.findByText(/Pedido congelado/);
    const [url, init] = mutation.mock.calls[0]!;
    expect(url).toBe(
      `/api/order-operations/orders/${reference}/liquidate-simple`,
    );
    expect(JSON.parse(init!.body as string)).toEqual({
      declaredPaymentMedium: "Bono  Vecinal / ABC",
    });
    const headers = new Headers(init!.headers);
    expect(headers.get("Idempotency-Key")).toMatch(uuidV4);
    expect(headers.get("X-NexoBar-CSRF")).toBe("csrf-ending");
    expect(headers.get("Content-Type")).toBe("application/json");
    expect(init!.credentials).toBe("same-origin");
    expect(reads).toBe(2);
    expect(screen.getByText("Bono vecinal")).toBeVisible();
    expect(screen.getByText("2026-09-05T17:59:00Z")).toHaveAttribute(
      "datetime",
      "2026-09-05T17:59:00Z",
    );
    expect(screen.getByRole("button", { name: "Cerrar Pedido" })).toBeEnabled();
    expect(mutation).toHaveBeenCalledTimes(1); // No automatic Closure.
    await user.click(screen.getByRole("button", { name: "Buscar Pedido" }));
    expect(await screen.findByText("2026-09-05T17:59:00Z")).toBeVisible();
  });

  it.each(["", "   ", "x".repeat(201)])(
    "valida el medio antes de crear una intención (%s)",
    async (medium) => {
      await open();
      const input = screen.getByLabelText("Medio de pago declarado");
      fireEvent.change(input, { target: { value: medium } });
      fireEvent.submit(
        screen.getByRole("form", { name: "Liquidación simple" }),
      );
      expect(await screen.findByRole("alert")).toHaveTextContent(
        "1 a 200 caracteres",
      );
      expect(mutation).not.toHaveBeenCalled();
    },
  );

  it("acepta 200 caracteres más espacios exteriores sin truncar el medio declarado", async () => {
    mutation.mockResolvedValue(json({ occurredAt: "2026-09-05T17:59:00Z" }));
    const user = await open();
    await simple(user, `  ${"x".repeat(200)}  `);
    expect(JSON.parse(mutation.mock.calls[0]![1]!.body as string)).toEqual({
      declaredPaymentMedium: "x".repeat(200),
    });
  });

  it("cobro externo no envía cuerpo ni detalles, aun con un medio escrito en Simple", async () => {
    mutation.mockImplementation(async () => {
      current = liquidated("ExternalCollection");
      return json({ occurredAt: "2026-09-05T17:59:00Z" });
    });
    const user = await open();
    await user.type(
      screen.getByLabelText("Medio de pago declarado"),
      "No enviar",
    );
    await user.click(
      screen.getByRole("button", {
        name: "Registrar cobro gestionado externamente",
      }),
    );
    await screen.findByText(/Pedido congelado/);
    const [url, init] = mutation.mock.calls[0]!;
    expect(url).toBe(
      `/api/order-operations/orders/${reference}/record-external-collection`,
    );
    expect(init).not.toHaveProperty("body");
    expect(new Headers(init!.headers).has("Content-Type")).toBe(false);
    expect(new Headers(init!.headers).get("Idempotency-Key")).toMatch(uuidV4);
    expect(new Headers(init!.headers).get("X-NexoBar-CSRF")).toBe(
      "csrf-ending",
    );
    expect(
      screen.queryByText("Medio de pago declarado"),
    ).not.toBeInTheDocument();
    expect(screen.getByText("Cobro gestionado externamente")).toBeVisible();
  });

  it.each(["network", "timeout", "408", "500", "503"])(
    "conserva la intención Simple incierta ante %s",
    async (failure) => {
      if (failure === "network" || failure === "timeout")
        mutation.mockRejectedValueOnce(new TypeError(failure));
      else mutation.mockResolvedValueOnce(rejected(Number(failure)));
      mutation.mockImplementationOnce(async () => {
        current = liquidated();
        return json({ occurredAt: "2026-09-05T17:59:00Z" });
      });
      const user = await open();
      await simple(user);
      expect(await screen.findByText(/Resultado incierto/)).toBeVisible();
      expect(screen.queryByText(/Pedido congelado/)).not.toBeInTheDocument();
      expect(screen.getByRole("button", { name: "Liquidar" })).toBeDisabled();
      expect(
        screen.getByRole("button", {
          name: "Registrar cobro gestionado externamente",
        }),
      ).toBeDisabled();
      expect(screen.getByLabelText("Medio de pago declarado")).toBeDisabled();
      expect(
        screen.getByRole("button", { name: "Buscar Pedido" }),
      ).toBeDisabled();
      await user.click(
        screen.getByRole("button", { name: "Reintentar misma operación" }),
      );
      await screen.findByText(/Pedido congelado/);
      expect(mutation.mock.calls[1]).toEqual(mutation.mock.calls[0]);
    },
  );

  it.each(["external", "close"])(
    "reintenta exactamente %s sin cuerpo",
    async (kind) => {
      if (kind === "close") current = liquidated();
      mutation
        .mockRejectedValueOnce(new TypeError("offline"))
        .mockImplementationOnce(async () => {
          current =
            kind === "close"
              ? {
                  ...current,
                  isClosed: true,
                  isClosureEligible: false,
                  closedAt: "2026-09-05T18:00:00Z",
                }
              : liquidated("ExternalCollection");
          return json({ occurredAt: "2026-09-05T17:59:00Z" });
        });
      const user = await open();
      await user.click(
        screen.getByRole("button", {
          name:
            kind === "close"
              ? "Cerrar Pedido"
              : "Registrar cobro gestionado externamente",
        }),
      );
      await screen.findByText(/Resultado incierto/);
      expect(screen.queryByText("Pedido cerrado")).not.toBeInTheDocument();
      await user.click(
        screen.getByRole("button", { name: "Reintentar misma operación" }),
      );
      await waitFor(() => expect(mutation).toHaveBeenCalledTimes(2));
      expect(mutation.mock.calls[1]).toEqual(mutation.mock.calls[0]);
      const [url, init] = mutation.mock.calls[0]!;
      expect(init).not.toHaveProperty("body");
      expect(new Headers(init!.headers).get("Idempotency-Key")).toMatch(uuidV4);
      expect(new Headers(init!.headers).get("X-NexoBar-CSRF")).toBe(
        "csrf-ending",
      );
      if (kind === "close") {
        expect(url).toBe(`/api/orders/${reference}/close`);
        expect(await screen.findByText("Pedido cerrado")).toBeVisible();
        expect(screen.getByText("2026-09-05T18:00:00Z")).toHaveAttribute(
          "datetime",
          "2026-09-05T18:00:00Z",
        );
        expect(
          screen.queryByRole("button", {
            name: /reabrir|reopen|Cerrar Pedido|Continuar este Pedido/i,
          }),
        ).not.toBeInTheDocument();
        expect(
          screen.getByRole("button", { name: "Buscar Pedido" }),
        ).toBeEnabled();
      }
    },
  );

  it("401 limpia antiforgery y usa el reset de autenticación existente", async () => {
    mutation.mockResolvedValue(rejected(401));
    const user = await open();
    await simple(user);
    await waitFor(() => expect(onUnauthorized).toHaveBeenCalledOnce());
    await simple(user, " Otro");
    expect(
      fetchMock.mock.calls.filter(
        ([url]) => url === "/api/security/antiforgery",
      ),
    ).toHaveLength(2);
  });

  it("403 conserva Identity y muestra autorización", async () => {
    mutation.mockResolvedValue(rejected(403));
    const user = await open();
    await simple(user);
    expect(await screen.findByRole("alert")).toHaveTextContent(
      "Identity no tiene autorización",
    );
    expect(onUnauthorized).not.toHaveBeenCalled();
    expect(
      screen.queryByRole("button", { name: "Reintentar misma operación" }),
    ).not.toBeInTheDocument();
  });

  it.each([
    "pending_composition",
    "unresolved_fulfillment",
    "idempotency_key_conflict",
    "frozen",
  ])("409 %s resuelve la intención y refresca", async (code) => {
    mutation.mockImplementation(async () => {
      current = liquidated();
      return rejected(409, code);
    });
    const user = await open();
    await simple(user);
    await screen.findByText(/Pedido congelado/);
    expect(reads).toBe(2);
    expect(
      screen.queryByRole("button", { name: "Reintentar misma operación" }),
    ).not.toBeInTheDocument();
    expect(screen.getByRole("alert")).toBeVisible();
  });

  it("no duplica submit ni muestra Freeze antes del GET, aunque el comando responda Frozen", async () => {
    let resolve!: (value: Response) => void;
    mutation.mockReturnValue(
      new Promise<Response>((done) => {
        resolve = done;
      }),
    );
    await open();
    fireEvent.change(screen.getByLabelText("Medio de pago declarado"), {
      target: { value: "Bono" },
    });
    const form = screen.getByRole("form", { name: "Liquidación simple" });
    fireEvent.submit(form);
    fireEvent.submit(form);
    await waitFor(() => expect(mutation).toHaveBeenCalledTimes(1));
    expect(screen.queryByText(/Pedido congelado/)).not.toBeInTheDocument();
    failRead = true;
    await act(async () =>
      resolve(json({ isFrozen: true, occurredAt: "2026-09-05T17:59:00Z" })),
    );
    expect(
      await screen.findByText(/No se pudo actualizar el Pedido/),
    ).toBeVisible();
    expect(screen.queryByText(/Pedido congelado/)).not.toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Liquidar" })).toBeDisabled();
    failRead = false;
    current = liquidated();
    fireEvent.click(
      screen.getByRole("button", { name: "Actualizar Estado del Pedido" }),
    );
    await screen.findByText(/Pedido congelado/);
    expect(mutation).toHaveBeenCalledTimes(1);
  });

  it.each([401, 403, 409])(
    "Cierre trata HTTP %s sin perder la distinción de autorización",
    async (status) => {
      current = liquidated();
      mutation.mockResolvedValue(
        rejected(status, status === 409 ? "not_liquidated" : "forbidden"),
      );
      const user = await open();
      await user.click(screen.getByRole("button", { name: "Cerrar Pedido" }));
      if (status === 401) {
        await waitFor(() => expect(onUnauthorized).toHaveBeenCalledOnce());
      } else {
        expect(await screen.findByRole("alert")).toHaveTextContent(
          status === 403 ? "Identity no tiene autorización" : "debe liquidarse",
        );
        expect(onUnauthorized).not.toHaveBeenCalled();
      }
      if (status === 409) expect(reads).toBe(2);
      expect(screen.queryByText("Pedido cerrado")).not.toBeInTheDocument();
    },
  );

  it("no muestra Cierre optimista y conserva bloqueos si falla el refresco", async () => {
    current = liquidated();
    mutation.mockImplementation(async () => {
      failRead = true;
      return json({ isClosed: true });
    });
    const user = await open();
    await user.click(screen.getByRole("button", { name: "Cerrar Pedido" }));
    await screen.findByText(/No se pudo actualizar el Pedido/);
    expect(screen.queryByText("Pedido cerrado")).not.toBeInTheDocument();
    expect(
      screen.getByRole("button", { name: "Cerrar Pedido" }),
    ).toBeDisabled();
    expect(
      screen.queryByRole("button", { name: "Reintentar misma operación" }),
    ).not.toBeInTheDocument();
  });

  it("un fallo de CSRF durante el retry conserva una intención todavía incierta", async () => {
    mutation.mockRejectedValue(new TypeError("offline"));
    const user = await open();
    await simple(user);
    await screen.findByText(/Resultado incierto/);
    discardAntiforgeryToken();
    fetchMock.mockRejectedValueOnce(new TypeError("CSRF offline"));
    await user.click(
      screen.getByRole("button", { name: "Reintentar misma operación" }),
    );
    expect(
      await screen.findByText(
        "No se pudo obtener la protección de la solicitud.",
      ),
    ).toBeVisible();
    expect(screen.getByRole("button", { name: "Liquidar" })).toBeDisabled();
    mutation.mockImplementationOnce(async () => {
      current = liquidated();
      return json({ occurredAt: "2026-09-05T17:59:00Z" });
    });
    await user.click(
      screen.getByRole("button", { name: "Reintentar misma operación" }),
    );
    await screen.findByText(/Pedido congelado/);
    expect(mutation.mock.calls[1]).toEqual(mutation.mock.calls[0]);
  });

  it("consulta un Pedido cerrado con su Historia visible y sin reapertura", async () => {
    current = {
      ...liquidated(),
      isClosed: true,
      isClosureEligible: false,
      closedAt: "2026-09-05T18:00:00Z",
      incorporations: [
        {
          id: "inc",
          ordinal: 1,
          confirmedAt: "2026-09-05T17:00:00Z",
          items: [],
        },
      ],
    };
    await open();
    expect(screen.getByText("Pedido cerrado")).toBeVisible();
    expect(
      screen.getByText("Fecha de Liquidación no disponible en esta consulta."),
    ).toBeVisible();
    expect(
      screen.getByRole("article", { name: "Incorporación 1" }),
    ).toBeVisible();
    expect(
      within(
        screen.getByRole("region", { name: "Liquidación y Cierre" }),
      ).queryByRole("button"),
    ).not.toBeInTheDocument();
    expect(
      screen.queryByRole("button", {
        name: /reabrir|reopen|Continuar este Pedido/i,
      }),
    ).not.toBeInTheDocument();
  });
});
