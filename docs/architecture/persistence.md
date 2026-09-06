# Persistencia transversal

## Persistencia

- Una instancia/base PostgreSQL compartida es la persistencia relacional transaccional primaria. EF Core 10 y Npgsql son la estrategia predeterminada.
- Cada módulo que persiste Estado posee su propio `DbContext`, schema e historial de migraciones:
  - `CatalogDbContext` mapea `catalog`;
  - `OrderOperationsDbContext` mapea `order_operations`;
  - `InventoryDbContext` mapea `inventory`;
  - `OperationalConfigurationDbContext` mapea `operational_configuration`;
  - `IdentitiesAndCapabilitiesDbContext` mapea `identities_and_capabilities`.
- Las migraciones son explícitas, versionadas y revisables. El Host productivo no ejecuta auto-migrate durante el startup.
- Dinero y las cantidades exactas fraccionarias materializadas usan `numeric`/`decimal`, nunca coma flotante binaria como representación autoritativa; cuando aplica, su representación HTTP es un decimal string estable. Las cantidades actuales de `PreparationWork` son enteros exactos; las cantidades fraccionarias de Preparation permanecen abiertas.
- Las identidades persistentes principales materializadas usan UUID v7. `Idempotency-Key` usa UUID v4.
- Los timestamps operacionales autoritativos los asigna el backend y se persisten en UTC.
- Estado vigente e Historia semántica son conceptos distintos; cuando son consecuencias inseparables de un comando persistente, se confirman atómicamente junto a su resultado durable de [idempotencia](http-and-idempotency.md). La solución no es CQRS ni Event Sourcing.
- SQL explícito puede usarse cuando una invariante, concurrencia o rendimiento lo justifique. No se usan Repository Pattern genérico ni lazy loading por defecto.

### Invariante de configuración

Las connection strings modulares de `Catalog`, `OrderOperations`, `Inventory`, `OperationalConfiguration` e `IdentitiesAndCapabilities` deben apuntar a la misma instancia y base PostgreSQL. Las colaboraciones transaccionales entre módulos dependen de ello. Actualmente es una invariante de configuración documentada, no una validación automatizada.
