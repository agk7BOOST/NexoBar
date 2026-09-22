# OperationalConfiguration

## OperationalConfiguration

`OperationalConfiguration` posee el Estado y storage de `PreparationResponsibility`: su `OperationalConfigurationDbContext`, el schema `operational_configuration` y migration history propia sobre la PostgreSQL primaria compartida.

El Estado materializado de `PreparationResponsibility` contiene:

- `Id` UUID v7;
- `OperationalName`;
- `NormalizedOperationalName`.

La API materializada es:

```text
POST /api/operational-configuration/preparation-responsibilities
GET  /api/operational-configuration/preparation-responsibilities
```

### Lectura administrativa

`GET /api/operational-configuration/preparation-responsibilities` es una lectura administrativa. Requiere Session autenticada y utilizable, Identity activa y `GeneralConfiguration` vigente. `CatalogConfiguration` por sí sola no autoriza este endpoint.

### Creación e idempotencia

`POST /api/operational-configuration/preparation-responsibilities` crea una Preparation Responsibility mediante un comando nuevo que requiere Session autenticada y utilizable, Identity activa, `GeneralConfiguration` vigente, el antiforgery convencional e `Idempotency-Key` UUID v4 con idempotencia durable local. El actor durable se deriva en servidor de la Identity autenticada; el cliente no lo atribuye. La identidad UUID v7 de la responsabilidad se asigna en backend y el nombre operacional conserva su unicidad case-insensitive.

Un replay exacto ya comprometido requiere Session utilizable, Identity activa, el mismo actor original, el mismo command kind y la misma intención canonical. No se vuelve a exigir `GeneralConfiguration` sólo para devolver ese resultado persistido; los comandos nuevos sí exigen la responsabilidad vigente. Un registro durable anterior al retrofit sin actor atribuible no es replay autenticado: entra en conflicto seguro y no inventa un actor.

Cuando Catalog configura un Product, obtiene el lookup mínimo `{ id, operationalName }` mediante colaboración in-process explícita `Catalog → OperationalConfiguration`. `GeneralConfiguration` es la autoridad del GET administrativo; `CatalogConfiguration` no recibe ese listado. No se accede al `DbContext`, schema o tablas de `OperationalConfiguration` desde Catalog.

## Vertical web de configuración de Preparation

La superficie **Configuración general** lista Preparation Responsibilities y permite crear una nueva por nombre operacional. Ambas acciones siguen la autoridad administrativa de `GeneralConfiguration`; crear un destino no asigna `Preparation` ni concede una Preparation Enablement a ninguna Identity.

El lifecycle completo de `PreparationResponsibility` permanece diferido: no hay rename, retiro/reactivación, delete, ordering ni gestión de estaciones. No se marca ese lifecycle como completo.
