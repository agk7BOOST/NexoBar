# Contratos, Historia e idempotencia de OrderOperations

Este documento conserva las estructuras de Historia, matching durable y contratos comunes del módulo. Las transiciones y precondiciones se mantienen en [Confirmation](confirmation.md), [Preparation](preparation.md), [Delivery](delivery.md) y [terminación](ending.md); los resúmenes de contratos no las sustituyen.

## Estado e Historia

- `ConfirmationHistory` explica la Confirmación que originó el contenido y conserva `confirmedContext` histórico.
- `ConfirmationHistory` y el `IncorporationContent` persistido explican conjuntamente la existencia de una instruction confirmada; no existe `InstructionAdded History`.
- `PreparationWork` representa Estado operacional vigente y no se reconstruye ordinariamente desde Historia.
- `DeliveryState` representa Estado operacional vigente y `QuantityDelivered` explica cada incremento sin reconstruirlo ordinariamente desde Historia.
- La query de Preparation usa el Context vigente guardado por Order (`CurrentContextId` y `CurrentContextOperationalName`); no debe confundirse con `ConfirmationHistory.confirmedContext`.
- El progreso humano materializa Historia separada con los eventos `PreparationQuantityStarted` y `PreparationQuantityReady`. Preparation Correction materializa su propia Historia semántica, distinta de progreso, Content Correction, Content Cancellation, Delivery Correction y OperationalIntervention. Cada registro conserva `HistoryId` UUID v7, `WorkId`, `Quantity`, `ActorIdentityId`, `OccurredAt` UTC y el resultado de las cuatro cantidades: `TotalQuantity`, `PendingQuantity`, `InPreparationQuantity` y `ReadyQuantity`. Una Preparation Correction no requiere referencia a un evento Start o Ready anterior.
- No existen eventos `WorkCreated`, `Progress` genérico ni `WorkCompleted`. La Historia de Preparation no conserva `SessionId`, snapshot de nombre del Product ni duplicación de instruction.
- No existe todavía una query, API ni UI independiente de Historia de Preparation; sus hechos sí aparecen en la consulta terminal de Order History.
- No existe todavía una query ni UI independiente de Historia de Delivery; sus hechos sí aparecen en la consulta terminal de Order History.
- Esta separación no constituye Event Sourcing.

## Terminal Order History — MVP-FC-TOH: CLOSED

`GET /api/order-operations/order-history/{operationalReference}` es el read autorizado por `OrderOperationsAndBasicClosure` para una referencia exacta. Sólo devuelve una History cuando el Order terminó mediante Closure o Complete Cancellation. Una referencia malformada conserva su ProblemDetails específico; Order desconocido, activo o no terminal obtiene el resultado genérico no disponible. El lookup activo continúa usando su endpoint de Order activo y no consulta History como fallback. La History usa su endpoint dedicado y no vuelve a consultar el endpoint activo.

La consulta proyecta snapshots y hechos persistidos de OrderOperations en secciones estructuradas, respetando el orden de backend por sección; no produce una cronología global ni representa Event Store. Incluye Context final y cambios secuenciados con nombres anterior/nuevo, actor y tiempo; cada Incorporation ordinal conserva su confirmación, Context snapshot, actor y Contents; cada Content conserva cantidad confirmada, Applied Price original/efectivo, instruction, preparación y responsabilidad de Preparation cuando exista, excepción de Product no disponible, historial de correcciones de precio, Content Corrections y Cancellations, Preparation, Delivery y Delivery Corrections. Closure History presenta Liquidation/pago y Closure como hechos independientes. Complete Cancellation conserva su decisión terminal, descarte de PendingComposition y consecuencias por Content, y no aporta Liquidation, pago ni Closure.

`productOperationalNameSnapshot` es nullable por compatibilidad histórica. La vista presenta el snapshot cuando existe y, para `null`, exactamente «Nombre histórico no disponible»; no busca Catalog ni sustituye con un nombre o ID vigente. Contextos proceden de snapshots de OrderOperations, sin lookup runtime de OperationalConfiguration. `ActorIdentityId` estable se presenta como referencia técnica neutral, sin traducirlo mediante Identity administration.

El frontend expone la consulta exacta en una superficie explícitamente «Historial del pedido / Solo lectura», gated por OABC y sin affordances mutantes. History carga sólo tras una solicitud explícita; no hay polling ni scopes SSE terminales.

## Context actual, cambio e Historia — MVP-FC-CTX: CLOSED

`OrderOperations` posee el Context actual del Order (`CurrentContextId`, `CurrentContextOperationalName`), el Context Change History y el resultado durable de sus comandos. El nombre actual es un snapshot propiedad del Order. Reads de Order, Preparation y Delivery usan ese Estado de Order; no consultan OperationalConfiguration en vivo y no copian Context a `PreparationWork` o `DeliveryState`. La resolución de IDs configurados ocurre mediante la capacidad estrecha de Context; no hay acceso runtime cross-module a DbContext/schema ni FK cross-module.

La Primera Confirmación selecciona un Context configurado por ID y conserva su snapshot canónico en el Order y en su propia `ConfirmationHistory`. Cada Confirmación posterior conserva su Context de confirmación; un cambio posterior del Context actual no reescribe esa Historia.

### Comando y elegibilidad

```text
POST /api/order-operations/orders/{orderId}/context-changes
```

El body contiene `expectedCurrentContextId` y `newContextId`; la mutación requiere antiforgery, `Idempotency-Key` UUID v4 y Session utilizable/Identity activa con `OrderOperationsAndBasicClosure`. El ID destino debe resolver a un Context configurado activo. Un Context retirado o ausente conserva el conflicto 409 `order.context_change.target_context_not_found`, con significado de no seleccionable, y no se convierte en 404. La selección se estabiliza `FOR SHARE` dentro de la transacción del cambio. El Order debe seguir abierto operacionalmente: no estar congelado por Liquidation, cerrado ni completamente cancelado. Preparation Pending/InProgress/Ready, Delivery y `PendingComposition` no bloquean por sí mismos el cambio.

El cambio exitoso mantiene el mismo Order y `OperationalReference`, actualiza sólo su Context de coordinación actual y agrega una fila semántica a `OrderContextChangeHistory`: secuencia determinista por Order, IDs y snapshots anterior/nuevo, actor y timestamp. No recrea ni reinterpreta Incorporations, Content confirmado, AppliedPrice, Confirmation History, PreparationWork/responsabilidad/progreso, Delivery, Functional Amount ni PendingComposition.

Un cambio A→A devuelve el conflicto funcional `no_change`: no agrega Historia ni publica frescura. Un `expectedCurrentContextId` obsoleto devuelve conflicto explícito; el cliente debe releer Estado autoritativo y no rebasa ni reenvía automáticamente la intención. El cambio se rechaza después de Liquidation/Freeze, Closure o Complete Cancellation.

### Concurrencia e idempotencia

La fila del mismo Order es la frontera de serialización. A→B frente a A→C bloquea esa fila y compara `expectedCurrentContextId`: un comando confirma y el competidor queda obsoleto; no hay last-write-wins. Context Change y Liquidation se serializan por la misma frontera: si el cambio confirma primero, precede a Freeze; si Liquidation confirma primero, el cambio posterior se rechaza como congelado. No existe lock global ni exclusividad operacional por Contexto. La selección del Contexto destino sí retiene un lock compartido para serializarse con su lifecycle administrativo.

El resultado durable de Context Change hace replay exacto por igualdad de actor, Order, Context esperado, Context destino y key. Con Session válida e Identity activa, el replay devuelve el resultado originalmente confirmado antes de exigir de nuevo la responsabilidad vigente. Si K1 ejecutó A→B y K2 ejecutó B→C, replay de K1 devuelve el resultado A→B, pero el Order permanece en C: no reaplica, no agrega Historia y no publica frescura. Actor, Order, expected Context o target diferentes bajo la misma key producen conflicto.

### Historia y frescura

`OrderContextChangeHistory` conserva `PreviousContextId`/nombre snapshot, `NewContextId`/nombre snapshot, `ActorIdentityId`, `OccurredAtUtc` y una secuencia única por Order. Por ejemplo, First Confirmation en A, A→B y B→C dejan el Context actual C, `ConfirmationHistory` en A y dos cambios semánticos A→B y B→C. Son Estado e Historia diferenciados; esto no es Event Sourcing.

Un Context Change nuevo exitoso publica las invalidaciones existentes, después de commit: `order.active` para ese Order y los scopes distintos de Preparation representados por sus Works vigentes. Preparation vuelve a leer el Estado autoritativo de Order y presenta el Context nuevo sin recrear Work. Replay, rechazo y no-op no publican. No se crea scope SSE de Context, bus global de Context ni SSE de configuración. La semántica compartida de invalidación está en [SSE y frescura](../architecture/sse-and-freshness.md#order-sse-03--criterio-de-publicación-implementado).

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

## Applied Price Correction — Historia e idempotencia implementadas S7-I8D

Applied Price Correction registra un hecho semántico distinto que conserva el Content exacto `(IncorporationId, ContentOrdinal)`, `PreviousEffectiveAppliedPrice`, `ResultingEffectiveAppliedPrice`, el actor autenticado y `OccurredAt` UTC. Preserva la Confirmation original, todas las correcciones de precio previas y la Historia de Delivery. No persiste Functional Amount como duplicación de esta Historia.

La intención durable contiene `IdempotencyKey` UUID v4, `ActorIdentityId`, `CommandKind`, el target exacto y el precio efectivo resultante que el backend obtuvo de Catalog. Mismo actor/key/intención devuelve el resultado durable original sin nuevo State ni Historia; cambiar actor, target o precio resultante con la misma key produce conflicto. El replay confirmado conserva sus reglas normales de Session/Identity y no reevalúa el precio actual de Catalog. Una intención nueva cuyo precio actual de Catalog ya coincide con el efectivo se rechaza como no-op, sin resultado ni Historia de corrección durable.

State de precio efectivo, Historia y resultado durable se confirman atómicamente. El Estado vigente se lee sin replay de Historia; esta separación no constituye Event Sourcing.

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

Los items de request de First y Subsequent contienen `productId`, `quantity`, `instruction` optional/nullable e `unavailableProductExceptionRequested`. Los items de la respuesta confirmada contienen `productId`, `quantity`, `appliedPrice`, `instruction` e `unavailableProductExceptionApplied`; el lookup de Order devuelve también `instruction` y el marcador aplicado por item. La consulta autorizada de Preparation Work devuelve `productOperationalName` vigente e `instruction` nullable, y obtiene `productId` e instruction desde Content. Delivery expone `contentOrdinal` únicamente junto con `incorporationId` como identidad técnica del target. Ningún contrato público expone `draftLineId`.

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

## RF-PED-024/025 — intención, Historia y replay de excepción de disponibilidad

Los items de First y Subsequent persisten `IntentUnavailableProductExceptionRequested` como parte de la intención durable por línea. Los registros históricos anteriores se interpretan como `false`; la intención no se infiere de la disponibilidad actual de Catalog, de las responsabilidades del actor ni del resultado aplicado.

El contenido confirmado persiste `UnavailableProductExceptionApplied` en `IncorporationContent`. `true` significa que el snapshot autoritativo estaba no disponible y que una solicitud explícita autorizada permitió incorporar el Content; `false` significa que no se aplicó una excepción. En particular, una solicitud `true` frente a un Product disponible produce `Applied = false`. No es una copia de la intención.

`ConfirmationHistory` continúa aportando actor, timestamp y contexto de la incorporación. Junto con `UnavailableProductExceptionApplied` en `IncorporationContent`, hace trazable quién, cuándo, qué y si la excepción se aplicó, sin una tabla de eventos de intervención separada, motivo de texto libre ni framework genérico adicional.

La igualdad durable incluye `IntentUnavailableProductExceptionRequested` en cada línea: una misma key que cambie cualquier línea entre ordinaria y excepcional produce el conflicto de idempotencia existente. El replay exacto conserva actor/session utilizable e Identity activa, pero no vuelve a exigir las responsabilidades funcionales ni a consultar Catalog; tampoco crea otra Incorporation, History o publicación de invalidación.

La respuesta y la lectura de Order exponen `UnavailableProductExceptionApplied` como hecho histórico del Content. La lectura no expone la intención de retry como si fuera State vigente.

## Participación conservada para lifecycle de configuración

OrderOperations implementa `IContextOperationalParticipation` y `IDestinationOperationalParticipation` para la eliminación elegible bajo GeneralConfiguration. La primera considera Confirmation History, los IDs previo/nuevo de todo Context Change y el Context actual; la segunda considera cualquier PreparationWork persistido, incluidas obligaciones ya terminadas o reducidas a cero. Ambas adoptan la transacción del comando de OperationalConfiguration y consultan únicamente storage propietario de OrderOperations. Cierre, cancelación o cambio de Context no borran esa participación.

First Confirmation y Context Change resuelven sólo Contextos activos y retienen `FOR SHARE` en la fila configurada hasta commit. Retire puede aplicarse luego sin modificar el Order ni sus snapshots. Rename tampoco reescribe el Context adquirido: el nombre nuevo se usa para selecciones futuras. Para destinos, First/Subsequent Confirmation revalidan el indicador activo estabilizado por Catalog y rechazan con `order_operations.confirmation.preparation_destination_not_current` si el destino no es seleccionable. Retire no reescribe Work existente ni impide su progreso autorizado. Véase [lifecycle propietario](../operational-configuration/README.md#lifecycle-mínimo-de-contextos-y-destinos-de-preparación).
