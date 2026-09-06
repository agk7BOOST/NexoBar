# OrderOperations

Preparation y Delivery son fronteras internas de este módulo. El código está en una carpeta común; selecciona lecturas por responsabilidad del archivo o cambio, sin cargar todos los temas.

| Trabajo afectado | Lectura requerida |
| --- | --- |
| First/Subsequent Confirmation, PendingComposition, Content, instruction | [Confirmation](../../../docs/order-operations/confirmation.md), [colaboración Catalog](../../../docs/catalog/confirmation-collaboration.md) y [Freeze](../../../docs/order-operations/ending.md) |
| `Preparation*`, Start/Ready, lectura o autorización de Work | [Preparation](../../../docs/order-operations/preparation.md), [contratos e Historia](../../../docs/order-operations/contracts-and-history.md), [Freeze](../../../docs/order-operations/ending.md) y [fronteras abiertas](../../../docs/order-operations/pending.md) |
| `Delivery*`, `OrderDelivery*`, entrega o su Correction | [Delivery](../../../docs/order-operations/delivery.md), [contratos e Historia](../../../docs/order-operations/contracts-and-history.md), [Freeze](../../../docs/order-operations/ending.md) y [fronteras abiertas](../../../docs/order-operations/pending.md) |
| Liquidation, Closure, estado económico o bloqueo del Order | [Terminación](../../../docs/order-operations/ending.md), [Confirmation](../../../docs/order-operations/confirmation.md) y [fronteras abiertas](../../../docs/order-operations/pending.md) |
| Contratos, comandos, replay, lookup o Historia | [Contratos e Historia](../../../docs/order-operations/contracts-and-history.md) y el tema correspondiente |
| Mapping o migraciones | [Migraciones](../../../docs/order-operations/migrations.md) y los temas de las entidades afectadas |

Antes de trabajar sobre cantidades, creación de Content, cumplimiento o locks, lee el apartado [Q/R/F implementado en S7-I2](../../../docs/order-operations/confirmation.md#q-r-y-f-s7-i2). `OrderModel.cs`, `OrderOperationsDbContext.cs` y `OrderOperationsModule.cs` reúnen varios temas: aplica las filas pertinentes, incluidas las invariantes de Preparation si afectan Work.

Al cambiar la estabilización de sesión/capacidades, añade [seguridad de Identity](../../../docs/identities-and-capabilities/security.md). Las rutas frontend están en [frontend/AGENTS.md](../../../frontend/AGENTS.md).
