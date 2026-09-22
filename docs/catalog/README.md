# Catalog

## MVP-FC-CAT — Catalog Structure and Product Lifecycle: CLOSED

El bloque de estructura de Catalog y lifecycle de Product esta cerrado. La evidencia de implementacion es MVP-FC-CAT-I1 (backend), MVP-FC-CAT-I2 (frontend) y MVP-FC-CAT-I3 (E2E real sobre PostgreSQL).

### Groups

Catalog es dueno de `Group`. Un Group tiene identidad estable y nombre operacional, y soporta create/list. Un Product puede pertenecer a cero o un Group; Catalog permite asignar, cambiar y desasignar esa relacion. Un Group sin Products es valido.

Group es funcionalmente neutral: no cambia precio, disponibilidad, configuracion de Preparation, comportamiento de Order ni requiere reescribir snapshots historicos de Order.

Quedan fuera de este bloque: rename de Group, retire/reactivate de Group, delete de Group, jerarquia, ordenamiento, metadata visual y semantica de categoria/regla de negocio.

### Product rename

`CatalogConfiguration` puede renombrar Products activos y retirados mediante la intencion de cambio de nombre. La identidad del Product continua siendo la misma. La unicidad del nombre operacional se aplica entre Products activos; un nombre de Product retirado no reserva esa unicidad activa, aunque la reactivacion puede fallar si existe un Product activo con ese nombre.

Rename cambia la presentacion vigente de Catalog. No reescribe nombres ni contenido historico de Orders.

### Product lifecycle

Retire establece `IsActive=false`. El Product permanece persistido y sigue expuesto en el read administrativo. El browse operacional lo excluye y una nueva Confirmation lo rechaza como Product no actual (`product_not_current`). Precio, Group, configuracion de Preparation y disponibilidad almacenada permanecen preservados. Content confirmado y PreparationWork existentes no se alteran.

Reactivate establece `IsActive=true` y `IsAvailable=true` por regla de lifecycle. Preserva Group, precio, configuracion de Preparation y nombre actual. Una colision con el nombre de un Product activo impide la reactivacion.

`Retired` no significa `Deleted`: Product Delete no pertenece a este bloque.

### Active versus Available

`IsActive` representa lifecycle/currentness del Product. `IsAvailable` representa disponibilidad operacional temporal. `CatalogConfiguration` es dueno del lifecycle, pero la mutacion general de Availability no forma parte de este bloque y no existe un control general de Availability en CatalogConfiguration.

La disponibilidad `true` producida por Reactivate es una consecuencia especifica del comando de lifecycle; no otorga a CatalogConfiguration autoridad general sobre Availability. Product Availability Intervention queda documentado como bloque separado y cerrado bajo autoridad de `OperationalIntervention` en [MVP-FC-AVAIL](#mvp-fc-avail--product-availability-intervention-closed).

Los reads administrativos de CatalogConfiguration incluyen Products activos y retirados. Los reads operacionales de Product continuan excluyendo retirados; `OperationalIntervention` no obtiene por ello visibilidad de Products retirados.

## MVP-FC-AVAIL — Product Availability Intervention: CLOSED

Product Availability Intervention queda cerrado con la evidencia MVP-FC-AVAIL-I1 (backend), MVP-FC-AVAIL-I2 (frontend) y MVP-FC-AVAIL-I3 (E2E dirigido).

### Propiedad, autoridad y estado

Catalog es dueño del Estado de Product, incluidos `IsActive` e `IsAvailable`. `OperationalIntervention` posee la autorización operacional para mutar temporalmente `IsAvailable`; esta autorización no transfiere la propiedad del Estado a OrderOperations ni convierte a `CatalogConfiguration` en autoridad general de Availability. `OrderOperationsAndBasicClosure` no es requisito para cambiar Availability.

La separación es:

- `IsActive`: lifecycle/currentness del Product;
- `IsAvailable`: disponibilidad operacional temporal.

Un Product retirado permanece inutilizable aunque conserve `IsAvailable=true`. Reactivate estableciendo `IsAvailable=true` sigue siendo comportamiento propio del comando de lifecycle del bloque MVP-FC-CAT. Availability Intervention no retira, reactiva, renombra, cambia Group, cambia precio ni configura Preparation.

### Read estrecho de intervención

`GET /api/catalog/availability-administration-products` es el read estrecho que permite a `OperationalIntervention` ejercer esta capacidad. Devuelve únicamente Products activos/currentes con:

```text
id
operationalName
isAvailable
```

No expone Products retirados, precio, Group, configuración de Preparation ni controles de lifecycle de Catalog. Es independiente de `GET /api/catalog/operational-products`, que continúa siendo el read de composición de OrderOperations.

### Comando de Availability

La mutación explícita es:

```text
POST /api/catalog/products/{productId}/availability-changes
```

con la intención:

```json
{
  "expectedCurrentAvailability": true,
  "newAvailability": false
}
```

El comando sólo modifica `IsAvailable` y sólo admite Products activos. Un Product inexistente produce `catalog.product.not_found`; uno retirado produce `catalog.product.not_current`; una expectativa stale produce `catalog.product.availability_concurrency_conflict` con el valor vigente. Una observación exacta cuyo nuevo valor coincide con el actual es un no-op durable exitoso. No hay last-write-wins.

### Idempotencia y seguridad

La intención nueva exige Session utilizable, Identity activa, antiforgery, `Idempotency-Key` UUID v4 y `OperationalIntervention`. La idempotencia es durable y actor-aware, siguiendo las convenciones locales de Catalog.

Un replay exacto conserva el mismo actor, Product e intención completa (`expectedCurrentAvailability` y `newAvailability`). Se resuelve antes de reautorizar la responsabilidad vigente; por eso el replay puede seguir siendo válido después de revocar `OperationalIntervention`, mientras Session e Identity continúen satisfechas. Otro actor o una intención cambiada producen conflicto. Esta semántica no se generaliza fuera de los comandos Catalog establecidos.

### Efectos sobre Orders y separación de S10

Un Product disponible participa de la operación ordinaria. Cuando pasa a no disponible, desaparece del browse ordinario y una Confirmation ordinaria rechaza con `product_unavailable` sin comprometer Content. Una Identity con `OrderOperationsAndBasicClosure + OperationalIntervention` puede usar además la intención explícita de excepción de Product no disponible ya definida por S10. La mutación de Availability por sí sola nunca otorga autoridad de Order.

Availability Intervention cambia el Estado actual de Catalog. La excepción S10 es una intención excepcional para una incorporación concreta de un Order. Cambiar Availability después no reescribe `UnavailableProductExceptionApplied` en Content ya confirmado.

Availability changes no mutan Content confirmado, Incorporations, `appliedPrice`, PreparationWork, el destino de Preparation capturado, Delivery ni Order History. Volver a poner un Product disponible tampoco reinterpreta History.

La concurrencia probada conserva dos órdenes relevantes: si Confirmation toma primero el snapshot de Product, Availability espera, Confirmation puede committear y el cambio de Availability queda efectivo después; si Availability=false committea primero, una Confirmation ordinaria posterior observa el Product no disponible y rechaza sin Content.

### Persistencia y evidencia de migración

El schema de Product no cambió: `IsAvailable` ya existía. La nueva migración agrega únicamente la persistencia durable de comandos de Availability y actualiza el snapshot de EF. La verificación de migración Up y Down pasó, preservó Product/`IsAvailable`, y `HasPendingModelChanges=false` para los cinco DbContext.

### Superficie frontend y combinaciones

La UI dedicada de Availability Intervention se monta sólo con `OperationalIntervention`. Lista Products activos, muestra explícitamente `Disponible`/`No disponible`, ofrece `Marcar no disponible` y `Marcar disponible`, refresca ante estado stale y permite reintentar explícitamente la intención durable original ante resultado incierto. No pertenece a `CatalogPanel`.

Las combinaciones efectivas son:

- `OperationalIntervention` solamente: read estrecho y cambio de Availability; sin administración de Catalog lifecycle/configuración;
- `OrderOperationsAndBasicClosure` solamente: browse ordinario, sin mutación de Availability y con Products no disponibles ocultos;
- ambas: Products no disponibles visibles en Order workflow, mutación de Availability disponible y excepción S10 disponible;
- `CatalogConfiguration` solamente: administración de Catalog, sin mutación temporal de Availability.

### Price correction on retired Products

Un Product retirado puede recibir una correccion de precio configurado porque un Order ya confirmado y aun no liquidado puede requerir el flujo explicito de Applied Price Correction:

```text
precio configurado en Catalog -> Applied Price Correction explicita
```

Cambiar el precio de un Product retirado no lo reactiva, no cambia `IsAvailable`, no lo vuelve seleccionable operacionalmente y no muta automaticamente precios ya aplicados en Orders. Applied Price Correction resuelve el precio configurado por identidad estable de Product; Confirmation continua usando su snapshot separado de Product actual/activo. Estos conceptos no se fusionan.

### Commands, actor and durable idempotency

CreateGroup, ChangeProductGroup, RenameProduct, RetireProduct y ReactivateProduct son comandos durables actor-aware de Catalog. Cada uno conserva replay exacto de la misma key e intencion, y conflicto ante intencion cambiada. La evidencia de acceptance cubre para las cinco familias: replay exacto despues de revocacion de `CatalogConfiguration` para el mismo actor activo, aislamiento frente a otro actor y conflicto de intencion cambiada. Esto no introduce un rediseno generico de seguridad/idempotencia.

### Persistence boundary

La persistencia de Groups agrega el schema de Groups, la relacion nullable Product -> Group y sus restricciones de identidad/nombre. Los comandos tienen persistencia durable dedicada; el lifecycle reutiliza `IsActive` existente y conserva la unicidad de nombre entre Products activos.

La prueba de frontera real de migracion PostgreSQL comprobo Up/Down, preservacion de Products legacy, Group null para Products existentes, schema/FK/uniqueness de Groups, estructuras de comandos, unicidad de nombre activo y Down con el estado legacy preservado.

### Catalog UI

La UI de Catalog ofrece listado/creacion de Groups; asignacion, cambio y desasignacion de Group mientras el Product esta activo; rename mientras esta activo o retirado; retire/reactivate; estados de lifecycle y Availability separados; y correccion del precio configurado mientras el Product esta retirado.

La creacion de Product no cambio: no selecciona Group inicial ni lifecycle, y comienza activo, sin Group y con la configuracion existente de creacion. No hay controles generales de Availability en CatalogConfiguration.

Producto no significa Elemento de Inventario. Para cambiar la capacidad consumida por Confirmation, leer [colaboración transaccional](confirmation-collaboration.md).

## Reads y fronteras de S9

Catalog distingue administración de exploración operacional. La administración de Products requiere `CatalogConfiguration`. La exploración para Composición/Confirmation requiere `OrderOperationsAndBasicClosure`; no implica `CatalogConfiguration` ni existe una responsabilidad `CatalogRead`.

La proyección operacional contiene sólo Product id, nombre operacional, precio actual, disponibilidad y vigencia actual. `requiresPreparation` puede incluirse sólo cuando el frontend realmente lo necesite. No expone internals de Preparation Responsibility, datos de comandos administrativos, Historia ni lifecycle futuro.

La visibilidad y la incorporación excepcional de un Product temporalmente no disponible exigen conjuntamente `OrderOperationsAndBasicClosure` y `OperationalIntervention`. RF-PED-024/RF-PED-025 están implementados: la solicitud es explícita por línea, no cambia la disponibilidad general del Product y no exige motivo de texto libre. La Confirmation conserva la validación de Product vigente/activo antes de aplicar la excepción; un Product inexistente, inactivo o no vigente continúa usando el comportamiento existente de Product no actual.

Los reads internos usan colaboraciones explícitas: OrderOperations consume la capacidad de Catalog para necesidades de Product/Confirmation y Catalog consume el lookup estrecho de OperationalConfiguration al configurar Preparation Responsibility. Ningún módulo accede directamente al `DbContext`, schema o tablas de otro; el Host coordina autenticación, no posee storage de negocio.

## Catalog y configuración de preparación

El Estado vigente de `Product` incluye `requiresPreparation` y `preparationResponsibilityId` nullable, con la invariante física y de aplicación:

```text
RequiresPreparation = false ↔ PreparationResponsibilityId = null
RequiresPreparation = true  ↔ PreparationResponsibilityId = UUID
```

`PreparationResponsibilityId` es una referencia externa y lógica; no existe FK física desde `Catalog` hacia `OperationalConfiguration`.

La UI de Catalog para Products existentes ofrece un editor dedicado de configuración de preparación: habilita Preparation seleccionando exactamente una Preparation Responsibility, reasigna el destino y deshabilita Preparation. La creación de Product continúa iniciando con `requiresPreparation=false`; la configuración ocurre inmediatamente después mediante ese editor, no como parte de Create Product.

La mutación materializada es el comando explícito:

```text
POST /api/catalog/products/{productId}/preparation-configuration-changes
```

Su semántica es:

- `null → UUID`: habilitar preparación;
- `UUID A → UUID B`: reasignar prospectivamente;
- `UUID → null`: deshabilitar prospectivamente;
- `null → null`: no-op válido cuando el valor esperado coincide.

El comando recibe `expectedCurrentPreparationResponsibilityId` y realiza un `UPDATE` condicionado atómico con comparación nullable; no aplica last-write-wins. Mantiene idempotencia durable local y resuelve el replay antes de consultar `OperationalConfiguration`. Para una intención nueva cuyo destino no es `null`, `Catalog` usa la capacidad pública estrecha de existencia de `PreparationResponsibility`.

Price Change continúa siendo otro comando explícito de `Catalog`, no un `PATCH` genérico: recibe `expectedCurrentPrice`, ejecuta un `UPDATE` condicionado, responde `409 Conflict` ante una expectativa desactualizada, mantiene idempotencia durable local y no crea Price History ni reescribe `appliedPrice` históricos.

Los endpoints materializados de Catalog ya no son anónimos ni comparten una frontera única. La administración de Catalog requiere `CatalogConfiguration`. La exploración operacional de Products requiere `OrderOperationsAndBasicClosure`; los Products no disponibles sólo se incluyen cuando también está presente `OperationalIntervention`. La decisión de aplicar la excepción pertenece a OrderOperations durante Confirmation: Catalog sigue siendo autoritativo para `IsAvailable`, precio vigente y configuración de preparación, y no muta su Product como consecuencia de una incorporación excepcional.

El lookup de Preparation Responsibility para configurar un Product es un endpoint estrecho propiedad de Catalog y exige `CatalogConfiguration`; Catalog resuelve la referencia mediante su colaboración interna explícita con `OperationalConfiguration`. No se convierte el listado administrativo de `OperationalConfiguration` en una capacidad de Catalog ni se permite acceso directo entre storages.

`CatalogConfiguration` es dueño de la configuración de Preparation del Product. Catalog lee los destinos seleccionables mediante ese lookup estrecho de su propiedad; no llama directamente a APIs administrativas de `OperationalConfiguration`. Configurar un Product no requiere `Preparation` ni una Preparation Enablement.
