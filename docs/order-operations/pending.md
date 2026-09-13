# Fronteras abiertas de OrderOperations

## Slice 7 — Corrections, Cancellations and Exceptions: cerrado

Slice 7 está cerrado. El checkpoint vertical consolidado incluye Delivery Correction; `ContentQuantityState` con Q/R/C/F; Content Quantity Correction; Content Cancellation ordinaria; Preparation Progress Correction (Correct Start y Correct Ready); OperationalIntervention para obligación real InPreparation y Ready no entregada; Complete Order Cancellation como terminación excepcional con `OrderCancellationState`, descarte atómico de PendingComposition y autorización condicional `OperationalIntervention`; y Applied Price Correction con `AppliedPrice` original inmutable, `EffectiveAppliedPrice` por Content, adopción explícita del precio vigente de Catalog e Importe funcional basado en el precio efectivo.

La auditoría final del baseline vigente no encontró contradicción normativa ni capacidad de Slice 7 genuinamente ausente. Estado e Historia semántica permanecen separados; idempotencia durable y replay se preservan; Liquidation/Freeze y la terminalidad de Complete Cancellation son coherentes; el frontend conserva el significado de las intenciones backend; y existen checkpoints E2E focalizados para las fronteras verticales principales. Este checkpoint no reproduce las reglas propietarias de cada capacidad.

La deuda de terminalidad se mantiene como mantenibilidad: 12 guards manuales de Complete Cancellation en 11 servicios de OrderOperations. No hay guard faltante ni defecto de corrección demostrado. Toda mutación futura de OrderOperations debe considerar explícitamente `OrderCancellationState`; no se introduce un framework de estado terminal para eliminar esta duplicación.

Slice 8 continúa abierto. El transporte y el vertical `preparation.destination.changed` están implementados. El vertical aprobado `order.changed` queda limitado al Order explícitamente activo para operación y permanece pendiente de implementación; no es un feed general de Orders. SSE de Catalog, Inventory y OperationalConfiguration continúan diferidos. Sus decisiones y la limitación de instancia activa están en [SSE y frescura multiusuario](../architecture/sse-and-freshness.md); este alcance no incluye reparación post-Liquidation.

No implementar estas fronteras a partir de conveniencias técnicas. La base Q/R/F de S7-I2 y su extensión [Q/R/C/F](confirmation.md#q-r-c-y-f-s7-i2-content-correction-ordinaria-s7-i3d-content-cancellation-ordinaria-s7-i4d) ya están implementadas; no son deuda pendiente. OperationalIntervention INT-01..INT-07 también está implementada verticalmente en S7-I6D; su alcance y checkpoint E2E dirigido se registran en [Preparation](preparation.md#operationalintervention--decisiones-aprobadas-s7-int-d).

- demás intervenciones y excepciones de Order diferidas fuera de INT-01..07;
- reversal y exception handling de Delivery;
- interacción completa entre Delivery y Corrections de Content;
- errores post-Liquidation intencionalmente sin resolución en el flujo ordinario del MVP; Slice 6 no agrega correcciones económicas, reversals ni un subsistema adicional de Settlement/Payment;
- query/API/UI de Historia de Delivery;
- cantidades fraccionarias de Preparation;
- cantidades fraccionarias de Delivery, mientras continúen abiertas;

- persistencia cross-reload de intents inciertos de Confirmation, PendingComposition, Liquidation y Closure;
- exposición de Liquidation `occurredAt` en el read de Order después de reload; el hecho ya está persistido en State, resultado del comando e History y la UI no lo fabrica;
- prioridad/SLA y owner/assignment de Preparation;
- query/API/UI de Historia de Preparation;

- semántica física de desperdicio, descarte y recuperación pendiente; INT-07 no implica esos efectos ni modifica Inventory;
- efectos de Inventory y refunds/reversals de pagos permanecen pendientes y fuera del alcance de S7-I7D;
- reparación post-Liquidation sigue pendiente;
- Correcciones de instruction;
- edición de una instruction ya confirmada;

El checkpoint de seguridad requerido para acciones humanas de Preparation y Delivery está cerrado, pero eso no significa que la seguridad global esté cerrada ni que todos los endpoints backend estén protegidos. `Quantity` en Content no implica identidad física individual. La instruction confirmada no es editable. El vertical SSE de Preparation está implementado; el siguiente vertical aprobado es `order.changed` para el Order activo bajo su frontera de lectura operacional. Los demás scopes de Slice 8 permanecen diferidos según [SSE y frescura multiusuario](../architecture/sse-and-freshness.md).

### Complete Order Cancellation — implementación y deuda acotada S7-I7D

Complete Order Cancellation CAN-01..05 está implementada verticalmente. Sus [cantidades](confirmation.md#complete-order-cancellation--cantidades-implementadas-s7-i7d), [Historia e idempotencia](contracts-and-history.md#complete-order-cancellation--historia-e-idempotencia-implementadas-s7-i7d) y [lifecycle](ending.md#complete-order-cancellation--implementación-vertical-s7-i7d) no quedan abiertos.

La terminalidad usa actualmente 12 guards manuales repartidos en 11 servicios de OrderOperations; Applied Price Correction agregó uno. No hay defecto de corrección demostrado. El riesgo es omitir un guard al introducir un nuevo comando mutante; todo trabajo futuro de mutación debe considerar explícitamente `OrderCancellationState`. No se debe introducir un framework genérico de estados terminales solo para eliminar esta duplicación.

Permanecen diferidos: disposición física, desperdicio o recuperación; efectos de Inventory; refunds o reversals de pagos; reparación post-Liquidation; y SSE.

### Applied Price Correction — implementación vertical S7-I8D

PRICE-CORR-01..05 están implementadas verticalmente: separan `AppliedPrice` original confirmado de `EffectiveAppliedPrice` actual por Content, adoptan el precio actual válido de Catalog sólo mediante una intención explícita y preservan cantidades, Delivery y PendingComposition. Incluyen sucesión, no-op, autorización, Historia/idempotencia y fronteras de Liquidation/Freeze, Closure y Complete Order Cancellation. Véanse [Confirmation](confirmation.md#applied-price-correction--implementación-vertical-s7-i8d), [Historia](contracts-and-history.md#applied-price-correction--historia-e-idempotencia-implementadas-s7-i8d), [autorización](../identities-and-capabilities/security.md#applied-price-correction--autoridad-implementada-s7-i8d) y [lifecycle](ending.md#functional-amount-liquidation-freeze-y-closure--slice-6).

Siguen fuera de alcance: edición libre de precios en Orders; descuentos, promociones o precios de cortesía; impuestos; recargos; refunds o reversals de pagos; reparación post-Liquidation; efectos físicos o de Inventory; y SSE.

### Correction y coordinación Preparation/Delivery

Delivery Correction ordinaria está implementada como retracción exacta de `DeliveredQuantity` efectivo, con Historia propia y preservación de `QuantityDelivered` histórico. Permanecen abiertas las excepciones, reversals y demás Corrections de Delivery no materializadas.

Content Correction ordinaria ya está implementada verticalmente para cantidad Direct elegible y para `PendingQuantity` de un Content preparado. Content Cancellation ordinaria también está implementada verticalmente, exclusivamente para cantidad Direct no entregada o `PendingQuantity` preparado. Sus reglas vigentes viven en [Confirmation](confirmation.md#q-r-c-y-f-s7-i2-content-correction-ordinaria-s7-i3d-content-cancellation-ordinaria-s7-i4d). Preparation Progress Correction también está implementada verticalmente: solo corrige progreso registrado erróneamente mediante los comandos explícitos `InPreparation -> Pending` o `Ready -> InPreparation`; no altera `Q/R/C/F`, `TotalQuantity`, `DeliveredQuantity` ni Functional Amount, y nunca permite `DeliveredQuantity > ReadyQuantity`. La Cancellation mediante OperationalIntervention sobre trabajo real `InPreparation` o `Ready` está implementada verticalmente en S7-I6D conforme a INT-01..INT-07, preservando Delivered y su límite respecto de Ready. INT-02 limita la cantidad Ready intervenible a `ReadyQuantity - DeliveredQuantity` y nunca retrae Delivery. Ya no están abiertas las decisiones de usar C sin otra deducción de F, conservar procedencia en Historia sin nuevos contadores State ni exigir solo `OperationalIntervention`, sin `Preparation` o `PreparationEnablement` adicionales. La lectura estrecha del target no concede Start, Ready, Preparation Correction ni autoridad general sobre colas de destinos. Existen dos intenciones explícitas de intervención, lectura estrecha y persistencia implementadas. El frontend y el checkpoint E2E vertical dirigido también están registrados en Preparation; esto no cierra las deudas diferidas enumeradas arriba.
