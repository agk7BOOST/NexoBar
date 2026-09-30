import type { ReactNode } from "react";
import type { OrderDeliveryContent } from "./deliveryClient.ts";

interface DeliveryContentCardProps {
  item: OrderDeliveryContent;
  description: string;
  isFullyDelivered: boolean;
  busy: boolean;
  children: ReactNode;
}

export function DeliveryContentCard({
  item,
  description,
  isFullyDelivered,
  busy,
  children,
}: DeliveryContentCardProps) {
  return (
    <article
      className="delivery-content"
      aria-label={description}
      aria-busy={busy}
    >
      <div className="delivery-content-heading">
        <div>
          <h3>
            {item.productOperationalName ?? "Nombre histórico no disponible"}
          </h3>
          <p>Incorporación {item.incorporationOrdinal}</p>
        </div>
        {isFullyDelivered && (
          <p className="fully-delivered" role="status">
            Entregado
          </p>
        )}
      </div>
      <p className="confirmed-instruction">
        {item.instruction ?? "Sin instrucción"}
      </p>
      <dl className="delivery-quantities">
        <div>
          <dt>Total</dt>
          <dd>{item.totalQuantity}</dd>
        </div>
        {item.requiresPreparationAtConfirmation ? (
          <div>
            <dt>Listo</dt>
            <dd>{item.readyQuantity}</dd>
          </div>
        ) : (
          <div className="delivery-preparation-note">
            <dt>Preparación</dt>
            <dd>Preparación no requerida</dd>
          </div>
        )}
        <div>
          <dt>Entregado</dt>
          <dd>{item.deliveredQuantity}</dd>
        </div>
        <div>
          <dt>Disponible para entregar</dt>
          <dd>{item.deliverableQuantity}</dd>
        </div>
        <div>
          <dt>Pendiente de entrega</dt>
          <dd>{item.remainingQuantity}</dd>
        </div>
      </dl>

      {children}
    </article>
  );
}
