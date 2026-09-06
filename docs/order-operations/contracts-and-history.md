# Contratos, Historia e idempotencia de OrderOperations

Este documento conserva las estructuras de Historia, matching durable y contratos comunes del módulo. Las transiciones y precondiciones se mantienen en [Confirmation](confirmation.md), [Preparation](preparation.md), [Delivery](delivery.md) y [terminación](ending.md); los resúmenes de contratos no las sustituyen.

## Estado e Historia

- `ConfirmationHistory` explica la Confirmación que originó el contenido y conserva `confirmedContext` histórico.
- `ConfirmationHistory` y el `IncorporationContent` persistido explican conjuntamente la existencia de una instruction confirmada; no existe `InstructionAdded History`.
- `PreparationWork` representa Estado operacional vigente y no se reconstruye ordinariamente desde Historia.
- `DeliveryState` representa Estado operacional vigente y `QuantityDelivered` explica cada incremento sin reconstruirlo ordinariamente desde Historia.
- La query de Preparation usa `Order.Context` vigente; no debe confundirse con `ConfirmationHistory.confirmedContext`.
- El progreso humano materializa Historia separada con los eventos `PreparationQuantityStarted` y `PreparationQuantityReady`. Cada registro conserva `HistoryId` UUID v7, `WorkId`, `Quantity`, `ActorIdentityId`, `OccurredAt` UTC y el resultado de las cuatro cantidades: `TotalQuantity`, `PendingQuantity`, `InPreparationQuantity` y `ReadyQuantity`.
- No existen eventos `WorkCreated`, `Progress` genérico ni `WorkCompleted`. La Historia de Preparation no conserva `SessionId`, snapshot de nombre del Product ni duplicación de instruction.
- No existe todavía query, API ni UI de Historia de Preparation.
- No existe todavía query ni UI de Historia de Delivery.
- Tampoco está materializado Change Context.
- Esta separación no constituye Event Sourcing.

Los comandos humanos de Preparation usan un namespace durable local de `OrderOperations`. La intención persistida contiene `IdempotencyKey` UUID v4, `ActorIdentityId`, `CommandKind`, `WorkId`, `Quantity` y un resultado estable. Los kinds actuales son `StartPreparationQuantity` y `MarkPreparationQuantityReady`.

- mismo actor/key/kind/work/quantity produce replay;
- la misma key con actor, kind, work o quantity incompatible produce `409`;
- `SessionId` no es actor durable;
- el replay exige Session válida e Identity activa, pero no vuelve a exigir la capability vigente cuando el efecto ya fue confirmado;
- el replay no duplica Estado ni Historia y devuelve el resultado original persistido, no el Estado posterior actual del Work.

Delivery usa su propio namespace y registro durable `delivery_commands`, con `DeliverQuantity` como único kind actual. Conserva actor, target `(IncorporationId, ContentOrdinal)`, quantity y resultado original; aplica las semánticas de replay y reautorización descritas en [Delivery](delivery.md). No reutiliza `preparation_commands`.

Los contenidos durables de los comandos First y Subsequent tienen PK `(idempotency_key, line_ordinal)` y persisten `product_id`, `quantity` e `instruction` canonical. `lineOrdinal` es técnico y canonical: se deriva después de ordenar las líneas semánticas y no depende del orden HTTP.

El matching de intención incluye `ProductId`, `Quantity` y canonical instruction, además del resto de la intención ya existente. La misma key con instruction diferente produce conflicto; whitespace o line endings equivalentes y distinto orden del array producen replay. El replay no reconsulta `Catalog`, no recrea Work y reproduce el estado persistido.

- El lookup de Order se reconstruye exclusivamente desde `OrderOperations`; `Catalog` no reconstruye condiciones históricas.
- Las propiedades JSON autoritativas no reconocidas se rechazan en los comandos donde esta regla está materializada.

Los items de request de First y Subsequent contienen `productId`, `quantity` e `instruction` optional/nullable. Los items de la respuesta confirmada contienen `productId`, `quantity`, `appliedPrice` e `instruction`; el lookup de Order devuelve también `instruction` nullable por item. La consulta autorizada de Preparation Work devuelve `productOperationalName` vigente e `instruction` nullable, y obtiene `productId` e instruction desde Content. Delivery expone `contentOrdinal` únicamente junto con `incorporationId` como identidad técnica del target. Ningún contrato público expone `draftLineId`.

Contratos de terminación y marcador de Slice 6:

```text
POST /api/orders/{orderId}/pending-composition
GET  /api/orders/{orderId}/pending-composition
POST /api/orders/{orderId}/pending-composition/{pendingCompositionId}/discard
POST /api/order-operations/orders/{operationalReference}/liquidate-simple
POST /api/order-operations/orders/{operationalReference}/record-external-collection
POST /api/orders/{orderId}/close
```

- En una Confirmación posterior, el matching incluye actor, `Order`, marcador exacto `pendingCompositionId` e items canonicalizados; el orden del array no lo altera.

- `operationalReference` es opaca en HTTP y OpenAPI, aunque actualmente derive internamente del Order ID.
