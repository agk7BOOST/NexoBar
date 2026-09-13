# Autoridad de seguridad y fronteras pendientes

## Autoridad y actores

- En los comandos humanos autenticados materializados, el backend obtiene el actor de la Identity autenticada y estabilizada; el cliente no elige el actor durable. `IdentityId` es el actor, `SessionId` solo identifica la sesión.
- Sesión utilizable e Identity activa son precondiciones de esas operaciones, también del replay. Las capacidades se consultan en Estado vigente; no se derivan de claims persistidos en sesión. Cada intención nueva exige la capacidad y, cuando corresponde, habilitación exacta definidas por su módulo.
- Un replay de efecto ya confirmado sigue la regla local documentada: no reinterpreta su Historia por una revocación posterior de capacidad. No generalices ese tratamiento a intenciones nuevas.
- El alcance de protección existente es explícito: los endpoints anónimos pendientes de retrofit no se consideran protegidos por esta guía. Las reglas completas de cookie, antiforgery, credenciales, sesión y estabilización están en [Identities](../identities-and-capabilities/security.md); leerlas cuando la tarea afecte esos mecanismos.

## AD-SEC-05 — Visibilidad operacional del Pedido activo

Esta Adenda rige la lectura del Estado operacional actual de un Order. Una Identity activa con `OrderOperationsAndBasicClosure` puede localizar y leer los Orders operacionalmente activos dentro del alcance operacional de NexoBar. Para el MVP no hay restricción adicional por Identity creadora, owner, operador asignado, Session de origen, dispositivo, Context ni asignación personal del Order. Context es información de coordinación operacional, no una frontera de autorización.

La lectura operacional puede incluir identidad y referencia estables del Order, Context, Content confirmado relevante, PendingComposition, progreso y cumplimiento necesarios para entender la accionabilidad o Delivery, correcciones/cancelaciones/excepciones actuales, mutabilidad, Importe funcional, Liquidation y su elegibilidad, Freeze y Closure. Es Estado actual; no concede por sí sola Historia general, administración de Catalog o Identity, acceso a Inventory, `GeneralConfiguration` ni `OperationalConfiguration`.

Un Order entra al conjunto operacionalmente activo con First Confirmation. Permanece allí durante el recorrido ordinario, incluido Liquidated/Frozen mientras Closure sea el siguiente paso ordinario. Sale de ese conjunto cuando confirma Closure o Complete Order Cancellation; que todos los F sean cero no lo vuelve terminal ni elimina la visibilidad activa por sí solo.

La vista o suscripción que ya tenía autorización puede recibir la invalidación final de Closure o Complete Order Cancellation para reconciliar o retirar Estado que ya poseía. Después no se autoriza una nueva lectura o renovación `order.active`; esto termina la visibilidad previa y no concede acceso histórico post-terminal.

La misma frontera rige GETs/reads autoritativos del Estado operacional actual y la suscripción/entrega SSE `order.active`: SSE no puede exponer un alcance mayor que el read equivalente. `OperationalIntervention` conserva sólo sus reads estrechos de target y no concede visibilidad general del Order activo. Preparation continúa bajo `Preparation` más `PreparationEnablement` exacto y sus reads por destino; tampoco concede visibilidad de Order activo.

El retrofit AD-SEC-05 está implementado consistentemente en el lookup general de Order actual, Delivery, PendingComposition, evaluación de Applied Price Correction y la rama activa de evaluación de Complete Cancellation. Todos exigen Session utilizable, Identity activa, `OrderOperationsAndBasicClosure` y que el Order pertenezca al conjunto operacional activo; no conceden Historia ni lectura post-terminal.

## Pendientes

Este inventario no define políticas nuevas ni afirma seguridad global completa.

- retrofit global de autenticación/autorización para endpoints todavía anónimos, según corresponda: Catalog, OperationalConfiguration y otros endpoints funcionales actuales no cubiertos. Confirmaciones ya tienen el retrofit de Slice 6;
- bootstrap productivo de Identity y credenciales;
- implementación de recovery extraordinario (`AD-SEC-01`) y UX de recovery ordinario;
- decisión normativa de parámetros de timeout (`PAR-SEC-02`) y política cuantitativa de brute-force/lockout;
- frontend administrativo completo;
- elegibilidad de Delete Identity y coordinación con Historia;
- auditoría global de seguridad y consumo de autenticación en SSE;

- profundidad de Historia administrativa según los OPEN-TRA aplicables;
