import {
  fireEvent,
  render,
  screen,
  waitFor,
  within,
} from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";
import {
  discardAntiforgeryToken,
  getAntiforgeryToken,
} from "../identity/sessionClient.ts";
import { DeliveryPanel } from "./DeliveryPanel.tsx";
import {
  deliverQuantity,
  DeliveryProblemError,
  getOrderDelivery,
  type DeliverQuantityResult,
  type OrderDelivery,
  type OrderDeliveryContent,
} from "./deliveryClient.ts";

vi.mock("../identity/sessionClient.ts", async (importOriginal) => {
  const original =
    await importOriginal<typeof import("../identity/sessionClient.ts")>();
  return {
    ...original,
    discardAntiforgeryToken: vi.fn(),
    getAntiforgeryToken: vi.fn(),
  };
});

vi.mock("./deliveryClient.ts", async (importOriginal) => {
  const original = await importOriginal<typeof import("./deliveryClient.ts")>();
  return {
    ...original,
    deliverQuantity: vi.fn(),
    getOrderDelivery: vi.fn(),
  };
});

const direct: OrderDeliveryContent = {
  incorporationId: "incorporation-1",
  incorporationOrdinal: 1,
  contentOrdinal: 1,
  productId: "product-direct-id",
  productOperationalName: "Agua",
  instruction: null,
  totalQuantity: 5,
  requiresPreparationAtConfirmation: false,
  readyQuantity: null,
  deliveredQuantity: 0,
  deliverableQuantity: 5,
  remainingQuantity: 5,
};

const prepared: OrderDeliveryContent = {
  incorporationId: "incorporation-1",
  incorporationOrdinal: 1,
  contentOrdinal: 2,
  productId: "product-prepared-id",
  productOperationalName: "Hamburguesa",
  instruction: "Sin cebolla",
  totalQuantity: 5,
  requiresPreparationAtConfirmation: true,
  readyQuantity: 0,
  deliveredQuantity: 0,
  deliverableQuantity: 0,
  remainingQuantity: 5,
};

const firstKey =
  "11111111-1111-4111-8111-111111111111" as `${string}-${string}-${string}-${string}-${string}`;
const secondKey =
  "22222222-2222-4222-8222-222222222222" as `${string}-${string}-${string}-${string}-${string}`;

function orderDelivery(contents: OrderDeliveryContent[]): OrderDelivery {
  return {
    orderId: "order-id",
    operationalReference: "order-reference",
    currentContext: "Mesa 7",
    contents,
  };
}

function commandResult(item: OrderDeliveryContent): DeliverQuantityResult {
  return {
    incorporationId: item.incorporationId,
    contentOrdinal: item.contentOrdinal,
    historyId: "history-1",
    occurredAt: "2026-08-31T12:00:00Z",
    deliveredQuantity: item.deliveredQuantity + 1,
  };
}

function deferred<T>() {
  let resolve!: (value: T) => void;
  let reject!: (reason?: unknown) => void;
  const promise = new Promise<T>((resolvePromise, rejectPromise) => {
    resolve = resolvePromise;
    reject = rejectPromise;
  });
  return { promise, resolve, reject };
}

function description(item: OrderDeliveryContent): string {
  return `${item.productOperationalName}, ${item.instruction ?? "sin instrucción"}, incorporación ${item.incorporationOrdinal}`;
}

function inputFor(item: OrderDeliveryContent) {
  return screen.getByLabelText(
    `Cantidad a entregar — ${item.productOperationalName} — ${item.instruction ?? "sin instrucción"} — incorporación ${item.incorporationOrdinal}`,
  );
}

it("bloquea entregas ordinarias aunque Delivery conserve una lectura anterior a Freeze", async () => {
  vi.mocked(getOrderDelivery).mockResolvedValue(orderDelivery([direct]));
  render(
    <DeliveryPanel
      operationalReference="order-reference"
      onUnauthorized={vi.fn()}
      ordinaryMutationsBlocked
    />,
  );
  await screen.findByRole("article", { name: description(direct) });
  expect(buttonFor(direct)).toBeDisabled();
  expect(inputFor(direct)).toBeDisabled();
  expect(articleFor(direct)).toBeVisible();
});

function buttonFor(item: OrderDeliveryContent) {
  return screen.getByRole("button", { name: `Entregar ${description(item)}` });
}

function articleFor(item: OrderDeliveryContent) {
  return screen.getByRole("article", { name: description(item) });
}

async function renderPanel(
  contents: OrderDeliveryContent[],
  onUnauthorized = vi.fn(),
) {
  vi.mocked(getOrderDelivery).mockResolvedValueOnce(orderDelivery(contents));
  render(
    <DeliveryPanel
      operationalReference="order-reference"
      onUnauthorized={onUnauthorized}
    />,
  );
  if (contents.length > 0)
    await screen.findAllByText(contents[0].productOperationalName);
  return onUnauthorized;
}

describe("DeliveryPanel", () => {
  beforeEach(() => {
    vi.mocked(discardAntiforgeryToken).mockReset();
    vi.mocked(getAntiforgeryToken).mockReset().mockResolvedValue("csrf-1");
    vi.mocked(getOrderDelivery).mockReset();
    vi.mocked(deliverQuantity).mockReset();
  });

  it("reuses the opened Order reference and supports manual refresh", async () => {
    vi.mocked(getOrderDelivery).mockResolvedValue(orderDelivery([direct]));
    const user = userEvent.setup();
    render(
      <DeliveryPanel
        operationalReference="opaque/reference"
        onUnauthorized={vi.fn()}
      />,
    );

    await screen.findByText("Agua");
    expect(getOrderDelivery).toHaveBeenCalledWith("opaque/reference");

    await user.click(screen.getByRole("button", { name: "Actualizar" }));
    await waitFor(() => expect(getOrderDelivery).toHaveBeenCalledTimes(2));
  });

  it("presents direct quantities without fake Ready and sends a partial quantity", async () => {
    vi.spyOn(crypto, "randomUUID").mockReturnValue(firstKey);
    vi.mocked(deliverQuantity).mockReturnValue(new Promise(() => undefined));
    const user = userEvent.setup();
    await renderPanel([direct]);

    const article = articleFor(direct);
    expect(article).toHaveTextContent("Total5");
    expect(article).toHaveTextContent("Delivered0");
    expect(article).toHaveTextContent("Deliverable5");
    expect(article).toHaveTextContent("Remaining5");
    expect(article).toHaveTextContent("Preparación no requerida");
    expect(within(article).queryByText("Ready")).not.toBeInTheDocument();
    expect(article).not.toHaveTextContent("product-direct-id");
    expect(inputFor(direct)).toHaveValue(5);

    await user.clear(inputFor(direct));
    await user.type(inputFor(direct), "2");
    await user.click(buttonFor(direct));

    expect(deliverQuantity).toHaveBeenCalledWith({
      incorporationId: direct.incorporationId,
      contentOrdinal: direct.contentOrdinal,
      quantity: 2,
      idempotencyKey: firstKey,
      antiforgeryToken: "csrf-1",
    });
  });

  it("shows a fully delivered direct Content without an action", async () => {
    const complete = {
      ...direct,
      deliveredQuantity: 5,
      deliverableQuantity: 0,
      remainingQuantity: 0,
    };
    await renderPanel([complete]);

    const article = articleFor(complete);
    expect(article).toHaveTextContent("Entregado");
    expect(
      within(article).queryByRole("button", { name: /Entregar/ }),
    ).not.toBeInTheDocument();
    expect(within(article).queryByRole("spinbutton")).not.toBeInTheDocument();
    expect(article).not.toHaveTextContent(/cerrado|pagado|liquidado/i);
  });

  it("keeps prepared Contents visible and uses only authoritative Ready and Deliverable", async () => {
    const partialReady = {
      ...prepared,
      contentOrdinal: 3,
      instruction: "Poco hecha",
      readyQuantity: 3,
      deliverableQuantity: 3,
    };
    const partialDelivered = {
      ...prepared,
      contentOrdinal: 4,
      instruction: "Bien cocida",
      readyQuantity: 3,
      deliveredQuantity: 2,
      deliverableQuantity: 1,
      remainingQuantity: 3,
    };
    await renderPanel([prepared, partialReady, partialDelivered]);

    const unavailable = articleFor(prepared);
    expect(unavailable).toHaveTextContent("Ready0");
    expect(unavailable).toHaveTextContent("Deliverable0");
    expect(
      within(unavailable).queryByRole("spinbutton"),
    ).not.toBeInTheDocument();
    expect(inputFor(partialReady)).toHaveValue(3);
    expect(inputFor(partialDelivered)).toHaveValue(1);
    expect(screen.queryByText("Pendiente")).not.toBeInTheDocument();
    expect(screen.queryByText("En preparación")).not.toBeInTheDocument();
  });

  it("keeps multiple Contents for the same Product separate", async () => {
    const otherLine = {
      ...direct,
      contentOrdinal: 5,
      instruction: "Con hielo",
      deliverableQuantity: 2,
      remainingQuantity: 2,
      totalQuantity: 2,
    };
    await renderPanel([direct, otherLine]);

    expect(screen.getAllByRole("article", { name: /Agua/ })).toHaveLength(2);
    expect(inputFor(direct)).toHaveValue(5);
    expect(inputFor(otherLine)).toHaveValue(2);
  });

  it("disables one Content while submitting, prevents double submit, and renders only refreshed GET quantities", async () => {
    const post = deferred<DeliverQuantityResult>();
    const refresh = deferred<OrderDelivery>();
    const updated = {
      ...direct,
      deliveredQuantity: 2,
      deliverableQuantity: 3,
      remainingQuantity: 3,
    };
    vi.spyOn(crypto, "randomUUID").mockReturnValue(firstKey);
    vi.mocked(deliverQuantity).mockReturnValueOnce(post.promise);
    vi.mocked(getOrderDelivery)
      .mockResolvedValueOnce(orderDelivery([direct]))
      .mockReturnValueOnce(refresh.promise);
    render(
      <DeliveryPanel
        operationalReference="order-reference"
        onUnauthorized={vi.fn()}
      />,
    );
    await screen.findByText("Agua");

    fireEvent.change(inputFor(direct), { target: { value: "2" } });
    fireEvent.click(buttonFor(direct));
    fireEvent.click(buttonFor(direct));

    await waitFor(() => expect(deliverQuantity).toHaveBeenCalledTimes(1));
    expect(crypto.randomUUID).toHaveBeenCalledTimes(1);
    expect(inputFor(direct)).toBeDisabled();
    expect(buttonFor(direct)).toBeDisabled();
    expect(articleFor(direct)).toHaveAttribute("aria-busy", "true");

    post.resolve({ ...commandResult(direct), deliveredQuantity: 5 });
    await waitFor(() => expect(getOrderDelivery).toHaveBeenCalledTimes(2));
    expect(articleFor(direct)).toHaveTextContent("Delivered0");
    expect(articleFor(direct)).not.toHaveTextContent("Delivered5");

    refresh.resolve(orderDelivery([updated]));
    await waitFor(() =>
      expect(articleFor(updated)).toHaveTextContent("Delivered2"),
    );
    expect(articleFor(updated)).toHaveTextContent("Deliverable3");
    expect(inputFor(updated)).toHaveValue(3);
  });

  it("retains exact network-uncertain intent, retries it, then creates a new key after resolution", async () => {
    const updated = {
      ...direct,
      deliveredQuantity: 2,
      deliverableQuantity: 3,
      remainingQuantity: 3,
    };
    vi.spyOn(crypto, "randomUUID")
      .mockReturnValueOnce(firstKey)
      .mockReturnValueOnce(secondKey);
    vi.mocked(deliverQuantity)
      .mockRejectedValueOnce(new Error("connection lost"))
      .mockResolvedValueOnce(commandResult(direct))
      .mockReturnValueOnce(new Promise(() => undefined));
    vi.mocked(getOrderDelivery)
      .mockResolvedValueOnce(orderDelivery([direct]))
      .mockResolvedValueOnce(orderDelivery([updated]));
    const user = userEvent.setup();
    render(
      <DeliveryPanel
        operationalReference="order-reference"
        onUnauthorized={vi.fn()}
      />,
    );
    await screen.findByText("Agua");

    await user.clear(inputFor(direct));
    await user.type(inputFor(direct), "2");
    await user.click(buttonFor(direct));

    expect(
      await screen.findByText(
        "No se pudo confirmar el resultado de la entrega.",
      ),
    ).toBeInTheDocument();
    expect(inputFor(direct)).toBeDisabled();
    expect(buttonFor(direct)).toBeDisabled();
    const originalCommand = vi.mocked(deliverQuantity).mock.calls[0][0];

    await user.click(screen.getByRole("button", { name: "Reintentar" }));
    await waitFor(() => expect(deliverQuantity).toHaveBeenCalledTimes(2));
    expect(vi.mocked(deliverQuantity).mock.calls[1][0]).toEqual(
      originalCommand,
    );
    await waitFor(() => expect(inputFor(updated)).toHaveValue(3));

    await user.click(buttonFor(updated));
    expect(deliverQuantity).toHaveBeenLastCalledWith(
      expect.objectContaining({
        quantity: 3,
        idempotencyKey: secondKey,
      }),
    );
    expect(crypto.randomUUID).toHaveBeenCalledTimes(2);
  });

  it("treats a valid 5xx as uncertain and keeps the exact retry", async () => {
    vi.spyOn(crypto, "randomUUID").mockReturnValue(firstKey);
    vi.mocked(deliverQuantity)
      .mockRejectedValueOnce(
        new DeliveryProblemError(500, {
          code: "order_operations.delivery.state_inconsistent",
        }),
      )
      .mockReturnValueOnce(new Promise(() => undefined));
    const user = userEvent.setup();
    await renderPanel([direct]);

    await user.click(buttonFor(direct));

    expect(
      await screen.findByText(
        "No se pudo confirmar el resultado de la entrega.",
      ),
    ).toBeInTheDocument();
    expect(
      screen.getByText(
        "No se pudo procesar la entrega por un problema técnico.",
      ),
    ).toBeInTheDocument();
    const originalCommand = vi.mocked(deliverQuantity).mock.calls[0][0];
    await user.click(screen.getByRole("button", { name: "Reintentar" }));
    expect(vi.mocked(deliverQuantity).mock.calls[1][0]).toEqual(
      originalCommand,
    );
  });

  it("keeps another Content operable while one outcome is uncertain", async () => {
    const other = {
      ...direct,
      incorporationId: "incorporation-2",
      contentOrdinal: 1,
      productOperationalName: "Gaseosa",
    };
    vi.spyOn(crypto, "randomUUID")
      .mockReturnValueOnce(firstKey)
      .mockReturnValueOnce(secondKey);
    vi.mocked(deliverQuantity)
      .mockRejectedValueOnce(new Error("network"))
      .mockReturnValueOnce(new Promise(() => undefined));
    const user = userEvent.setup();
    await renderPanel([direct, other]);

    await user.click(buttonFor(direct));
    await screen.findByText("No se pudo confirmar el resultado de la entrega.");
    expect(buttonFor(direct)).toBeDisabled();
    expect(buttonFor(other)).toBeEnabled();

    await user.click(buttonFor(other));
    expect(deliverQuantity).toHaveBeenCalledTimes(2);
    expect(vi.mocked(deliverQuantity).mock.calls[1][0]).toMatchObject({
      incorporationId: other.incorporationId,
      contentOrdinal: other.contentOrdinal,
      idempotencyKey: secondKey,
    });
  });

  it("clears an uncertain intent after retry 409 and refreshes authority", async () => {
    vi.spyOn(crypto, "randomUUID").mockReturnValue(firstKey);
    vi.mocked(deliverQuantity)
      .mockRejectedValueOnce(new Error("network"))
      .mockRejectedValueOnce(
        new DeliveryProblemError(409, {
          code: "order_operations.delivery.deliverable_quantity_insufficient",
        }),
      );
    vi.mocked(getOrderDelivery)
      .mockResolvedValueOnce(orderDelivery([direct]))
      .mockResolvedValueOnce(orderDelivery([direct]));
    const user = userEvent.setup();
    render(
      <DeliveryPanel
        operationalReference="order-reference"
        onUnauthorized={vi.fn()}
      />,
    );
    await screen.findByText("Agua");
    await user.click(buttonFor(direct));
    await screen.findByRole("button", { name: "Reintentar" });

    await user.click(screen.getByRole("button", { name: "Reintentar" }));

    expect(
      await screen.findByText(
        "El estado de la entrega cambió. Se actualizó la información.",
      ),
    ).toBeInTheDocument();
    await waitFor(() => expect(getOrderDelivery).toHaveBeenCalledTimes(2));
    expect(
      screen.queryByRole("button", { name: "Reintentar" }),
    ).not.toBeInTheDocument();
    expect(buttonFor(direct)).toBeEnabled();
  });

  it("distinguishes an explicit idempotency conflict", async () => {
    vi.mocked(deliverQuantity).mockRejectedValueOnce(
      new DeliveryProblemError(409, {
        code: "order_operations.delivery.idempotency_key_conflict",
      }),
    );
    vi.mocked(getOrderDelivery)
      .mockResolvedValueOnce(orderDelivery([direct]))
      .mockResolvedValueOnce(orderDelivery([direct]));
    const user = userEvent.setup();
    render(
      <DeliveryPanel
        operationalReference="order-reference"
        onUnauthorized={vi.fn()}
      />,
    );
    await screen.findByText("Agua");

    await user.click(buttonFor(direct));

    expect(
      await screen.findByText(
        "La operación no coincide con el intento original.",
      ),
    ).toBeInTheDocument();
  });

  it.each(["0", "-1", "1.5", "6"])(
    "rejects invalid quantity %s without a request",
    async (value) => {
      await renderPanel([direct]);
      fireEvent.change(inputFor(direct), { target: { value } });
      fireEvent.click(buttonFor(direct));

      expect(
        await screen.findByText("Ingresá una cantidad entera entre 1 y 5."),
      ).toBeInTheDocument();
      expect(deliverQuantity).not.toHaveBeenCalled();
    },
  );

  it("handles an unexpected 400 as a known operation error", async () => {
    vi.mocked(deliverQuantity).mockRejectedValueOnce(
      new DeliveryProblemError(400, {
        code: "order_operations.delivery.quantity_invalid",
      }),
    );
    const user = userEvent.setup();
    await renderPanel([direct]);

    await user.click(buttonFor(direct));

    expect(
      await screen.findByText(
        "No se pudo realizar la entrega. Revisá la cantidad e intentá nuevamente.",
      ),
    ).toBeInTheDocument();
    expect(buttonFor(direct)).toBeEnabled();
  });

  it("returns to login and clears in-memory state on GET 401", async () => {
    vi.mocked(getOrderDelivery).mockRejectedValueOnce(
      new DeliveryProblemError(401),
    );
    const onUnauthorized = vi.fn();
    render(
      <DeliveryPanel
        operationalReference="order-reference"
        onUnauthorized={onUnauthorized}
      />,
    );

    await waitFor(() => expect(onUnauthorized).toHaveBeenCalledTimes(1));
    expect(discardAntiforgeryToken).toHaveBeenCalled();
  });

  it("returns to login on POST 401 without retry", async () => {
    vi.mocked(deliverQuantity).mockRejectedValueOnce(
      new DeliveryProblemError(401),
    );
    const onUnauthorized = vi.fn();
    const user = userEvent.setup();
    await renderPanel([direct], onUnauthorized);

    await user.click(buttonFor(direct));

    await waitFor(() => expect(onUnauthorized).toHaveBeenCalledTimes(1));
    expect(
      screen.queryByRole("button", { name: "Reintentar" }),
    ).not.toBeInTheDocument();
  });

  it("keeps Identity authenticated on GET and POST 403", async () => {
    const onUnauthorized = vi.fn();
    vi.mocked(getOrderDelivery).mockRejectedValueOnce(
      new DeliveryProblemError(403),
    );
    const { unmount } = render(
      <DeliveryPanel
        operationalReference="order-reference"
        onUnauthorized={onUnauthorized}
      />,
    );
    expect(await screen.findByText(forbiddenMessage)).toBeInTheDocument();
    expect(onUnauthorized).not.toHaveBeenCalled();
    unmount();

    vi.mocked(deliverQuantity).mockRejectedValueOnce(
      new DeliveryProblemError(403),
    );
    const user = userEvent.setup();
    await renderPanel([direct], onUnauthorized);
    await user.click(buttonFor(direct));
    expect(await screen.findByText(forbiddenMessage)).toBeInTheDocument();
    expect(onUnauthorized).not.toHaveBeenCalled();
  });

  it("shows GET not found and refreshes after a POST target 404", async () => {
    vi.mocked(getOrderDelivery).mockRejectedValueOnce(
      new DeliveryProblemError(404),
    );
    const { unmount } = render(
      <DeliveryPanel
        operationalReference="missing-order"
        onUnauthorized={vi.fn()}
      />,
    );
    expect(
      await screen.findByText("Pedido no encontrado."),
    ).toBeInTheDocument();
    unmount();
    vi.mocked(getOrderDelivery).mockClear();

    vi.mocked(deliverQuantity).mockRejectedValueOnce(
      new DeliveryProblemError(404),
    );
    vi.mocked(getOrderDelivery)
      .mockResolvedValueOnce(orderDelivery([direct]))
      .mockResolvedValueOnce(orderDelivery([]));
    const user = userEvent.setup();
    render(
      <DeliveryPanel
        operationalReference="order-reference"
        onUnauthorized={vi.fn()}
      />,
    );
    await screen.findByText("Agua");
    await user.click(buttonFor(direct));

    await waitFor(() => expect(getOrderDelivery).toHaveBeenCalledTimes(2));
    expect(
      await screen.findByText("El Pedido no contiene Contents para entregar."),
    ).toBeInTheDocument();
  });
});

const forbiddenMessage =
  "Esta Identity no tiene autorización para realizar entregas.";
