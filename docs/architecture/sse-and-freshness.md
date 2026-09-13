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

Siguen sin implementar el vertical `order.changed` de Order activo y SSE de Catalog, Inventory u OperationalConfiguration. El alcance y las fronteras del vertical de Order activo se aprueban en SSE-09; los demás scopes sólo podrán reutilizar la infraestructura mediante una decisión y vertical posterior explícitos.

## SSE-09 — Vertical aprobado: frescura del Order activo

Esta decisión aprueba el siguiente vertical de Slice 8, todavía no implementado. Su scope exacto es:

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

### ORDER-SSE-03 — Criterio de publicación

Tras un commit exitoso se publica `order.changed(orderId)` cuando cambió la comprensión operacional actual de ese Order en una lectura autoritativa actual. El criterio no es que exista un hecho de Historia.

Las familias relevantes, cuando alteren esos reads, incluyen First/Subsequent Confirmation; creación, reemplazo, consumo o descarte de PendingComposition; cambios de Context; progreso y correcciones de Preparation que cambien deliverability o comprensión operacional actual; Delivery y Delivery Correction; Content Correction; Content Cancellation ordinaria; OperationalIntervention; Complete Order Cancellation; Applied Price Correction; Liquidation; y Closure.

Los cambios de Context usados para coordinación y presentación actual del Order invalidan este mismo scope; el MVP no crea un kind SSE de Context separado. Replay exacto, rechazo, no-op y rollback no publican una invalidación nueva. La secuencia continúa siendo `commit → invalidación best-effort`; nunca se publica antes del commit.

### ORDER-SSE-04 — Reconciliación de reads activos en frontend

Cuando un Order está abierto activamente para operar, el frontend agrega su scope exacto al snapshot de la conexión SSE de la App. Ante `order.changed`, cada owner de read activo incrementa la generación de su read, lo marca stale y programa un GET autoritativo. Sólo una respuesta de la generación vigente puede actualizar la UI; invalidaciones duplicadas y señales durante un refresh se coalescen según SSE-06.

Los reads actualmente montados o necesarios pueden incluir Delivery y cantidades deliverable; PendingComposition/marcador de composición; presentación actual de Order/Context; Estado económico e Importe funcional; elegibilidad y Estado terminal de Liquidation/Closure; evaluaciones abiertas de Correction/Cancellation, Complete Cancellation y Applied Price Correction. No se releen todos los endpoints de OrderOperations por recibir una señal: cada owner conserva su State autoritativo y fencing, y refresca sólo su read montado o necesario. El transporte SSE no se convierte en un gestor de State de negocio.

### ORDER-SSE-05 — Límites y caso operativo

`order.changed` transporta exclusivamente la identidad de invalidación. No contiene cantidades, precios, valores Ready/Delivered, Estado terminal, actor, Historia, tipo de corrección ni detalles semánticos de comandos.

El caso operativo que motiva este vertical es Preparation → Delivery: A tiene abierta la Delivery de Order X sin cantidad Ready entregable; B, autorizado para Preparation, confirma Ready de un Content de X. Ese commit puede publicar tanto `preparation.destination.changed(destinationId)` como `order.changed(orderId)`. Cada señal sirve una superficie autorizada distinta. A recibe sólo la invalidación de Order, relee Delivery desde la autoridad y descubre la cantidad entregable sin reload manual; no recibe por ello visibilidad de Preparation.

La señal no implica repricing automático de Catalog, actualizaciones de Inventory, resolución de intents inciertos, bypass de validación backend, ni garantía de que el siguiente comando verá exactamente el State antes renderizado. Idempotencia y controles de concurrencia permanecen necesarios. La pérdida de una notificación degrada únicamente frescura; en apertura o reconexión el frontend reconcilia autoritativamente sus reads suscriptos conforme a SSE-03.

## Vertical implementado: frescura de destino de Preparation

**Preparation destination SSE freshness está verticalmente implementado.** Esto no cierra Slice 8 completo.

El Host expone `GET /api/notifications/stream`, compatible con `EventSource` nativo y autenticado por la cookie de Session opaca same-origin. Cada conexión usa un snapshot fijo de scopes; el único scope de producción actual es `preparation.destination:<destinationId>`, con hasta ocho scopes por conexión en la implementación actual. El límite de scopes simultáneos, buffering por conexión, timeout de escritura y heartbeat son detalles técnicos acotados de la implementación, no reglas funcionales. El hub es en memoria y local al proceso, entrega de forma no bloqueante best-effort y limpia suscripciones ante desconexión, cancelación o shutdown.

El servidor valida la autoridad al suscribirse y antes de entregar notificaciones. Para Preparation exige Session utilizable, Identity activa, responsabilidad `Preparation` y `PreparationEnablement` exacta. La pérdida de cualquiera de esas condiciones termina o deja de entregar por el stream fail-closed. La validación y el heartbeat SSE no renuevan la inactividad ni la vida absoluta de la Session.

`preparation.destination.changed(destinationId)` se publica sólo después del commit exitoso si cambió el read autoritativo del destino. Está integrado en First/Subsequent Confirmation que crean Work, Start, MarkReady, Correct Start, Correct Ready, Content Correction, Content Cancellation ordinaria, OperationalIntervention, Complete Order Cancellation, Delivery y Delivery Correction. Se publica una vez por destino afectado, con deduplicación si la mutación alcanza varios. Los Contents direct sin `PreparationWork` no generan una notificación de Preparation.

Replay durable exacto, rechazo, no-op y rollback no emiten una invalidación nueva. Un error del publisher después de commit se contiene como falla de frescura best-effort: no revierte ni convierte en fallido el comando ya comprometido.

El App mantiene un único `EventSource` por Session/App activa y la unión de los destinos suscriptos. Un cambio de snapshot cierra y reemplaza el stream; sin scopes no abre ninguno. La reconexión manual usa backoff acotado con jitter y callbacks, conexiones y timers obsoletos quedan cercados por generación de lifecycle. La apertura inicial y cada reconexión exitosa señalan que los reads suscriptos están stale. El transporte distribuye exclusivamente invalidaciones tipadas y no posee State de negocio de Preparation.

Preparation conserva el GET autoritativo y su State/UI. Para el destino activo, la invalidación incrementa de inmediato la generación del read, marca stale y programa refetch. Sólo una respuesta o error de la generación vigente puede actualizar UI; hay un refresh activo y las señales durante él coalescen en un único seguimiento. Reemplazar destino o Identity cerca las respuestas anteriores. El refresh posterior a comando local y el de SSE usan el mismo coordinador. Si SSE se desconecta, sólo degrada frescura: lecturas y comandos HTTP continúan disponibles.

La señal SSE ni el State observado tras un refetch prueban el éxito de Start, Ready o Correction. Los intents inciertos conservan su retry exacto idempotente; su resultado autoritativo sigue siendo la única resolución.

El checkpoint Playwright dirigido usa dos cuentas/Identities distintas, dos Sessions y contextos de navegador independientes, ambas con `Preparation` y enablement exacto sobre un único destino. Con un Work pendiente de cantidad 1, A ya visualiza `Pending=1`, `InPreparation=0`, `Ready=0`; B ejecuta Start por UI y observa `Pending=0`, `InPreparation=1`. Sin reload, Refresh manual, reselección de destino ni API de mutación desde la prueba, la vista ya abierta de A llega a los mismos buckets mediante `commit → invalidación SSE → GET autoritativo`. Resultado dirigido: Playwright `1/1 passed`.

La evidencia focalizada incluye integración backend de invalidación `7/7`, regresión de OrderOperations en ese checkpoint `687/687`, freshness frontend de Preparation `14/14` más pruebas afectadas, y el E2E final `1/1`. Durante ese checkpoint se corrigió una colisión incidental de keys entre hermanos de la composición autenticada de App; no modifica la semántica SSE.

Permanece sin implementar el vertical aprobado `order.changed` para Order activo. S8-I4A queda bloqueado hasta que **S8-I4A0 — retrofit de autorización de lectura de Order activo** alinee los GETs/reads con AD-SEC-05; no se marca el scope SSE como implementado. Continúan diferidos SSE de producto/precio de Catalog, Inventory, OperationalConfiguration, un feed general de Identity/capabilities, listas/búsqueda global de Orders e Historia. No se afirma que todos esos scopes sean necesarios para cerrar Slice 8. La limitación MVP de una sola instancia backend activa continúa vigente; fan-out multi-instancia queda fuera de la implementación actual.
