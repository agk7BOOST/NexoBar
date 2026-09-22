# OperationalConfiguration

## OperationalConfiguration

`OperationalConfiguration` posee el Estado y storage de `PreparationResponsibility` y de Context configurado: su `OperationalConfigurationDbContext`, el schema `operational_configuration` y migration history propia sobre la PostgreSQL primaria compartida.

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

## Context configurado — MVP-FC-CTX: CLOSED

`OperationalConfiguration` posee el Estado de Context configurado. Su identidad técnica es un UUID estable; `operationalName` es la identidad que usan los operadores. La UI ordinaria no presenta el UUID crudo. El mínimo MVP de administración es listar y crear:

```text
GET  /api/operational-configuration/contexts
POST /api/operational-configuration/contexts
```

Ambas operaciones administrativas requieren Session utilizable, Identity activa y `GeneralConfiguration`; la creación también requiere antiforgery e `Idempotency-Key` UUID v4. El frontend recarga desde la autoridad y conserva para retry explícito una creación de resultado incierto. No hay rename, retire/reactivate ni delete. Context no representa capacidad, ocupación, piso ni número de mesa; múltiples Orders activos pueden compartirlo. `OperationalReference` distingue los Orders.

OrderOperations resuelve Context mediante su capacidad estrecha de configuración, sin acceso runtime al `DbContext`, schema o tablas de OperationalConfiguration; no hay FK cross-module. El Order conserva su propio Context actual, por lo que sus reads operacionales no vuelven a consultar OperationalConfiguration. La colaboración de First Confirmation y Context Change, junto con el import/backfill de upgrade, está descrita en [OrderOperations: Context actual, cambio e Historia](../order-operations/contracts-and-history.md#context-actual-cambio-e-historia-mvp-fc-ctx).

El import de Contextos legados depende del orden del runner productivo, donde OperationalConfiguration se migra antes que OrderOperations. El Host ordinario sigue sin auto-migrate y las migraciones son una etapa explícita de deployment; véase [Tooling y Development](../architecture/development.md#tooling-development-y-migraciones).
