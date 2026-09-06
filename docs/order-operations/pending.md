# Fronteras abiertas de OrderOperations

No implementar estas fronteras a partir de conveniencias técnicas. La [base Q/R/F de S7-I2](confirmation.md#q-r-y-f-s7-i2) ya está implementada; no es deuda pendiente.

- Correction ordinaria sobre cantidad todavía Pending/elegible, conforme a reglas aún no definidas;
- excepciones sobre Work iniciado y Correction de progreso: una Correction no debe reinterpretar silenciosamente cantidades InPreparation o Ready, y modificar trabajo ya iniciado requiere tratamiento excepcional;
- reversal y exception handling de Delivery;
- interacción completa entre Delivery y Corrections de Content;
- errores post-Liquidation intencionalmente sin resolución en el flujo ordinario del MVP; Slice 6 no agrega correcciones económicas, reversals ni un subsistema adicional de Settlement/Payment;
- Cancellation de contenido y Cancellation completa donde siguen pendientes;
- Correction de AppliedPrice donde sigue pendiente;
- otras Corrections y excepciones de Order diferidas;
- query/API/UI de Historia de Delivery;
- cantidades fraccionarias de Preparation;
- cantidades fraccionarias de Delivery, mientras continúen abiertas;

- persistencia cross-reload de intents inciertos de Confirmation, PendingComposition, Liquidation y Closure;
- exposición de Liquidation `occurredAt` en el read de Order después de reload; el hecho ya está persistido en State, resultado del comando e History y la UI no lo fabrica;
- prioridad/SLA y owner/assignment de Preparation;
- query/API/UI de Historia de Preparation;

- comando de Content Correction e interacción con Preparation/Delivery: todavía no existe; la base Q/R/F no define esa transición;
- cantidad de Cancellation y Estado de precio efectivo: todavía no existen;
- futuros casos de obligación efectiva cero: no se habilita `PreparationWork.TotalQuantity = 0`, cuya invariante positiva sigue vigente;
- Correcciones de instruction;
- edición de una instruction ya confirmada;

El checkpoint de seguridad requerido para acciones humanas de Preparation y Delivery está cerrado, pero eso no significa que la seguridad global esté cerrada ni que todos los endpoints backend estén protegidos. `Quantity` en Content no implica identidad física individual. La instruction confirmada no es editable y SSE permanece pendiente.

### Correction y coordinación Preparation/Delivery

Delivery Correction ordinaria está implementada como retracción exacta de `DeliveredQuantity` efectivo, con Historia propia y preservación de `QuantityDelivered` histórico. Permanecen abiertas las excepciones, reversals y demás Corrections de Delivery no materializadas.

Una futura Correction de Preparation tampoco puede crear silenciosamente `DeliveredQuantity > ReadyQuantity` para un Prepared Content. Las Corrections que afecten cantidad ya Ready o Delivered deberán coordinarse con Delivery; esa política permanece abierta y no se resuelve aquí.
