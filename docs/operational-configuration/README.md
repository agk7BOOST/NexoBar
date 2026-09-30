# OperationalConfiguration

## OperationalConfiguration

`OperationalConfiguration` posee el Estado y storage de `PreparationResponsibility` y de Context configurado: su `OperationalConfigurationDbContext`, el schema `operational_configuration` y migration history propia sobre la PostgreSQL primaria compartida.

El Estado materializado de `PreparationResponsibility` contiene:

- `Id` UUID v7;
- `OperationalName`;
- `NormalizedOperationalName`;
- `IsActive` (activo o retirado).

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

El lifecycle mínimo permite cambiar nombre, retirar, reactivar y eliminar definitivamente cuando sea elegible. Ordenamiento y gestión de estaciones permanecen diferidos.

## Context configurado — MVP-FC-CTX: CLOSED

`OperationalConfiguration` posee el Estado de Context configurado. Su identidad técnica es un UUID estable; `operationalName` es la identidad que usan los operadores. La UI ordinaria no presenta el UUID crudo. La administración incluye listar, crear y el lifecycle mínimo descrito abajo:

```text
GET  /api/operational-configuration/contexts
POST /api/operational-configuration/contexts
```

Ambas operaciones administrativas requieren Session utilizable, Identity activa y `GeneralConfiguration`; la creación también requiere antiforgery e `Idempotency-Key` UUID v4. El frontend recarga desde la autoridad y conserva para retry explícito una creación de resultado incierto. Rename, Retire, Reactivate y Delete son comandos administrativos separados; el cliente no calcula elegibilidad. Context no representa capacidad, ocupación, piso ni número de mesa; múltiples Orders activos pueden compartirlo. `OperationalReference` distingue los Orders.

OrderOperations resuelve Context mediante su capacidad estrecha de configuración, sin acceso runtime al `DbContext`, schema o tablas de OperationalConfiguration; no hay FK cross-module. El Order conserva su propio Context actual, por lo que sus reads operacionales no vuelven a consultar OperationalConfiguration. La colaboración de First Confirmation y Context Change, junto con el import/backfill de upgrade, está descrita en [OrderOperations: Context actual, cambio e Historia](../order-operations/contracts-and-history.md#context-actual-cambio-e-historia-mvp-fc-ctx).

El import de Contextos legados depende del orden del runner productivo, donde OperationalConfiguration se migra antes que OrderOperations. El Host ordinario sigue sin auto-migrate y las migraciones son una etapa explícita de deployment; véase [Tooling y Development](../architecture/development.md#tooling-development-y-migraciones).

## Lifecycle mínimo de Contextos y Destinos de preparación

`GeneralConfiguration` es la única autoridad administrativa. Ambas entidades conservan identidad y unicidad case-insensitive entre todos los registros existentes, incluidos retirados. El read administrativo devuelve `{ id, operationalName, isActive }` con activos y retirados. El lookup operacional de Contextos conserva exclusivamente `{ id, operationalName }` y filtra activos; Catalog obtiene sólo destinos activos mediante `ListActiveAsync`.

Para cada colección `/api/operational-configuration/contexts` y `/api/operational-configuration/preparation-responsibilities` se añaden:

```text
POST   /{id}/operational-name-changes
POST   /{id}/retire
POST   /{id}/reactivate
DELETE /{id}
```

Todos reciben `expectedCurrentOperationalName` y `expectedIsActive`; Rename añade `newOperationalName`, recortado y validado en backend. El resultado durable es `{ id, operationalName, isActive, isDeleted }`. La fila se bloquea `FOR UPDATE` y una observación obsoleta obtiene `409 concurrency_conflict`, sin sobrescritura silenciosa. Retire/Reactivate sobre un estado ya equivalente obtienen `409 already_retired`/`already_active`; el replay exacto sigue devolviendo el resultado original.

Cada intención nueva exige Session utilizable, Identity activa, `GeneralConfiguration`, antiforgery y `Idempotency-Key` UUID v4. Los registros locales `context_lifecycle_commands` y `preparation_responsibility_lifecycle_commands` guardan actor, kind, target, observación, intención y resultado. Comparten el advisory lock por key con la creación de su entidad; reutilizar la key entre creación y lifecycle o con actor/kind/intención diferentes produce `409 idempotency_key_conflict`. Un replay exacto con Session e Identity utilizables se resuelve antes de reautorizar la capacidad y no reaplica efectos.

### Contextos

Rename y Retire son prospectivos: no reescriben Orders, Context actual adquirido, Confirmation History ni Context Change History. Retire está permitido con Orders abiertos y únicamente impide nuevas First Confirmations y nuevos Context Changes hacia ese Contexto. Reactivate restituye selección futura con identidad y nombre vigentes.

Delete físico puede ejecutarse activo o retirado, sin retiro previo, sólo si nunca se utilizó en ningún Order. `IContextOperationalParticipation`, implementada por OrderOperations, consulta Confirmation History, ambos extremos de Context Change History y Context actual. Cierre, cancelación completa o un cambio posterior no eliminan la participación. El conflicto estable es `operational_configuration.context.delete.operational_participation`.

First Confirmation y Context Change adoptan la misma transacción PostgreSQL mediante el lookup estrecho y estabilizan la fila Context `FOR SHARE` antes de adquirir significado operacional. Delete/Retire esperan ese lock: si la operación confirma primero, Delete ve la participación comprometida; si la configuración cambia primero, la operación revalida y rechaza el Contexto no seleccionable. No hay FK cross-module ni consultas a storage ajeno.

### Destinos de preparación

Retire rechaza mientras Catalog conserve cualquier Product activo configurado hacia ese destino: `operational_configuration.preparation_responsibility.retire.active_products`. La UI pide cambiar o deshabilitar primero esa preparación. Products retirados pueden conservar la referencia, pero Reactivate Product revalida y bloquea el destino activo antes de activar el Product; nunca lo reconfigura automáticamente.

Destino retirado no es destino inexistente: `ExistsAsync` y el batch `ReadByIdsAsync` siguen resolviéndolo para Habilitaciones y operación existente. No se revocan habilitaciones, mueven Works ni modifican Products. `IsActiveAsync` estabiliza destinos seleccionables `FOR SHARE` dentro de la transacción de configuración/reactivación de Product o Confirmation. First y Subsequent Confirmation rechazan destinos no activos sin crear nuevo Work; Work ya originado conserva destino y progreso operable según sus reglas existentes.

Delete físico, activo o retirado, exige ausencia de toda configuración actual de Product (activo o retirado), de Preparation Enablements actuales y de Work/uso operacional histórico. Las capacidades estrechas `IDestinationProductReferences`, `IDestinationEnablementReferences` e `IDestinationOperationalParticipation` pertenecen a esta frontera y las implementan respectivamente Catalog, IdentitiesAndCapabilities y OrderOperations. Consultan sobre la misma transacción del comando, sin dependencias inversas de proyecto ni FK cross-module. Los conflictos estables tienen prefijo `operational_configuration.preparation_responsibility.delete.` y sufijos `product_references`, `preparation_enablements` u `operational_participation`. No se hacen cascadas para conseguir elegibilidad.

### Migración y UI

`20260930120000_AddConfigurationLifecycle` añade `is_active=true` a todos los registros previos, crea los dos registros durables de lifecycle y elimina sólo las FK desde creation commands hacia State. Los resultados de creación y lifecycle permanecen después del Delete, permitiendo replay exacto. Down rechaza borrar resultados durables, registros retirados o referencias de creación a State ya eliminado; no elimina datos para forzar rollback. El modelo histórico de `AddOperationalContexts` queda congelado en su Designer y no depende del snapshot futuro. No existe auto-migrate en runtime.

Las listas existentes de Configuración general muestran nombre, Activo/Retirado y las cuatro acciones con controles inline. Retire y Delete tienen confirmación y consecuencias explícitas. Después de éxito se relee desde backend; un resultado incierto conserva body, key y token para retry explícito y bloquea otras mutaciones de esa lista. Un fallo de read posterior a un éxito confirmado solicita actualizar la lista, sin reinterpretar el comando como incierto. El lenguaje humano del destino es «Destino de preparación»; `PreparationResponsibility` se conserva en código.

Verificación dirigida de este cambio: [testing](../testing/verification.md#lifecycle-mínimo-de-contextos-y-destinos-de-preparación-2026-09-30).
