# NexoBar — índice técnico

El baseline técnico versionado se distribuye en los documentos siguientes. La [autoridad y el protocolo de trabajo](../AGENTS.md) siguen subordinados a las Sources normativas externas del Project y a decisiones posteriores aprobadas. Este índice no añade decisiones.

Consulta solo los temas afectados y sus dependencias explícitas. Los pendientes locales forman parte del contexto requerido cuando se extiende un comportamiento.

| Área o tarea | Documento propietario |
| --- | --- |
| Plataforma, módulos, dependencias y límites del producto | [Arquitectura](architecture/overview.md) |
| SSE, frescura multiusuario, topología MVP y límites de entrega | [SSE y frescura](architecture/sse-and-freshness.md) |
| Estado/Historia, EF, PostgreSQL, precisión, migraciones y conexión compartida | [Persistencia](architecture/persistence.md) |
| HTTP, errores e idempotencia transversal | [HTTP e idempotencia](architecture/http-and-idempotency.md) |
| Toolchain, Development y visibilidad técnica | [Development](architecture/development.md) |
| Seguridad global todavía pendiente | [Fronteras de seguridad](architecture/security-boundaries.md) |
| Product, precio y configuración prospectiva | [Catalog](catalog/README.md) |
| Snapshot y concurrencia Confirmation/Catalog | [Colaboración Catalog](catalog/confirmation-collaboration.md) |
| PreparationResponsibility, Context configurado y configuración operacional | [OperationalConfiguration](operational-configuration/README.md) |
| Identity, capacidades, sesión, antiforgery y destinos | [Seguridad de Identity](identities-and-capabilities/security.md) |
| Administración y conservación de GeneralConfiguration | [Administración de Identity](identities-and-capabilities/administration.md) |
| Inventory, Conteo, Reconciliación, Movimientos, Historia y pendientes | [Inventory](inventory/README.md) · [frontend](inventory/frontend.md) |
| Composición, Confirmation, Content, instruction y Context Change | [Confirmation](order-operations/confirmation.md) · [Context/Historia](order-operations/contracts-and-history.md#context-actual-cambio-e-historia-mvp-fc-ctx) |
| Preparation, buckets, Start/Ready, autorización y locks | [Preparation](order-operations/preparation.md) · [frontend](order-operations/preparation-frontend.md) |
| Delivery, direct/prepared y Delivery Correction | [Delivery](order-operations/delivery.md) · [frontend](order-operations/delivery-frontend.md) |
| Functional Amount, Liquidation, Freeze y Closure | [Terminación](order-operations/ending.md) |
| Contratos, Historia y replay de OrderOperations | [Contratos e Historia](order-operations/contracts-and-history.md) |
| Migraciones y límites abiertos de OrderOperations | [Migraciones](order-operations/migrations.md) · [pendientes](order-operations/pending.md) |
| Base Q/R/F implementada en S7-I2 | [Cantidad confirmada y obligación vigente](order-operations/confirmation.md#q-r-y-f-s7-i2) |
| App y coordinación web; composición/consulta/terminación | [Frontend transversal](frontend/README.md) · [workflow](order-operations/frontend.md) |
| Verificación, capas de pruebas y harness | [Testing](testing/verification.md) |

Las rutas de instrucciones locales empiezan en [backend](../backend/AGENTS.md), [frontend](../frontend/AGENTS.md), [tests backend](../backend/tests/AGENTS.md), [scripts](../scripts/AGENTS.md) y [docs](AGENTS.md).

## MVP-FC-CAT — Catalog Structure and Product Lifecycle: CLOSED

El mínimo de Groups, rename de Product y Retire/Reactivate de Product está implementado y verificado. Se eliminan como gaps completados: Group minimum capability, Product rename y Product retire/reactivate. El detalle funcional vive en [Catalog](catalog/README.md#mvp-fc-cat--catalog-structure-and-product-lifecycle-closed) y la evidencia en [Testing](testing/verification.md#mvp-fc-cat--catalog-structure-and-product-lifecycle).

**MVP-FC-AVAIL — Product Availability Intervention: CLOSED.** Catalog conserva la propiedad de Product State e `IsAvailable`; `OperationalIntervention` autoriza la mutación temporal mediante el read estrecho y el comando de Availability dedicados. No pertenece a `CatalogConfiguration` ni requiere `OrderOperationsAndBasicClosure`. La evidencia es MVP-FC-AVAIL-I1 (backend), MVP-FC-AVAIL-I2 (frontend) y MVP-FC-AVAIL-I3 (E2E dirigido). El detalle contractual y de autoridad está en [Catalog](catalog/README.md#mvp-fc-avail--product-availability-intervention-closed).

**MVP-FC-INV-LU — Inventory Element Lifecycle, Unit Correction and Eligible Definitive Delete: CLOSED.** Se eliminan como gaps completados el lifecycle de Inventory Element, la corrección de Unit y el Delete físico elegible. El detalle técnico está en [Inventory](inventory/README.md#mvp-fc-inv-lu--lifecycle-unit-correction-y-eligible-delete-closed), la UX y autoridad cliente en [Inventory frontend](inventory/frontend.md), y la evidencia por checkpoint en [Testing](testing/verification.md#mvp-fc-inv-lu--inventory-element-lifecycle-unit-correction-y-eligible-delete).

**MVP-FC-CTX — Context Configuration and Order Context Change: CLOSED.** OperationalConfiguration administra Contextos configurados; OrderOperations mantiene el Context actual del Order, Context Change History e idempotencia. La compatibilidad de upgrade, reglas, ownership y límites están en [OperationalConfiguration](operational-configuration/README.md#context-configurado--mvp-fc-ctx-closed), [OrderOperations](order-operations/contracts-and-history.md#context-actual-cambio-e-historia-mvp-fc-ctx) y [migraciones](order-operations/migrations.md#migración-de-context-configurado-mvp-fc-ctx). La evidencia backend, frontend y E2E focalizada está en [Testing](testing/verification.md#mvp-fc-ctx--context-configuration-and-order-context-change).

**Terminal Order History — CLOSED.** El contrato, la superficie read-only y la evidencia dirigida están registrados en [Contratos e Historia](order-operations/contracts-and-history.md#terminal-order-history--mvp-fc-toh-closed), [workflow frontend](order-operations/frontend.md#terminal-order-history--mvp-fc-toh-closed) y [Testing](testing/verification.md#mvp-fc-toh--terminal-order-history).

Los frontiers restantes de MVP Functional Completion son Inventory Movement Correction, eligible Product Delete y eligible Identity Delete. El siguiente frontier planificado es **Inventory Movement Correction**.

## Estado actual de Slice

**Slice 8 — SSE / Multi-user Freshness: CLOSED.** Preparation por destino, Order activo e Inventory operacional son las superficies MVP de frescura implementadas y verificadas verticalmente. La decisión, evidencia y límites —incluida la única instancia backend activa— pertenecen a [SSE y frescura](architecture/sse-and-freshness.md). Los feeds de otros módulos y mecanismos futuros multi-instancia permanecen diferidos; este cierre no inicia ni diseña el siguiente Slice.

**Slice 9 — Secure Configuration Foundations: CLOSED.** Su propósito —provisioning seguro de la primera vía administrativa, fronteras de configuración por capacidad, actor derivado en servidor, idempotencia durable consciente del actor, separación administrativa/operacional de Catalog, responsabilidades actuales autoritativas, administración utilizable de General Configuration y recovery tras pérdida de acceso ordinario— está implementado y documentado. Este cierre no afirma una auditoría global de todos los endpoints ni completa el roadmap funcional posterior.

**S9-I0 — Initial Installation Provisioning: CLOSED.** `provision-initial-admin` crea atómicamente la vía inicial y el factor de recovery generación 1; el fact durable, backfill conservador, subcomando Host sin HTTP, serialización y retry seguro están materializados. El detalle operativo vive en [Administración de Identity](identities-and-capabilities/administration.md#provisioning-inicial-técnico-ad-sec-06).

**S9-I1 — Secure Catalog & OperationalConfiguration Foundations: CLOSED.** OperationalConfiguration protege read/create administrativos con `GeneralConfiguration`, actor durable e idempotencia exacta; Catalog separa administración `CatalogConfiguration`, browse operacional `OrderOperationsAndBasicClosure` y visibilidad de no disponibles con `OperationalIntervention`. La colaboración Catalog → OperationalConfiguration para Preparation es estrecha y explícita.

**S9-I2 — General Configuration Administration Vertical: CLOSED.** La UI conecta las operaciones administrativas de Identity con sesiones y endpoints asegurados reales, preserva la vía administrativa final y cubre el reemplazo de credencial propia. El detalle de contrato está en [Administración de Identity](identities-and-capabilities/administration.md#vertical-web-s9-i2--general-configuration) y la evidencia en [Testing](testing/verification.md).

**S9-I3 — Extraordinary Recovery: CLOSED.** El factor obligatorio para instalaciones nuevas, su establecimiento/rotación ordinarios y `recover-general-configuration` como modo Host sin HTTP restauran una vía `GeneralConfiguration` tras pérdida de acceso ordinario, con atomicidad e idempotencia técnica durable. El detalle operativo y los límites están en [Administración de Identity](identities-and-capabilities/administration.md#recovery-factor-ordinario-y-recovery-extraordinario-ad-sec-07).

**S10-I1 — Incorporación excepcional de Product no disponible: CLOSED.** First y Subsequent Confirmation aceptan intención excepcional por línea, exigen ambas responsabilidades para comandos nuevos, conservan replay consciente de actor e intención, y persisten el marcador aplicado histórico sin mutar Catalog.

**S10-I2 — Frontend flow for exceptional unavailable-Product incorporation: CLOSED.** El workflow operacional muestra Products no disponibles al actor dual, mantiene Add ordinario deshabilitado, conserva la intención por línea y distingue «Intervención solicitada» de la marca histórica aplicada.

**S10-I3 — Cross-capability E2E for unavailable Product intervention: CLOSED.** El recorrido Playwright dirigido prueba las fronteras reales de `/current`, browse operacional, rechazo directo del actor base, acción UI explícita del actor dual, Content aplicado y disponibilidad inmutable de Catalog.

**Slice 10 — Operational Intervention on Unavailable Products: CLOSED.** RF-PED-024/RF-PED-025 ya no son un gap pendiente: la incorporación excepcional exige intención explícita por línea y `OrderOperationsAndBasicClosure + OperationalIntervention`; no habilita por sí sola cambios administrativos de disponibilidad.

**Preparation Configuration Completion: CLOSED.** La UI de `GeneralConfiguration` crea y lista Preparation Responsibilities; la UI de `CatalogConfiguration` configura el destino de Preparation de Products existentes. La evidencia focalizada está en [Testing](testing/verification.md#mvp-fc-prep--preparation-configuration-completion). Este cierre no inicia un Slice ni atribuye una capacidad nueva a OrderOperations.

El trabajo restante preservado fuera de estos cierres incluye el lifecycle completo de Preparation Responsibility (rename, retiro/reactivación, delete, ordering y gestión de estaciones); Inventory Movement Correction; eligible Product Delete; eligible Identity Delete; administración arbitraria de Sessions; y hardening de piloto/RNF. Estos frontiers permanecen no iniciados. No se reabren UI de bootstrap, recovery anónimo o un superadministrador técnico.

Solo para trazabilidad: [checkpoints históricos de slices](history/slice-checkpoints.md) y [auditoría de esta reorganización](context-audit.md). No son fuentes de nuevas decisiones ni lecturas de arranque obligatorias.
