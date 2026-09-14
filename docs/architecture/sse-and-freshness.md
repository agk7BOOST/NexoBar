# SSE y frescura multiusuario

Las decisiones de Slice 8 registradas aquí gobiernan la actualización activa del MVP. SSE mejora la frescura entre usuarios conectados; no es Estado de dominio, Historia, Event Sourcing, sincronización offline ni una cola durable de comandos.

## SSE-01 — Topología MVP y fan-out

El despliegue productivo MVP está limitado a **una única instancia activa** de la aplicación backend. El Host usa un hub de notificaciones en memoria para el fan-out SSE. Esta es una limitación de despliegue del MVP, no una invariante de dominio.

La publicación conserva una abstracción estrecha. Si un despliegue futuro requiere varias instancias activas, se preserva esa abstracción y se sustituye el fan-out entre instancias por PostgreSQL `LISTEN/NOTIFY` u otro mecanismo aprobado expresamente. El MVP no agrega Redis, outbox durable, broker de mensajes ni log de replay.

## SSE-02 — Semántica y entrega

Una notificación SSE es una invalidación no autoritativa: comunica que cambió un recurso o scope al que el receptor está autorizado. No transporta Estado de negocio suficiente para mutar State autoritativo en el frontend.

```text
notificación
→ marcar el read stale
→ GET autoritativo
→ renderizar el State vigente
```

No hay replay durable, backlog, dependencia de `Last-Event-ID` ni Event Sourcing. La pérdida o duplicación de notificaciones no puede corromper Estado de negocio: las operaciones y lecturas ordinarias siguen siendo autoritativas.

Las notificaciones se publican únicamente después del `COMMIT` exitoso de la transacción que cambió el Estado. Un rollback no emite nada. Se acepta la pérdida best-effort entre commit y publicación porque el Estado vigente puede releerse; no se introduce mensajería transaccional durable para este requisito de frescura.

## SSE-03 — Conexión, subscriptions y reconexión

Existe una conexión SSE por App/frontend activo y Session. Lleva un snapshot de los scopes autorizados que el App necesita actualmente. Si ese snapshot cambia materialmente, el cliente cierra el stream anterior y abre uno nuevo; no se crea una conexión por Work, Order o módulo.

Al abrir inicialmente o reconectar con éxito, el cliente trata los reads suscriptos como stale y los refresca desde la autoridad. Una falla SSE degrada sólo la frescura: comandos y lecturas HTTP ordinarias continúan siendo seguros y utilizables. La reconexión usa backoff acotado con jitter. No se persisten eventos en el cliente, no hay cola offline ni retry automático de comandos.

## SSE-04 — Autorización fail-closed

El endpoint SSE usa la Session opaca autenticada existente. Cada scope solicitado se autoriza en servidor y, antes de entregar una notificación, se comprueba que la autoridad actual sigue vigente. La pérdida de autorización deniega o termina el stream sin filtrar notificaciones posteriores ni revelar existencia de recursos no autorizados.

Para un scope de destino de Preparation se exige Session utilizable, Identity activa, responsabilidad `Preparation` y `PreparationEnablement` exacta del destino. La autorización no deriva de claims persistidos en la sesión.

## SSE-05 — La actividad SSE no renueva la Session

Abrir, mantener vivo, heartbeatear o reconectar automáticamente un stream SSE no renueva `LastActivityAt` ni extiende la vida absoluta de la Session. Un navegador abandonado o en background no conserva autoridad por mantener el stream conectado.

Logout, revocación de Session, desactivación de Identity o reemplazo de la persona actuante invalidan la autoridad del stream anterior. La nueva Identity abre su propio stream autorizado y refresca sus reads activos.

## SSE-06 — Cliente autoritativo, carreras y coalescing

Para cada read sensible a SSE, el frontend usa fencing por generación y clave de read:

- al invalidar, incrementa de inmediato la generación y marca stale;
- programa un GET autoritativo;
- una respuesta sólo puede actualizar UI si su generación sigue siendo la vigente;
- hay como máximo un refresh activo por clave; otra invalidación durante ese refresh deja pendiente un único refresh posterior.

Este mecanismo coalesce duplicados y evita que un GET antiguo en vuelo sobrescriba un Estado más fresco. No exige un gestor global de State de negocio.

## SSE-07 — SSE no resuelve intents inciertos

Una invalidación SSE nunca demuestra que una intención local haya tenido éxito. Los intents inciertos conservan endpoint, body, key y token exactos; se resuelven únicamente mediante retry idempotente y resultado autoritativo de comando/read. El evento relacionado no los limpia ni finaliza.

## SSE-08 — Primer vertical: Preparation

El primer vertical de Slice 8 usa exclusivamente:

```json
{
  "kind": "preparation.destination.changed",
  "scopeId": "<destination UUID>"
}
```

No incluye cantidades, precios, actor, hechos de Historia ni State completo. No se convierten los kinds de Historia en eventos SSE.

Tras commit, se invalida el destino cuando una mutación confirmada lo afecte: Confirmation que crea Work; Start; MarkReady; correcciones Start/Ready; Content Correction; Content Cancellation ordinaria; OperationalIntervention; Complete Order Cancellation; y Delivery o Delivery Correction cuando cambien límites o State mostrados por Preparation.

Los verticales `order.changed` de Order activo e `inventory.operation` están implementados conforme a SSE-09 y SSE-10. SSE de Catalog u OperationalConfiguration continúa diferido; los demás scopes sólo podrán reutilizar la infraestructura mediante una decisión y vertical posterior explícitos.

## SSE-09 — Vertical implementado: frescura del Order activo

El vertical implementado usa exclusivamente este scope:

```text
order.active:<orderId>
```

Representa exclusivamente un Order que el usuario abrió explícitamente para operar. No es un feed de todos los Orders, descubrimiento, frescura de búsqueda/listados, Historia, ni un stream tenant o global. La única señal es una invalidación no autoritativa:

```json
{
  "kind": "order.changed",
  "scopeId": "<order UUID>"
}
```

### ORDER-SSE-02 — Autoridad de lectura operacional

Suscribirse a `order.active:<orderId>` exige autoridad vigente: Session utilizable, Identity activa, `OrderOperationsAndBasicClosure` y visibilidad de lectura operacional actual conforme a AD-SEC-05. Durante el MVP esa visibilidad comprende cualquier Order operacionalmente activo dentro del alcance de NexoBar, sin filtro por creador, owner, asignación, Session, dispositivo o Context. La suscripción se autoriza al abrir y se revalida mientras el stream permanece conectado; la pérdida de autoridad termina o deja de entregar de forma fail-closed.

Esta frontera no infiere visibilidad universal de Orders, autoridad de escritura desde la lectura en general, ni autoridad de feed amplio desde `OperationalIntervention`. `OperationalIntervention` por sí sola no concede el scope general de Order activo; sus lecturas propietarias siguen siendo estrechas. `Preparation` tampoco concede `order.active` porque pueda afectar el Order: Preparation conserva su superficie por destino. Una suscripción no autorizada no revela existencia del Order.

Closure o Complete Order Cancellation permiten entregar sólo la invalidación final a una suscripción que ya estaba autorizada, para reconciliar o retirar Estado que ya poseía. Tras ese commit, `order.active` no puede autorizarse ni renovarse para ese Order; la excepción de invalidación final no concede lectura post-terminal ni Historia.

### ORDER-SSE-03 — Criterio de publicación implementado

Tras un commit exitoso se publica `order.changed(orderId)` cuando cambió la comprensión operacional actual de ese Order en una lectura autoritativa actual. El criterio no es que exista un hecho de Historia.

Las mutaciones implementadas que alteran esos reads publican después de commit: Subsequent Confirmation; Start, Discard y consumo de PendingComposition; Start/Ready y correcciones Start/Ready de Preparation; Delivery y Delivery Correction; Content Correction; Content Cancellation ordinaria; OperationalIntervention; Applied Price Correction; Liquidation/Freeze; Complete Order Cancellation; y Closure. No existe comando mutable de Context ni comando de reemplazo de PendingComposition. First Confirmation no publica este scope porque antes de crear el Order no puede existir una suscripción activa previamente autorizada.

El MVP no crea un kind SSE de Context separado. Replay exacto, rechazo, no-op, conflicto y rollback no publican; un comando lógico publica a lo sumo una vez por Order afectado. La secuencia continúa siendo `commit → invalidación best-effort`; un fallo del publisher después del commit no revierte el éxito de negocio. Las publicaciones de Preparation y Order son independientes y best-effort.

### ORDER-SSE-04 — Reconciliación de reads activos en frontend

Cuando un Order está abierto activamente para operar, el frontend agrega su scope exacto al snapshot de la conexión SSE de la App. Ante `order.changed`, cada owner de read activo incrementa la generación de su read, lo marca stale y programa un GET autoritativo. Sólo una respuesta de la generación vigente puede actualizar la UI; invalidaciones duplicadas y señales durante un refresh se coalescen según SSE-06.

Los reads actualmente montados o necesarios pueden incluir Delivery y cantidades deliverable; PendingComposition/marcador de composición; presentación actual de Order/Context; Estado económico e Importe funcional; elegibilidad y Estado terminal de Liquidation/Closure; evaluaciones abiertas de Correction/Cancellation, Complete Cancellation y Applied Price Correction. No se releen todos los endpoints de OrderOperations por recibir una señal: cada owner conserva su State autoritativo y fencing, y refresca sólo su read montado o necesario. El transporte SSE no se convierte en un gestor de State de negocio.

### ORDER-SSE-05 — Límites y caso operativo

`order.changed` transporta exclusivamente la identidad de invalidación. No contiene cantidades, precios, valores Ready/Delivered, Estado terminal, actor, Historia, tipo de corrección ni detalles semánticos de comandos.

El caso operativo que motiva este vertical es Preparation → Delivery: A tiene abierta la Delivery de Order X sin cantidad Ready entregable; B, autorizado para Preparation, confirma Ready de un Content de X. Ese commit puede publicar tanto `preparation.destination.changed(destinationId)` como `order.changed(orderId)`. Cada señal sirve una superficie autorizada distinta. A recibe sólo la invalidación de Order, relee Delivery desde la autoridad y descubre la cantidad entregable sin reload manual; no recibe por ello visibilidad de Preparation.

La señal no implica repricing automático de Catalog, actualizaciones de Inventory, resolución de intents inciertos, bypass de validación backend, ni garantía de que el siguiente comando verá exactamente el State antes renderizado. Idempotencia y controles de concurrencia permanecen necesarios. La pérdida de una notificación degrada únicamente frescura; en apertura o reconexión el frontend reconcilia autoritativamente sus reads suscriptos conforme a SSE-03.

## SSE-10 — Vertical implementado: frescura operacional de Inventory

### INV-SSE-01 — Un scope operacional

El vertical usa un único scope estático `inventory.operation`. Representa exclusivamente la superficie operacional actualmente montada y su read autoritativo `GET /api/inventory/operations/items`. La UI operacional vigente es un listado, no un detalle por Item; por ello no se agregan suscripciones por Item.

`inventory.item:<itemId>` fue descartado: no existe un GET autoritativo exacto del State actual de un Item, las filas visibles pueden exceder el límite de scopes del transporte y suscribir cada fila dejaría la cobertura frágil e incompleta. Un feed genérico más amplio, `inventory.changed`, también fue descartado porque mezclaría Estado operacional, configuración e Historia. `inventory.operation` es el scope mínimo que coincide con el read real. No se agregan SSE de configuración ni Historia.

### INV-SSE-02 — Autoridad

Abrir y mantener `inventory.operation` exige Session utilizable, Identity activa e `InventoryOperation` vigente. `InventoryConfiguration` por sí sola no concede este scope. La frontera coincide exactamente con `GET /api/inventory/operations/items`, que ya es list-wide, por lo que no requiere autorización individual por Item. La autoridad conectada se revalida antes de la entrega y se pierde fail-closed; la actividad SSE no renueva la inactividad de Session.

### INV-SSE-03 — Invalidación opaca

La única señal será:

```json
{
  "kind": "inventory.operation.changed"
}
```

No contiene UUID, `scopeId`, identidad de Item, cantidad, unidad, `MovementRevision`, naturaleza del Movimiento, actor, Conteo, resultado de Reconciliación ni payload de Historia. Su significado es solamente que el listado operacional autoritativo puede haber cambiado.

### INV-SSE-04 — Publicación

Se publicará sólo después de un nuevo commit exitoso que cambie el Estado operacional actual: creación de Item cuando aparece en el listado operacional, Entry, Manual Exit, Waste y Reconciliación que crea Movimiento, incluida la primera fijación de existencia cuando se materializa como ese cambio comprometido. No publican Conteo, Reconciliación `no_discrepancy` sin Movimiento, replay exacto, rechazo, no-op, conflicto ni rollback. La mera incorporación de Historia no es criterio de publicación.

### INV-SSE-05 — `MovementRevision` y Conteo

SSE es sólo una pista de frescura. `MovementRevision` conserva la guardia autoritativa de concurrencia: la notificación no valida un Conteo, no avanza una observación local, no vuelve válida una observación vieja ni reemplaza validaciones esperadas. Tras releer el listado, si `ObservedMovementRevision != AsOfMovementRevision`, el frontend muestra la observación stale y no permite la Reconciliación normal; se requiere un nuevo Conteo físico. La validación backend de Reconciliación sigue siendo obligatoria. Conteo no cambia por sí mismo el Estado registrado ni publica.

### INV-SSE-06 — Reconciliación frontend

`InventoryPanel` posee `GET /api/inventory/operations/items` y se suscribe a `inventory.operation` sólo mientras la superficie operacional autorizada está montada. Ante `inventory.operation.changed`, apertura inicial exitosa o reconexión exitosa, reconcilia desde la autoridad. `FreshnessReadCoordinator` incrementa la generación, cerca éxitos y errores stale, mantiene como máximo un GET activo y coalesce una única lectura posterior. SSE nunca muta cantidades o revisiones directamente ni introduce un gestor global de State de negocio. Count, Entry, Manual Exit, Waste, Reconciliación, refresh manual y el refresh visible tras creación de Item comparten esa ruta cercada cuando corresponde; los intents inciertos son independientes de SSE.

### INV-SSE-07 — Sin semántica terminal

Inventory no tiene lifecycle operacional implementado que retire Items de esta superficie. El vertical usa sólo invalidaciones ordinarias y no inventa retire, reactivate, delete, invalidación final ni lifecycle de corrección de unidad.

### INV-SSE-08 — Aislamiento funcional

Sólo commits reales de Inventory pueden publicar esta frescura. Order, Preparation, Catalog y otros módulos no publican `inventory.operation.changed`. Una cantidad registrada negativa sigue siendo Estado válido: la señal no significa disponibilidad, reserva, habilitación/deshabilitación de una operación ni suficiencia física; significa solamente que el Estado registrado cambió.

### INV-SSE-09 — Idempotencia y degradación

La secuencia es `commit de negocio → invalidación best-effort`. Replay exacto, no-op, rechazo, conflicto y rollback no republican. Un fallo del publisher posterior al commit no revierte la operación de Inventory. Si SSE no está disponible, reads y comandos ordinarios continúan utilizables y las reglas backend de revisión/concurrencia siguen siendo autoritativas; sólo degrada la frescura hasta el refresh manual o autoritativo actual. No se agrega replay durable, outbox ni broker.

### INV-SSE-10 — Checkpoint E2E

El checkpoint vertical dirigido usa dos Identities y Sessions separadas, ambas sólo con `InventoryOperation`. El fixture aislado establece Item X en cantidad 10. A ya ve el listado operacional y su stream real `inventory.operation` está abierto antes de la mutación; B registra Entry +5 por UI y observa 15. Sin reload, refresh manual, re-navegación ni API de mutación directa, la fila ya abierta de A relee y renderiza 15 mediante `commit → inventory.operation.changed → EventSource real → GET /api/inventory/operations/items → UI`. Playwright focalizado: `1 discovered`, `1 passed`, `0 failed`, `0 skipped`; no se consigna un exit code no capturado.

## Vertical implementado: frescura de destino de Preparation

**Preparation destination SSE freshness está verticalmente implementado.** Esto no cierra Slice 8 completo.

El Host expone `GET /api/notifications/stream`, compatible con `EventSource` nativo y autenticado por la cookie de Session opaca same-origin. Cada conexión usa un snapshot fijo de scopes; los scopes de producción actuales son `preparation.destination:<destinationId>`, `order.active:<orderId>` e `inventory.operation`, con hasta ocho scopes por conexión en la implementación actual. El límite de scopes simultáneos, buffering por conexión, timeout de escritura y heartbeat son detalles técnicos acotados de la implementación, no reglas funcionales. El hub es en memoria y local al proceso, entrega de forma no bloqueante best-effort y limpia suscripciones ante desconexión, cancelación o shutdown.

El servidor valida la autoridad al suscribirse y antes de entregar notificaciones. Para Preparation exige Session utilizable, Identity activa, responsabilidad `Preparation` y `PreparationEnablement` exacta. La pérdida de cualquiera de esas condiciones termina o deja de entregar por el stream fail-closed. La validación y el heartbeat SSE no renuevan la inactividad ni la vida absoluta de la Session.

`preparation.destination.changed(destinationId)` se publica sólo después del commit exitoso si cambió el read autoritativo del destino. Está integrado en First/Subsequent Confirmation que crean Work, Start, MarkReady, Correct Start, Correct Ready, Content Correction, Content Cancellation ordinaria, OperationalIntervention, Complete Order Cancellation, Delivery y Delivery Correction. Se publica una vez por destino afectado, con deduplicación si la mutación alcanza varios. Los Contents direct sin `PreparationWork` no generan una notificación de Preparation.

Replay durable exacto, rechazo, no-op y rollback no emiten una invalidación nueva. Un error del publisher después de commit se contiene como falla de frescura best-effort: no revierte ni convierte en fallido el comando ya comprometido.

El App mantiene un único `EventSource` por Session/App activa y la unión de los destinos suscriptos. Un cambio de snapshot cierra y reemplaza el stream; sin scopes no abre ninguno. La reconexión manual usa backoff acotado con jitter y callbacks, conexiones y timers obsoletos quedan cercados por generación de lifecycle. La apertura inicial y cada reconexión exitosa señalan que los reads suscriptos están stale. El transporte distribuye exclusivamente invalidaciones tipadas y no posee State de negocio de Preparation.

Preparation conserva el GET autoritativo y su State/UI. Para el destino activo, la invalidación incrementa de inmediato la generación del read, marca stale y programa refetch. Sólo una respuesta o error de la generación vigente puede actualizar UI; hay un refresh activo y las señales durante él coalescen en un único seguimiento. Reemplazar destino o Identity cerca las respuestas anteriores. El refresh posterior a comando local y el de SSE usan el mismo coordinador. Si SSE se desconecta, sólo degrada frescura: lecturas y comandos HTTP continúan disponibles.

La señal SSE ni el State observado tras un refetch prueban el éxito de Start, Ready o Correction. Los intents inciertos conservan su retry exacto idempotente; su resultado autoritativo sigue siendo la única resolución.

El checkpoint Playwright dirigido usa dos cuentas/Identities distintas, dos Sessions y contextos de navegador independientes, ambas con `Preparation` y enablement exacto sobre un único destino. Con un Work pendiente de cantidad 1, A ya visualiza `Pending=1`, `InPreparation=0`, `Ready=0`; B ejecuta Start por UI y observa `Pending=0`, `InPreparation=1`. Sin reload, Refresh manual, reselección de destino ni API de mutación desde la prueba, la vista ya abierta de A llega a los mismos buckets mediante `commit → invalidación SSE → GET autoritativo`. Resultado dirigido: Playwright `1/1 passed`.

La evidencia focalizada incluye integración backend de invalidación `7/7`, regresión de OrderOperations en ese checkpoint `687/687`, freshness frontend de Preparation `14/14` más pruebas afectadas, y el E2E final `1/1`. Durante ese checkpoint se corrigió una colisión incidental de keys entre hermanos de la composición autenticada de App; no modifica la semántica SSE.

## Vertical implementado: frescura del Order activo

**Active Order SSE freshness está verticalmente implementado.** El read operacional activo requiere Session utilizable, Identity activa, `OrderOperationsAndBasicClosure` y que el Order siga operacionalmente activo. No hay restricción MVP por creador, owner, asignación, Session, dispositivo o Context. Esta frontera está aplicada consistentemente al lookup general actual, Delivery, PendingComposition, evaluación de Applied Price Correction y rama activa de evaluación de Complete Cancellation. OperationalIntervention conserva su read estrecho y Preparation permanece por destino.

El scope es `order.active:<orderId>` y entrega sólo `{ "kind": "order.changed", "scopeId": "<order UUID>" }` para un Order abierto explícitamente. No es descubrimiento, lista/búsqueda, Historia ni feed tenant/global. La suscripción inicial y la entrega conectada ordinaria revalidan la misma frontera de lectura activa; `OperationalIntervention` o `Preparation` por sí solos no la conceden y los Orders no autorizados/no activos permanecen existence-safe.

`order.changed` normal cubre cambios operacionales no terminales, incluida Liquidation/Freeze: Liquidation no retira visibilidad activa porque Closure sigue pendiente. Sólo Closure y Complete Order Cancellation se entregan como `FinalForPreviouslyAuthorizedScope` interno a un stream ya autorizado, mediante el mismo evento opaco. No revela motivo terminal, no debilita `ActiveOrderReadState`, no permite suscripción nueva/reconectada ni lectura post-terminal, y retira exclusivamente ese scope. Duplicados finales y eventos posteriores del scope retirado se suprimen; otros scopes de la conexión siguen operativos. La pérdida de Session, Identity o responsabilidad invalida el stream completo.

El frontend conserva un `EventSource` por App/Session y agrega únicamente el scope exacto del Order activo al snapshot. Cada owner montado reconcilia su propio GET: Order/Estado económico-terminal, Delivery, PendingComposition, evaluación abierta de Applied Price Correction y State de correcciones/cancelaciones respaldado por Delivery. No relee todos los endpoints de OrderOperations. La invalidación marca stale de inmediato; fencing por generación impide que respuestas o errores viejos sobrescriban State nuevo, hay un refresh activo por read y señales durante él coalescen en un seguimiento. Cambio de Order/Identity cerca reads anteriores y apertura/reconexión reconcilia autoritativamente. El payload sigue siendo no autoritativo.

Después de la invalidación final, un GET activo puede devolver `404` porque el Order salió del alcance operacional. El frontend retira Estado y acciones activas, desmonta la suscripción por lifecycle y no reabre el Order repetidamente; no infiere el motivo terminal desde SSE. `order.changed` tampoco prueba éxito de comando: intents inciertos de Delivery/corrección conservan endpoint, body, key y retry idempotente exactos aunque el State visible se refresque.

Preparation puede publicar simultáneamente `preparation.destination.changed(destinationId)` y `order.changed(orderId)`: sirven consumidores autorizados distintos, sin conceder visibilidad general de Order a Preparation ni de Preparation a operadores de Order.

El checkpoint Playwright dirigido usa dos Identities, Sessions y contextos distintos sobre un Order activo con un Content preparado de cantidad 1: Work `InPreparation=1`, `Ready=0`, `Delivered=0`. A, con sólo `OrderOperationsAndBasicClosure`, abre Delivery y su stream `order.active` con `Deliverable=0`; B, con `Preparation` y enablement exacto, ejecuta MarkReady. B observa `Ready=1`; sin reload, Refresh ni reselección, A observa `Ready=1` y `Deliverable=1`, entrega 1 y termina con `Delivered=1`, `Deliverable=0`. Playwright focalizado: `1/1 passed`.

Evidencia proporcional: autorización de active Order `13/13`; regresión read/API afectada `169/169`; checkpoint de autorización OrderOperations `674/674`; expectativas de migración `2/2`; transporte active Order `49/49`; regresión transporte Preparation `39/39`; publicación Order `13/13`; regresión publicación Preparation `7/7`; checkpoint de publicación OrderOperations `760/762`, con los dos reads de migración originalmente fallidos verificados después individualmente green; frontend active Order `100 passed`; E2E final `1/1`. No se afirma una segunda corrida completa `762/762`.

La infraestructura compartida neutral actual es `NotificationSseProvider`, `NotificationSseTransport` y `FreshnessReadCoordinator`; los consumidores permanecen específicos de cada feature. La deuda de naming específica de Preparation quedó resuelta al reutilizar el coordinator para Inventory.

## Auditoría de cierre — Slice 8 SSE / frescura multiusuario

**Slice 8 — SSE / Multi-user Freshness: CLOSED.** El cierre significa que las superficies operacionales MVP requeridas están implementadas y verificadas verticalmente; no significa que todo módulo o read futuro tenga SSE.

- **Preparation operacional:** scope exacto por destino, `Preparation` más enablement exacto, relectura autoritativa y vertical de dos usuarios están cubiertos.
- **Order activo:** scope exacto `order.active:<orderId>`, autorización de lectura AD-SEC-05, publicaciones post-commit y la invalidación final para Closure/Complete Order Cancellation, reconciliación de reads montados y vertical Preparation→Delivery están cubiertos.
- **Inventory operacional:** scope list-wide `inventory.operation`, autorización `InventoryOperation`, mutaciones calificadas post-commit, `MovementRevision` y Conteo stale preservados, y vertical Entry de dos usuarios están cubiertos.

No hay contradicción normativa conocida, desajuste de autorización con el read equivalente, State derivado del payload, resolución SSE de intents inciertos ni superficie multiusuario MVP requerida sin cobertura. Las notificaciones actuales siguen siendo best-effort, invalidaciones no autoritativas seguidas de GET, no son streams durables, Event Sourcing, acknowledgements de comando ni garantías de replay; la reconexión reconcilia, no reproduce eventos.

Permanecen diferidos y no bloquean este cierre: SSE de Catalog/product, OperationalConfiguration, feed general de Identity/capabilities, listas o búsqueda global de Orders, frescura de Historia, fan-out multi-instancia, PostgreSQL `LISTEN/NOTIFY` u otro mecanismo futuro, replay/backlog durable, outbox/broker, lifecycle/retirement de Inventory, corrección o unidad de Inventory y la integración automática Order/Preparation → Inventory. La limitación MVP de una única instancia backend activa continúa vigente: el fan-out en memoria no es seguro para múltiples instancias; una evolución futura queda detrás de la abstracción de publisher existente.
