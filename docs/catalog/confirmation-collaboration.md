# Colaboración OrderOperations → Catalog

## Colaboración `OrderOperations -> Catalog` y concurrencia

- `IOrderConfirmationCatalog` es la capacidad pública mínima de Confirmación. `Catalog` conserva la propiedad de su Estado; `OrderOperations` no accede a `CatalogDbContext` ni a tablas `catalog.*`.
- Su snapshot de `Product` incluye `ProductId`, `Price`, `IsActive`, `IsAvailable`, `RequiresPreparation` y `PreparationResponsibilityId`.
- Todos esos campos se leen bajo el mismo `FOR SHARE`, dentro de la transacción modular existente. `Catalog` reutiliza la conexión y transacción PostgreSQL de `OrderOperations`.
- Exponer `DbTransaction` en esa interfaz es una excepción técnica deliberada por atomicidad y estabilización; no establece un patrón genérico para todas las colaboraciones entre módulos.

El guardrail de concurrencia materializado es:

```text
Confirmation                         Price Change / Preparation Configuration
Products FOR SHARE                   Product UPDATE incompatible
```

El orden efectivo serializa las operaciones. Una Confirmación que estabilizó primero un `Product` conserva como snapshot aplicado `appliedPrice`, `RequiresPreparation` y `PreparationResponsibilityId`, aunque una mutación concurrente espere y se aplique después. El precio queda en `IncorporationContent`; la decisión de crear Work y su responsabilidad proceden de ese mismo snapshot.

Una Confirmación deduplica los `ProductId`, estabiliza una sola vez cada `Product` distinto mediante `Catalog` y reutiliza ese snapshot para todas sus líneas. Por tanto, todas las líneas del mismo Product dentro de una Confirmación comparten coherentemente `appliedPrice`, `RequiresPreparation` y `PreparationResponsibilityId`, sin alterar el lock ordering ni la atomicidad existentes.

Si el Product estabilizado tiene `RequiresPreparation = false` y una línea contiene `instruction != null`, la Confirmación responde `409 Conflict` con `order_operations.confirmation.instruction_requires_preparation` y no produce efectos. La instrucción no se descarta, no fuerza Preparation y no crea Work por sí sola. Si una Confirmación estabiliza primero un Product preparado y luego espera una configuración concurrente `true → false`, puede completar y su Work conserva el snapshot anterior; si `false` ya estaba aplicado al estabilizar, la Confirmación falla con ese `409`.
