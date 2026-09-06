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

No están materializados `IsActive`, retiro, reactivación, delete ni un lifecycle completo de `PreparationResponsibility`.

- Todavía no existe `OperationalConfigurationPanel` productivo ni frontend administrativo completo.

Autenticación/autorización pendiente de endpoints actuales: [fronteras de seguridad](../architecture/security-boundaries.md).
