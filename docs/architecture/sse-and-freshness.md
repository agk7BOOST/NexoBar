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

## SSE-04 — Autorización fail-closed y sesión

El endpoint SSE usa la Session opaca autenticada existente. Cada scope solicitado se autoriza en servidor y, antes de entregar una notificación, se comprueba que la autoridad actual sigue vigente. La pérdida de autorización deniega o termina el stream sin filtrar notificaciones posteriores ni revelar existencia de recursos no autorizados.

Para un scope de destino de Preparation se exige Session utilizable, Identity activa, responsabilidad `Preparation` y `PreparationEnablement` exacta del destino. La autorización no deriva de claims persistidos en la sesión.

Abrir, mantener vivo, heartbeatear o reconectar automáticamente un stream SSE no renueva `LastActivityAt` ni extiende la vida absoluta de la Session. Un navegador abandonado o en background no conserva autoridad por mantener el stream conectado.

Logout, revocación de Session, desactivación de Identity o reemplazo de la persona actuante invalidan la autoridad del stream anterior. La nueva Identity abre su propio stream autorizado y refresca sus reads activos.

## SSE-05 — Cliente autoritativo, carreras e intents inciertos

Para cada read sensible a SSE, el frontend usa fencing por generación y clave de read:

- al invalidar, incrementa de inmediato la generación y marca stale;
- programa un GET autoritativo;
- una respuesta sólo puede actualizar UI si su generación sigue siendo la vigente;
- hay como máximo un refresh activo por clave; otra invalidación durante ese refresh deja pendiente un único refresh posterior.

Este mecanismo coalesce duplicados y evita que un GET antiguo en vuelo sobrescriba un Estado más fresco. No exige un gestor global de State de negocio.

Una invalidación SSE nunca demuestra que una intención local haya tenido éxito. Los intents inciertos conservan endpoint, body, key y token exactos; se resuelven únicamente mediante retry idempotente y resultado autoritativo de comando/read. El evento relacionado no los limpia ni finaliza.

## SSE-06 — Primer vertical: Preparation

El primer vertical de Slice 8 usa exclusivamente:

```json
{
  "kind": "preparation.destination.changed",
  "scopeId": "<destination UUID>"
}
```

No incluye cantidades, precios, actor, hechos de Historia ni State completo. No se convierten los kinds de Historia en eventos SSE.

Tras commit, se invalida el destino cuando una mutación confirmada lo afecte: Confirmation que crea Work; Start; MarkReady; correcciones Start/Ready; Content Correction; Content Cancellation ordinaria; OperationalIntervention; Complete Order Cancellation; y Delivery o Delivery Correction cuando cambien límites o State mostrados por Preparation.

No se implementan aún `order.changed` general, SSE de Catalog, Inventory u OperationalConfiguration. Podrán reutilizar la infraestructura sólo mediante una decisión y vertical posterior explícitos.

## Implementación y verificación siguientes

La próxima implementación comienza por transporte/infraestructura SSE y después integra el vertical de destino de Preparation. La verificación proporcional futura cubre publicación sólo post-commit, silencio ante rollback, autorización exacta de destino, terminación por pérdida de autoridad, fencing de generación, duplicados/coalescing, refresh tras reconexión, intents inciertos sin cambio y un E2E de dos navegadores para frescura de Preparation.
