# Autoridad de seguridad y fronteras pendientes

## Autoridad y actores

- En los comandos humanos autenticados materializados, el backend obtiene el actor de la Identity autenticada y estabilizada; el cliente no elige el actor durable. `IdentityId` es el actor, `SessionId` solo identifica la sesión.
- Sesión utilizable e Identity activa son precondiciones de esas operaciones, también del replay. Las capacidades se consultan en Estado vigente; no se derivan de claims persistidos en sesión. Cada intención nueva exige la capacidad y, cuando corresponde, habilitación exacta definidas por su módulo.
- Un replay de efecto ya confirmado sigue la regla local documentada: no reinterpreta su Historia por una revocación posterior de capacidad. No generalices ese tratamiento a intenciones nuevas.
- El alcance de protección existente es explícito: los endpoints anónimos pendientes de retrofit no se consideran protegidos por esta guía. Las reglas completas de cookie, antiforgery, credenciales, sesión y estabilización están en [Identities](../identities-and-capabilities/security.md); leerlas cuando la tarea afecte esos mecanismos.

## AD-SEC-06 — Provisioning inicial de la primera vía administrativa

Una instalación nueva se inicializa una sola vez mediante provisioning técnico. Ese provisioning crea una Identity activa, su credencial local y la asignación `GeneralConfiguration` inicial. Tras la primera inicialización exitosa, el bootstrap queda permanentemente indisponible.

No es funcionalidad ordinaria de la aplicación, no crea un administrador técnico permanente ni un superadministrador, y no es un endpoint HTTP anónimo permanente. Debe ser técnicamente trazable. Está materializado por `provision-initial-admin`, ejecutado bajo autoridad de deployment/proceso, con stdin redirigido para el secret y sin listeners HTTP. El detalle operativo, gates, retry, observabilidad y exits está en la [administración de Identity](../identities-and-capabilities/administration.md#provisioning-inicial-técnico-ad-sec-06).

Si después se pierden todos los caminos de `GeneralConfiguration`, el bootstrap no se reactiva: aplica el recovery extraordinario materializado por `AD-SEC-07`.

## AD-SEC-07 — Recovery extraordinario de GeneralConfiguration

La arquitectura de recovery combina un factor obligatorio en el provisioning inicial nuevo, `InstallationRecoveryState` como Estado del factor actual y su establecimiento/rotación ordinarios por una Identity activa con `GeneralConfiguration`. El factor sólo se persiste como verifier; el historial de retry de provisioning es independiente del Estado actual del factor.

`POST /api/installation-recovery-factor/rotate` es la vía ordinaria protegida: para una intención nueva requiere Session utilizable, Identity activa, `GeneralConfiguration`, antiforgery e `Idempotency-Key` UUID v4. Establece generación 1 solamente en una instalación genuinamente provisionada que no tenga State, o rota N a N+1. No se entrega ni requiere el factor anterior. Las instalaciones legacy con fact de provisioning y sin `InstallationRecoveryState` permanecen `recovery_not_configured`; la migración no inventa un factor y no hay bypass técnico MVP.

Ante pérdida total de administración ordinaria, sólo el proceso Host `recover-general-configuration` puede ejecutar el recovery. Recibe command ID UUID v4, Identity target existente y login opcional por CLI; recibe factor actual y secret de credencial nuevo por las dos líneas de stdin redirigido. No inicia HTTP/Kestrel. Su autoridad es la ejecución de deployment/proceso y el factor para comandos nuevos, no una Session, Identity o rol técnico. No existe endpoint HTTP anónimo/de break-glass, Identity técnica ni superadministrador.

Recovery activa únicamente la Identity target, asegura `GeneralConfiguration`, crea o reemplaza su `LocalCredential` y revoca sus Sessions. No crea, elimina o renombra Identities, no modifica otras responsabilidades ni habilitaciones de Preparation, y no modifica Catalog, Inventory ni Orders. Preserva el login existente cuando hay credencial salvo reemplazo explícito; una Identity sin credencial requiere login explícito y nunca se deriva desde el nombre operacional.

Establecimiento, rotación y recovery comparten un advisory lock PostgreSQL transaction-scoped; recovery bloquea además la fila Identity target. De este modo las operaciones sobre el mismo target se serializan sin una serialización global de administraciones no relacionadas. Activación, assignment, credencial, revocación de Sessions y command durable son una única transacción.

El command durable de recovery da idempotencia técnica: misma intención de command ID, target, login y credencial nueva devuelve `replayed_success`; otra intención durable devuelve `intent_conflict`. El factor no pertenece a la intención de replay: autoriza un comando nuevo, pero un command ya comprometido se reproduce sin revalidar el factor vigente aun después de rotación, sin reaplicar mutaciones.

## S9 — Secure Configuration Foundations

El retrofit de `OperationalConfiguration` está implementado: su listado administrativo de Preparation Responsibilities exige Session utilizable, Identity activa y `GeneralConfiguration`; su creación además exige antiforgery, actor derivado en servidor e idempotencia durable. El replay exacto preserva la regla de actor, command kind e intención canonical sin reautorizar `GeneralConfiguration`, pero nunca omite Session utilizable ni Identity activa.

Los endpoints pendientes de retrofit deben separar los reads administrativos de los operacionales y autorizar los writes por actor.

- `OperationalConfiguration` administra y lista Preparation Responsibilities con `GeneralConfiguration`.
- La administración de Catalog requiere `CatalogConfiguration`.
- La exploración operacional de Products para Composición/Confirmation requiere `OrderOperationsAndBasicClosure`; la visibilidad de la excepción de Product temporalmente no disponible exige además `OperationalIntervention`.
- No existe una responsabilidad `CatalogRead`.

Los comandos retrofitados de ambos módulos deben obtener en servidor el actor autenticado, exigir Session utilizable, Identity activa, responsabilidad vigente y antiforgery, y persistir la identidad durable del actor en su registro local de idempotencia. Un replay requiere igualdad exacta de actor, command kind e intención canonical; la misma key con actor o intención incompatibles produce conflicto. Esta regla reutiliza la semántica local de idempotencia; no crea una infraestructura compartida nueva.

## AD-SEC-05 — Visibilidad operacional del Pedido activo

Esta Adenda rige la lectura del Estado operacional actual de un Order. Una Identity activa con `OrderOperationsAndBasicClosure` puede localizar y leer los Orders operacionalmente activos dentro del alcance operacional de NexoBar. Para el MVP no hay restricción adicional por Identity creadora, owner, operador asignado, Session de origen, dispositivo, Context ni asignación personal del Order. Context es información de coordinación operacional, no una frontera de autorización.

La lectura operacional puede incluir identidad y referencia estables del Order, Context, Content confirmado relevante, PendingComposition, progreso y cumplimiento necesarios para entender la accionabilidad o Delivery, correcciones/cancelaciones/excepciones actuales, mutabilidad, Importe funcional, Liquidation y su elegibilidad, Freeze y Closure. Es Estado actual; no concede por sí sola Historia general, administración de Catalog o Identity, acceso a Inventory, `GeneralConfiguration` ni `OperationalConfiguration`.

Un Order entra al conjunto operacionalmente activo con First Confirmation. Permanece allí durante el recorrido ordinario, incluido Liquidated/Frozen mientras Closure sea el siguiente paso ordinario. Sale de ese conjunto cuando confirma Closure o Complete Order Cancellation; que todos los F sean cero no lo vuelve terminal ni elimina la visibilidad activa por sí solo.

La vista o suscripción que ya tenía autorización puede recibir la invalidación final de Closure o Complete Order Cancellation para reconciliar o retirar Estado que ya poseía. Después no se autoriza una nueva lectura o renovación `order.active`; esto termina la visibilidad previa y no concede acceso histórico post-terminal.

La misma frontera rige GETs/reads autoritativos del Estado operacional actual y la suscripción/entrega SSE `order.active`: SSE no puede exponer un alcance mayor que el read equivalente. `OperationalIntervention` conserva sólo sus reads estrechos de target y no concede visibilidad general del Order activo. Preparation continúa bajo `Preparation` más `PreparationEnablement` exacto y sus reads por destino; tampoco concede visibilidad de Order activo.

El retrofit AD-SEC-05 está implementado consistentemente en el lookup general de Order actual, Delivery, PendingComposition, evaluación de Applied Price Correction y la rama activa de evaluación de Complete Cancellation. Todos exigen Session utilizable, Identity activa, `OrderOperationsAndBasicClosure` y que el Order pertenezca al conjunto operacional activo; no conceden Historia ni lectura post-terminal.

## Pendientes

Este inventario no define políticas nuevas ni afirma seguridad global completa.

- retrofit global de autenticación/autorización para endpoints todavía anónimos, según corresponda: otros endpoints funcionales actuales no cubiertos. Confirmaciones ya tienen el retrofit de Slice 6;
- UX adicional de administración ordinaria del factor de recovery, si se prioriza; el establecimiento/rotación API y el recovery extraordinario `AD-SEC-07` ya están materializados;
- decisión normativa de parámetros de timeout (`PAR-SEC-02`) y política cuantitativa de brute-force/lockout;
- frontend administrativo restante más allá del vertical actual de GeneralConfiguration;
- elegibilidad de Delete Identity y coordinación con Historia;
- auditoría global de seguridad y consumo de autenticación en SSE;

- profundidad de Historia administrativa según los OPEN-TRA aplicables;
