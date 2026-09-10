# Fronteras abiertas de OrderOperations

No implementar estas fronteras a partir de conveniencias técnicas. La base Q/R/F de S7-I2 y su extensión [Q/R/C/F](confirmation.md#q-r-c-y-f-s7-i2-content-correction-ordinaria-s7-i3d-content-cancellation-ordinaria-s7-i4d) ya están implementadas; no son deuda pendiente.

- intervención y demás excepciones de Order diferidas;
- reversal y exception handling de Delivery;
- interacción completa entre Delivery y Corrections de Content;
- errores post-Liquidation intencionalmente sin resolución en el flujo ordinario del MVP; Slice 6 no agrega correcciones económicas, reversals ni un subsistema adicional de Settlement/Payment;
- Cancellation completa de Order sigue pendiente;
- price correction (`AppliedPrice`) sigue pendiente;
- query/API/UI de Historia de Delivery;
- cantidades fraccionarias de Preparation;
- cantidades fraccionarias de Delivery, mientras continúen abiertas;

- persistencia cross-reload de intents inciertos de Confirmation, PendingComposition, Liquidation y Closure;
- exposición de Liquidation `occurredAt` en el read de Order después de reload; el hecho ya está persistido en State, resultado del comando e History y la UI no lo fabrica;
- prioridad/SLA y owner/assignment de Preparation;
- query/API/UI de Historia de Preparation;

- Cancellation/intervención que afecte trabajo `InPreparation` o `Ready` sigue pendiente; la Cancellation ordinaria de Pending ya está implementada;
- OperationalIntervention sigue pendiente;
- Applied Price Correction sigue pendiente;
- reparación post-Liquidation sigue pendiente;
- `PreparationWork.TotalQuantity` puede llegar a cero cuando Content Correction o Content Cancellation reducen toda la obligación vigente mientras preservan la fila Work; no habilita OperationalIntervention ni Cancellation de trabajo real `InPreparation` o `Ready`;
- Correcciones de instruction;
- edición de una instruction ya confirmada;

El checkpoint de seguridad requerido para acciones humanas de Preparation y Delivery está cerrado, pero eso no significa que la seguridad global esté cerrada ni que todos los endpoints backend estén protegidos. `Quantity` en Content no implica identidad física individual. La instruction confirmada no es editable y SSE permanece pendiente.

### Correction y coordinación Preparation/Delivery

Delivery Correction ordinaria está implementada como retracción exacta de `DeliveredQuantity` efectivo, con Historia propia y preservación de `QuantityDelivered` histórico. Permanecen abiertas las excepciones, reversals y demás Corrections de Delivery no materializadas.

Content Correction ordinaria ya está implementada verticalmente para cantidad Direct elegible y para `PendingQuantity` de un Content preparado. Content Cancellation ordinaria también está implementada verticalmente, exclusivamente para cantidad Direct no entregada o `PendingQuantity` preparado. Sus reglas vigentes viven en [Confirmation](confirmation.md#q-r-c-y-f-s7-i2-content-correction-ordinaria-s7-i3d-content-cancellation-ordinaria-s7-i4d). Preparation Correction aprobada solo corrige progreso registrado erróneamente mediante `InPreparation -> Pending` o `Ready -> InPreparation`; no altera `Q/R/C/F`, `TotalQuantity` ni Delivery, y nunca permite `DeliveredQuantity > ReadyQuantity`. La Cancellation/intervención sobre trabajo real ya `InPreparation` o `Ready` deberá coordinarse con Delivery; esa política permanece abierta y no se resuelve aquí.
