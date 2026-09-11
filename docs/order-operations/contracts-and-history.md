# Contratos, Historia e idempotencia de OrderOperations

Este documento conserva las estructuras de Historia, matching durable y contratos comunes del módulo. Las transiciones y precondiciones se mantienen en [Confirmation](confirmation.md), [Preparation](preparation.md), [Delivery](delivery.md) y [terminación](ending.md); los resúmenes de contratos no las sustituyen.

## Estado e Historia

- `ConfirmationHistory` explica la Confirmación que originó el contenido y conserva `confirmedContext` histórico.
- `ConfirmationHistory` y el `IncorporationContent` persistido explican conjuntamente la existencia de una instruction confirmada; no existe `InstructionAdded History`.
- `PreparationWork` representa Estado operacional vigente y no se reconstruye ordinariamente desde Historia.
- `DeliveryState` representa Estado operacional vigente y `QuantityDelivered` explica cada incremento sin reconstruirlo ordinariamente desde Historia.
- La query de Preparation usa `Order.Context` vigente; no debe confundirse con `ConfirmationHistory.confirmedContext`.
- El progreso humano materializa Historia separada con los eventos `PreparationQuantityStarted` y `PreparationQuantityReady`. Preparation Correction materializa su propia Historia semántica, distinta de progreso, Content Correction, Content Cancellation, Delivery Correction y OperationalIntervention. Cada registro conserva `HistoryId` UUID v7, `WorkId`, `Quantity`, `ActorIdentityId`, `OccurredAt` UTC y el resultado de las cuatro cantidades: `TotalQuantity`, `PendingQuantity`, `InPreparationQuantity` y `ReadyQuantity`. Una Preparation Correction no requiere referencia a un evento Start o Ready anterior.
- No existen eventos `WorkCreated`, `Progress` genérico ni `WorkCompleted`. La Historia de Preparation no conserva `SessionId`, snapshot de nombre del Product ni duplicación de instruction.
- No existe todavía query, API ni UI de Historia de Preparation.
- No existe todavía query ni UI de Historia de Delivery.
- Tampoco está materializado Change Context.
- Esta separación no constituye Event Sourcing.

## OperationalIntervention — Historia y Estado aprobados S7-INT-D

INT-03 define Pending/InPreparation/Ready/Total como obligación de cumplimiento actual, no producción histórica acumulada. INT-05 sitúa la procedencia de Cancellation tras trabajo real en Historia semántica, sin introducir `CancelledFromInPreparation` ni `CancelledFromReady` en State.

La Historia implementada en S7-I6D registra que el trabajo realmente empezó o llegó a Ready y que posteriormente cesó esa obligación, distinguiendo intervención desde InPreparation de intervención desde Ready. OperationalIntervention tiene Historia semántica distinta de Cancellation ordinaria, Content Correction, Preparation Correction y Delivery Correction. No se reescriben ni reinterpretan los Start/Ready originales como errores de registro.

C más los buckets actuales permite leer la obligación operacional vigente sin replay de Historia, pero no identifica por sí solo si la cantidad fue cancelada en Pending, InPreparation o Ready. Esa procedencia se conserva en Historia; no se agregan contadores de etapa para obtenerla desde State.

Existen dos intenciones explícitas de intervención, una para InPreparation y otra para Ready, con idempotencia durable UUID v4. Las [transiciones INT-01/02 y límites INT-04/06/07](preparation.md#operationalintervention--decisiones-aprobadas-s7-int-d) están implementados verticalmente en S7-I6D, incluida la lectura estrecha del target. Ambas intenciones preservan Q, R, Delivered y Functional Amount; C sigue siendo la única deducción por cancelación. Este registro no afirma una query/API/UI de Historia implementada.

## Complete Order Cancellation — Historia e idempotencia implementadas S7-I7D

Se registra una única decisión semántica Order-level de Complete Cancellation, distinguible de cancelaciones parciales independientes, OperationalIntervention, Content Correction, Liquidation y Closure. Su resultado/Historia conserva:

- Order;
- actor;
- timestamp UTC;
- Estado terminal resultante;
- si existía PendingComposition y fue descartado dentro de la decisión (CAN-01);
- consecuencias semánticas por Content/etapa afectada suficientes para explicar la procedencia de la cantidad cancelada.

Se preservan las Historias originales de Confirmation, Start, Ready y Delivery. La procedencia desde Direct/Pending, InPreparation o Ready pertenece a las consecuencias semánticas; no se duplican representaciones históricas equivalentes innecesariamente ni se reconstruye el Estado ordinario por event sourcing. Descartar ediciones no confirmadas no produce hechos ficticios de Cancellation de cantidad. CAN-04 tampoco produce hechos de Content Cancellation/intervención con cantidad cero cuando todos los F ya eran cero; sí conserva la decisión terminal Order-level.

El comando es una sola intención durable de alcance Order con `Idempotency-Key` UUID v4. State, descarte de PendingComposition cuando exista, Historia y resultado durable forman una única operación atómica; no es un batch visible al cliente de comandos o claves de idempotencia hijos independientes.

**CAN-05:** replay exacto con la misma key durable devuelve el resultado original y no crea nuevo Estado ni Historia. Una intención nueva con otra key sobre un Order ya completamente cancelado se rechaza como terminal, sin segunda cancelación. Todos F=0 sin el hecho/Estado terminal no equivalen a una Complete Cancellation previa.

Las [reglas de terminación CAN-01..05](ending.md#complete-order-cancellation--implementación-vertical-s7-i7d) y la [autorización CAN-02](../identities-and-capabilities/security.md#complete-order-cancellation--autoridad-implementada-can-02--s7-i7d) están implementadas verticalmente. Este registro conserva la semántica de la operación, sin convertirla en comandos hijos independientes.

## Contratos e idempotencia

Los comandos humanos de Preparation usan un namespace durable local de `OrderOperations`. La intención persistida contiene `IdempotencyKey` UUID v4, `ActorIdentityId`, `CommandKind`, `WorkId`, `Quantity` y un resultado estable. Los kinds materializados actuales son `StartPreparationQuantity` y `MarkPreparationQuantityReady`; las Preparation Corrections aprobadas incorporarán kinds explícitos propios, sin reutilizar ni reinterpretar esos progresos.

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
