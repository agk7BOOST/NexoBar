# NexoBar — índice técnico

El baseline técnico versionado se distribuye en los documentos siguientes. La [autoridad y el protocolo de trabajo](../AGENTS.md) siguen subordinados a las Sources normativas externas del Project y a decisiones posteriores aprobadas. Este índice no añade decisiones.

Consulta solo los temas afectados y sus dependencias explícitas. Los pendientes locales forman parte del contexto requerido cuando se extiende un comportamiento.

| Área o tarea | Documento propietario |
| --- | --- |
| Plataforma, módulos, dependencias y límites del producto | [Arquitectura](architecture/overview.md) |
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

Solo para trazabilidad: [checkpoints históricos de slices](history/slice-checkpoints.md) y [auditoría de esta reorganización](context-audit.md). No son fuentes de nuevas decisiones ni lecturas de arranque obligatorias.
