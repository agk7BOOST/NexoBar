# OperationalConfiguration

## OperationalConfiguration

`OperationalConfiguration` posee `OperationalConfigurationDbContext`, el schema `operational_configuration` y migration history propia sobre la PostgreSQL primaria compartida.

El Estado materializado de `PreparationResponsibility` contiene:

- `Id` UUID v7;
- `OperationalName`;
- `NormalizedOperationalName`.

La API materializada es:

```text
POST /api/operational-configuration/preparation-responsibilities
GET  /api/operational-configuration/preparation-responsibilities
```

La creación es un comando explícito con `Idempotency-Key` UUID v4, idempotencia durable local, identidad UUID v7 asignada por backend, unicidad case-insensitive del nombre operacional y replay desde el resultado persistido.

La lista y la creación son administración de `OperationalConfiguration` y requieren `GeneralConfiguration`. `CatalogConfiguration` no adquiere por ello autoridad para crear o cambiar Preparation Responsibilities.

Cuando Catalog configura un Product, obtiene el lookup mínimo `{ id, operationalName }` mediante colaboración explícita `Catalog → OperationalConfiguration`. No se expone el listado administrativo de este módulo para satisfacer ese consumidor ni se accede a su `DbContext`, schema o tablas desde Catalog.

No están materializados `IsActive`, retiro, reactivación, delete ni un lifecycle completo de `PreparationResponsibility`.

- Todavía no existe `OperationalConfigurationPanel` productivo ni frontend administrativo completo.

- Los endpoints públicos existentes siguen anónimos: asegurar create/list antes de una UI administrativa productiva es trabajo pendiente de S9. Los comandos deben adoptar el actor durable y la igualdad de replay definidos en las [fronteras de seguridad](../architecture/security-boundaries.md).

Autenticación/autorización pendiente de endpoints actuales: [fronteras de seguridad](../architecture/security-boundaries.md).
