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
| PreparationResponsibility y su lifecycle pendiente | [OperationalConfiguration](operational-configuration/README.md) |
| Identity, capacidades, sesión, antiforgery y destinos | [Seguridad de Identity](identities-and-capabilities/security.md) |
| Administración y conservación de GeneralConfiguration | [Administración de Identity](identities-and-capabilities/administration.md) |
| Inventory, Conteo, Reconciliación, Movimientos, Historia y pendientes | [Inventory](inventory/README.md) · [frontend](inventory/frontend.md) |
| Composición, Confirmation, Content e instruction | [Confirmation](order-operations/confirmation.md) |
| Preparation, buckets, Start/Ready, autorización y locks | [Preparation](order-operations/preparation.md) · [frontend](order-operations/preparation-frontend.md) |
| Delivery, direct/prepared y Delivery Correction | [Delivery](order-operations/delivery.md) · [frontend](order-operations/delivery-frontend.md) |
| Functional Amount, Liquidation, Freeze y Closure | [Terminación](order-operations/ending.md) |
| Contratos, Historia y replay de OrderOperations | [Contratos e Historia](order-operations/contracts-and-history.md) |
| Migraciones y límites abiertos de OrderOperations | [Migraciones](order-operations/migrations.md) · [pendientes](order-operations/pending.md) |
| Base Q/R/F implementada en S7-I2 | [Cantidad confirmada y obligación vigente](order-operations/confirmation.md#q-r-y-f-s7-i2) |
| App y coordinación web; composición/consulta/terminación | [Frontend transversal](frontend/README.md) · [workflow](order-operations/frontend.md) |
| Verificación, capas de pruebas y harness | [Testing](testing/verification.md) |

Las rutas de instrucciones locales empiezan en [backend](../backend/AGENTS.md), [frontend](../frontend/AGENTS.md), [tests backend](../backend/tests/AGENTS.md), [scripts](../scripts/AGENTS.md) y [docs](AGENTS.md).

## Estado actual de Slice

**Slice 8 — SSE / Multi-user Freshness: CLOSED.** Preparation por destino, Order activo e Inventory operacional son las superficies MVP de frescura implementadas y verificadas verticalmente. La decisión, evidencia y límites —incluida la única instancia backend activa— pertenecen a [SSE y frescura](architecture/sse-and-freshness.md). Los feeds de otros módulos y mecanismos futuros multi-instancia permanecen diferidos; este cierre no inicia ni diseña el siguiente Slice.

**Siguiente frontera: Slice 9 — Secure Configuration Foundations.** Su objetivo inicial no es completar toda la administración: establece las bases seguras y correctas por capacidad del comportamiento existente de Catalog y OperationalConfiguration.

**S9-I0 — Initial Installation Provisioning: CLOSED.** El fact durable, backfill conservador, subcomando Host sin superficie HTTP, atomicidad, serialización y retry seguro están materializados. El detalle operativo vive en [Administración de Identity](identities-and-capabilities/administration.md#provisioning-inicial-técnico-ad-sec-06).

**S9-I1A — API existente de OperationalConfiguration: CLOSED.** El GET administrativo y el create de Preparation Responsibilities exigen Session utilizable, Identity activa y `GeneralConfiguration`; el create además preserva antiforgery, actor durable e idempotencia/replay exacto.

**S9-I2 — General Configuration Administration Vertical: CLOSED.** La UI conecta las operaciones administrativas de Identity con sesiones y endpoints asegurados reales, preserva la vía administrativa final y cubre el reemplazo de credencial propia. El detalle de contrato está en [Administración de Identity](identities-and-capabilities/administration.md#vertical-web-s9-i2--general-configuration) y la evidencia en [Testing](testing/verification.md).

La secuencia restante es:

1. **S9-I1B** — asegurar y separar reads/writes de Catalog.
2. **S9-I1C** — adaptar frontend a reads autenticados y conscientes de capacidad para configuración y Catalog operacional.
3. **S9-I1D** — checkpoint de seguridad cross-capability.

Permanecen como trabajo posterior y separado: override de Product no disponible RF-PED-024/RF-PED-025, Delete Identity, recovery ordinario y extraordinario (`AD-SEC-01`), UI de lifecycle/administración de Preparation Responsibility más allá de los contratos existentes, Contexts, administración arbitraria de Sessions, UI de bootstrap, lifecycle restante de Catalog y los demás trabajos de Slice 9, incluido SSE de Catalog/OperationalConfiguration. Slice 9 permanece OPEN; no están implementados por esta planificación.

Solo para trazabilidad: [checkpoints históricos de slices](history/slice-checkpoints.md) y [auditoría de esta reorganización](context-audit.md). No son fuentes de nuevas decisiones ni lecturas de arranque obligatorias.
