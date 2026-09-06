# Pruebas backend

Lee [testing y verificación](../../docs/testing/verification.md). Las suites de integración usan PostgreSQL real con Testcontainers; las comprobaciones puras de dominio/modelo se identifican por prueba.

Los AGENTS de `backend/src` no son ancestros de estas carpetas. Antes de modificar una prueba, lee el AGENTS del módulo cuyo comportamiento verifica y sigue sus lecturas por tema:

| Suite | Instrucciones de dominio |
| --- | --- |
| NexoBar.Catalog.IntegrationTests | [Catalog](../src/NexoBar.Catalog/AGENTS.md) |
| NexoBar.Inventory.IntegrationTests | [Inventory](../src/NexoBar.Inventory/AGENTS.md) |
| NexoBar.IdentitiesAndCapabilities.IntegrationTests | [Identities](../src/NexoBar.IdentitiesAndCapabilities/AGENTS.md) |
| NexoBar.OperationalConfiguration.IntegrationTests | [OperationalConfiguration](../src/NexoBar.OperationalConfiguration/AGENTS.md) |
| NexoBar.OrderOperations.IntegrationTests | [OrderOperations](../src/NexoBar.OrderOperations/AGENTS.md), incluida la fila Preparation para sus pruebas |
| NexoBar.E2E.DatabaseSetup | [Development](../../docs/architecture/development.md), [persistencia](../../docs/architecture/persistence.md) y módulos afectados por el fixture |

Para fixtures que cruzan módulos, consulta las áreas efectivamente alteradas. `InternalsVisibleTo` es una excepción técnica de testing, no colaboración productiva.
