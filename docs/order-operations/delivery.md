# Delivery y Delivery Correction

## Delivery mínima operativa

Delivery pertenece a `OrderOperations`; no es un módulo top-level. Delivery y Preparation son dimensiones distintas. Entregar no decrementa ni modifica `PendingQuantity`, `InPreparationQuantity` o `ReadyQuantity`, y `Ready != Delivered`. Delivery tampoco implica Liquidation, Payment ni Closure.

### DeliveryState y clasificación

`DeliveryState` mantiene una relación técnica 1:1 con `IncorporationContent`; ambos comparten la identidad `(IncorporationId, ContentOrdinal)`. El único Estado persistido propio es `DeliveredQuantity : int`, inicialmente `0`. No existen `DeliveryId` UUID, `DeliveryWork`, `Delivery Status`, `IsDelivered` persistido, `Remaining` persistido, `Deliverable` persistido ni una copia de `Ready`.

La clasificación histórica usa `IncorporationContent.RequiresPreparationAtConfirmation` y valida su coherencia con `PreparationWork`:

- prepared: flag `true` y Work presente;
- direct: flag `false` y Work ausente;
- cualquiera de las dos combinaciones contrarias es inconsistencia técnica.

Catalog vigente no participa de esta clasificación.

### Read model autorizado

```text
GET /api/order-operations/orders/{operationalReference}/delivery
```

La consulta exige Session válida, Identity activa y `OrderOperationsAndBasicClosure` vigente. No requiere `Preparation` ni `PreparationEnablement`. Session/Identity no utilizable responde `401`, Responsibility faltante responde `403` y Order inexistente responde `404`.

La lectura abre una transacción corta `READ COMMITTED`, estabiliza Session, Identity y Responsibility, y obtiene el Estado de `OrderOperations` mediante una proyección coherente. No usa `FOR UPDATE` para la lectura ordinaria. Después resuelve en batch el `ProductOperationalName` vigente mediante la capacidad estrecha de Catalog, reutilizando la transacción; no existe SQL ni acceso a `DbContext` cross-module.

El contrato relevante por Content contiene:

- `incorporationId`;
- `incorporationOrdinal`;
- `contentOrdinal`;
- `productId`;
- `productOperationalName`;
- `instruction`;
- `totalQuantity`;
- `requiresPreparationAtConfirmation`;
- `readyQuantity` nullable;
- `deliveredQuantity`;
- `deliverableQuantity`;
- `remainingQuantity`.

No expone Status, WorkId, destination ni `appliedPrice`.

`ProductId` conserva la identidad autoritativa. `productOperationalName` es el nombre actual de Catalog, no un snapshot histórico: un rename posterior a Confirmation cambia la presentación de Delivery, y un Product inactive/retired continúa resolviéndose. Un Product referenciado que ya no existe es inconsistencia técnica y produce `500`; no existe fallback al UUID ni snapshot histórico del nombre en Delivery.

### Fórmulas y Delivery parcial

La obligación actual `totalQuantity` es F, según [Q/R/F de S7-I2](confirmation.md#q-r-y-f-s7-i2); Q permanece en el Content confirmado.

Para Prepared Content:

```text
total       = F
ready       = PreparationWork.ReadyQuantity
delivered   = DeliveryState.DeliveredQuantity
deliverable = ready - delivered
remaining   = total - delivered
```

Ready no disminuye al entregar. Por ejemplo, `total = 5`, `ready = 3`, `delivered = 2` produce `deliverable = 1`, `remaining = 3` y `ready` sigue siendo `3`.

Para Direct Content:

```text
total       = F
ready       = null / no aplicable
delivered   = DeliveryState.DeliveredQuantity
deliverable = total - delivered
remaining   = total - delivered
```

No se inventan `Ready = Total` ni un `PreparationWork` ficticio.

Delivery parcial está materializada: un Content puede tener `total = 5`, `delivered = 2`, `remaining = 3` sin identidad física por unidad, sub-items ni unidades físicas persistentes. Contents diferentes no se mezclan por `ProductId`.

### DeliverQuantity, autorización y transición

```text
POST /api/order-operations/incorporations/{incorporationId}/contents/{contentOrdinal}/deliver
Intent: DeliverQuantity
Body: { "quantity": integer }
Headers: Idempotency-Key UUID v4 + antiforgery
Target: (IncorporationId, ContentOrdinal)
```

El request no aporta `ProductId`, `WorkId`, actor, claim de Ready ni destination. Una intención nueva exige Session válida, Identity activa y `OrderOperationsAndBasicClosure` vigente; no exige Responsibility `Preparation` ni `PreparationEnablement`. Por ello una Identity puede entregar Prepared Content sin poder prepararlo. El actor procede exclusivamente de `AuthenticatedContext.IdentityId`.

Para Direct Content, `deliverable = F - DeliveredQuantity`. Para Prepared Content, `deliverable = ReadyQuantity - DeliveredQuantity`. En ambos casos la precondición es `0 < quantity <= deliverable` y la mutación es `DeliveredQuantity += quantity`; no hay clipping. La variante direct no crea un Ready ficticio y la prepared no modifica contadores de Preparation.

La transición está encapsulada en el método explícito `DeliveryState.Deliver(quantity, deliverableQuantity)`, que valida cantidad positiva, límite exacto e incremento. El service calcula la elegibilidad desde Content, ContentQuantityState y, cuando corresponde, Work; `DeliveryState` no conoce Catalog ni el modelo completo de Preparation y no expone un setter genérico.

### Transacción, locks y concurrencia

Una intención nueva sigue este orden materializado:

```text
BEGIN READ COMMITTED
→ advisory transaction lock del namespace Delivery por Idempotency-Key
→ estabilización de Session + Identity
→ replay/conflicto de Delivery command
→ OrderOperationsAndBasicClosure FOR SHARE
→ Order FOR UPDATE / validación de Freeze
→ IncorporationContent FOR SHARE
→ PreparationWork FOR UPDATE, si Prepared
→ DeliveryState FOR UPDATE
→ ContentQuantityState FOR UPDATE
→ validación de F y transición
→ QuantityDelivered History
→ DeliveryCommand
→ COMMIT
```

El orden es Content → PreparationWork cuando corresponde → DeliveryState → ContentQuantityState; no se invierte a DeliveryState → Work. Delivery Correction lee/bloquea estos mismos Estados en ese orden. No existen locks de larga duración, un lock global de Delivery ni espera automática hasta que exista Ready suficiente.

Ready y Delivery serializan sobre `PreparationWork` para Prepared Content. Si Ready bloquea primero, incrementa Ready y Delivery espera y observa el nuevo valor. Si Delivery bloquea primero, evalúa el Ready ya estabilizado, confirma o responde `409`, y Ready progresa después. Ambos órdenes conservan coherencia.

Direct y Prepared bloquean `DeliveryState FOR UPDATE`. En direct con `total = 5`, dos `Deliver(2)` pueden confirmar secuencialmente y dejar `Delivered = 4`; con `total = 3`, uno confirma y el otro responde `409`, sin clipping. Prepared aplica la misma semántica respecto de la cantidad Ready disponible.

### Historia, idempotencia y replay

Delivery confirma Historia separada de State mediante el evento `QuantityDelivered`, con:

- `HistoryId` UUID v7;
- `IncorporationId`;
- `ContentOrdinal`;
- `Quantity`;
- `ActorIdentityId`;
- `OccurredAt` UTC;
- `ResultingDeliveredQuantity`.

No guarda `SessionId`, Product name, instruction duplicada, snapshot de Ready, destination ni `appliedPrice`. No existen eventos redundantes `OrderDelivered` o `ContentCompleted`, y esta separación no constituye Event Sourcing.

`delivery_commands` persiste la intención comparable: `IdempotencyKey`, `ActorIdentityId`, `CommandKind = DeliverQuantity`, `IncorporationId`, `ContentOrdinal` y `Quantity`, además del resultado original. Mismo actor/key/intención hace replay; la misma key con actor o intención diferentes responde `409`. Delivery tiene namespace advisory propio, no reutiliza `preparation_commands` ni introduce un framework genérico de comandos.

El replay devuelve el resultado original persistido. Si key A dejó `delivered = 1`, key B dejó `delivered = 2` y luego se repite A, responde el `delivered = 1` original; no devuelve el Estado actual, no crea History y no muta Estado.

Un replay confirmado todavía exige Session utilizable e Identity activa, pero no reautoriza `OrderOperationsAndBasicClosure` después del efecto. Así, éxito con key X seguido de revocación de la Responsibility permite replay X con `200`, mientras una intención nueva con key Y recibe `403`. Identity desactivada o Session inválida produce `401`. El actor durable es `IdentityId`, nunca `SessionId`.

### Delivery Correction

Delivery Correction está implementada verticalmente mediante la intención explícita `CorrectDeliveryQuantity`. Retrae una cantidad positiva exacta del `DeliveredQuantity` efectivo vigente; puede reducirlo hasta cero, nunca lo incrementa y rechaza cantidades inválidas o superiores a la entrega efectiva sin clipping silencioso.

```text
POST /api/order-operations/orders/{orderId}/incorporations/{incorporationId}/contents/{contentOrdinal}/correct-delivery
Body: { "quantity": integer }
Headers: Idempotency-Key UUID v4 + antiforgery
```

La Correction modifica únicamente `DeliveryState.DeliveredQuantity`. El `IncorporationContent` confirmado, `ContentQuantityState` y `PreparationWork` permanecen sin cambios, y la Historia original `QuantityDelivered` no se elimina ni se reinterpreta. Cada efecto crea `DeliveryQuantityCorrected` History con cantidad previa, corregida y resultante, actor y timestamp UTC. State, History y resultado durable se confirman atómicamente.

El Functional Amount deriva inmediatamente del `DeliveredQuantity` efectivo corregido. La cantidad corregida vuelve a quedar ordinariamente entregable bajo las mismas reglas vigentes: para Content prepared continúa limitada por `ReadyQuantity`, mientras direct usa F. La Correction queda prohibida después de Liquidation/Freeze.

El comando mantiene idempotencia durable sobre el endpoint, target, body, actor y key exactos. El replay devuelve el resultado original sin repetir State ni History; una reutilización incompatible de la key responde conflicto.

### Errores de Delivery

- `400`: key, body, quantity o target estructuralmente inválido;
- `401`: Session no utilizable o Identity inactiva;
- `403`: intención nueva sin `OrderOperationsAndBasicClosure`;
- `404`: Content target inexistente;
- `409`: cantidad entregable insuficiente, Content completamente entregado bajo una key nueva, conflicto de idempotencia u otro conflicto de dominio conocido;
- `500`: Estado inconsistente, incluido flag/Work contradictorio, `DeliveryState` o `ContentQuantityState` faltante, R fuera de rango, `delivered > F`, Prepared `delivered > Ready` o inconsistencia estructural del Work.

No hay clipping ni auto-repair.
