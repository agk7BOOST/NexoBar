# Fronteras abiertas de OrderOperations

No implementar estas fronteras a partir de conveniencias técnicas. La base Q/R/F de S7-I2 y su extensión [Q/R/C/F](confirmation.md#q-r-c-y-f-s7-i2-content-correction-ordinaria-s7-i3d-content-cancellation-ordinaria-s7-i4d) ya están implementadas; no son deuda pendiente. OperationalIntervention INT-01..INT-07 también está implementada verticalmente en S7-I6D; su alcance y checkpoint E2E dirigido se registran en [Preparation](preparation.md#operationalintervention--decisiones-aprobadas-s7-int-d).

- demás intervenciones y excepciones de Order diferidas fuera de INT-01..07;
- reversal y exception handling de Delivery;
- interacción completa entre Delivery y Corrections de Content;
- errores post-Liquidation intencionalmente sin resolución en el flujo ordinario del MVP; Slice 6 no agrega correcciones económicas, reversals ni un subsistema adicional de Settlement/Payment;
- price correction (`AppliedPrice`) sigue pendiente;
- query/API/UI de Historia de Delivery;
- cantidades fraccionarias de Preparation;
- cantidades fraccionarias de Delivery, mientras continúen abiertas;

- persistencia cross-reload de intents inciertos de Confirmation, PendingComposition, Liquidation y Closure;
- exposición de Liquidation `occurredAt` en el read de Order después de reload; el hecho ya está persistido en State, resultado del comando e History y la UI no lo fabrica;
- prioridad/SLA y owner/assignment de Preparation;
- query/API/UI de Historia de Preparation;

- semántica física de desperdicio, descarte y recuperación pendiente; INT-07 no implica esos efectos ni modifica Inventory;
- efectos de Inventory y refunds/reversals de pagos permanecen pendientes y fuera del alcance de S7-I7D;
- Applied Price Correction sigue pendiente;
- reparación post-Liquidation sigue pendiente;
- Correcciones de instruction;
- edición de una instruction ya confirmada;

El checkpoint de seguridad requerido para acciones humanas de Preparation y Delivery está cerrado, pero eso no significa que la seguridad global esté cerrada ni que todos los endpoints backend estén protegidos. `Quantity` en Content no implica identidad física individual. La instruction confirmada no es editable y SSE permanece pendiente.

### Complete Order Cancellation — implementación y deuda acotada S7-I7D

Complete Order Cancellation CAN-01..05 está implementada verticalmente. Sus [cantidades](confirmation.md#complete-order-cancellation--cantidades-implementadas-s7-i7d), [Historia e idempotencia](contracts-and-history.md#complete-order-cancellation--historia-e-idempotencia-implementadas-s7-i7d) y [lifecycle](ending.md#complete-order-cancellation--implementación-vertical-s7-i7d) no quedan abiertos.

La terminalidad usa actualmente 11 guards manuales repartidos en 10 servicios de OrderOperations. No hay defecto de corrección demostrado. El riesgo es omitir un guard al introducir un nuevo comando mutante; todo trabajo futuro de mutación debe considerar explícitamente `OrderCancellationState`. No se debe introducir un framework genérico de estados terminales solo para eliminar esta duplicación.

Permanecen diferidos: disposición física, desperdicio o recuperación; efectos de Inventory; refunds o reversals de pagos; Applied Price Correction; reparación post-Liquidation; y SSE.

### Correction y coordinación Preparation/Delivery

Delivery Correction ordinaria está implementada como retracción exacta de `DeliveredQuantity` efectivo, con Historia propia y preservación de `QuantityDelivered` histórico. Permanecen abiertas las excepciones, reversals y demás Corrections de Delivery no materializadas.

Content Correction ordinaria ya está implementada verticalmente para cantidad Direct elegible y para `PendingQuantity` de un Content preparado. Content Cancellation ordinaria también está implementada verticalmente, exclusivamente para cantidad Direct no entregada o `PendingQuantity` preparado. Sus reglas vigentes viven en [Confirmation](confirmation.md#q-r-c-y-f-s7-i2-content-correction-ordinaria-s7-i3d-content-cancellation-ordinaria-s7-i4d). Preparation Progress Correction también está implementada verticalmente: solo corrige progreso registrado erróneamente mediante los comandos explícitos `InPreparation -> Pending` o `Ready -> InPreparation`; no altera `Q/R/C/F`, `TotalQuantity`, `DeliveredQuantity` ni Functional Amount, y nunca permite `DeliveredQuantity > ReadyQuantity`. La Cancellation mediante OperationalIntervention sobre trabajo real `InPreparation` o `Ready` está implementada verticalmente en S7-I6D conforme a INT-01..INT-07, preservando Delivered y su límite respecto de Ready. INT-02 limita la cantidad Ready intervenible a `ReadyQuantity - DeliveredQuantity` y nunca retrae Delivery. Ya no están abiertas las decisiones de usar C sin otra deducción de F, conservar procedencia en Historia sin nuevos contadores State ni exigir solo `OperationalIntervention`, sin `Preparation` o `PreparationEnablement` adicionales. La lectura estrecha del target no concede Start, Ready, Preparation Correction ni autoridad general sobre colas de destinos. Existen dos intenciones explícitas de intervención, lectura estrecha y persistencia implementadas. El frontend y el checkpoint E2E vertical dirigido también están registrados en Preparation; esto no cierra las deudas diferidas enumeradas arriba.
