# Migraciones de OrderOperations

Este inventario resume migraciones seleccionadas del baseline vigente y no sustituye los archivos versionados.

## Migración de Context configurado — MVP-FC-CTX

La actualización de Context usa `OperationalConfiguration` antes de `OrderOperations`, aplicado por el runner productivo en el orden explícito `OperationalConfiguration → IdentitiesAndCapabilities → Catalog → Inventory → OrderOperations`. El Host ordinario no ejecuta migraciones al arrancar: deployment las aplica como etapa explícita. El propósito y los comandos del runner están en [Tooling y Development](../architecture/development.md#tooling-development-y-migraciones).

`20260922130000_AddOperationalContexts` crea `operational_configuration.contexts` y, sólo durante upgrade cuando ya existen las tablas históricas correspondientes, importa nombres usados por Orders, Confirmation History y First Confirmation commands. Recorta whitespace exterior; nombres equivalentes por normalización case-insensitive se agrupan determinísticamente, eligiendo la forma display con prioridad Order, Confirmation History y luego comando, con desempate ordinal. La migración asigna identidad configurada común por nombre equivalente.

`20260922140000_AddConfiguredOrderContexts` backfillea `orders.current_context_id`, `confirmation_history.confirmed_context_id` y `first_confirmation_commands.intent_context_id` desde `operational_configuration.contexts`, exige que ninguna fila quede sin mapear y deja los IDs requeridos. Los snapshots textuales legados (`orders.context`, `confirmed_context`, `intent_context`) no se reescriben. Por eso varios Orders legados bajo un Contexto equivalente comparten el mismo ID configurado sin alterar su texto histórico.

Los comandos nuevos de First Confirmation envían `contextId`; una First Confirmation nueva por texto libre se rechaza. Para un comando legacy ya comprometido, su texto y el ID importado se preservan para que el matching exacto de la intención histórica permita replay idempotente. Este camino es compatibilidad interna limitada al replay confirmado, no un fallback textual para nuevas operaciones.

`20260922150000_AddOrderContextChanges` agrega Historia semántica y resultados durables de Context Change, con secuencia única por Order. No crea FK hacia Contextos de OperationalConfiguration; cada módulo conserva propiedad de su Estado.

Las migraciones relevantes de S3-I3 son:

- `20260830210000_ReidentifyIncorporationContent`;
- `20260830230000_AddConfirmationInstructions`.

Ambas tienen Designer completo, snapshots coherentes y `HasPendingModelChanges = false`. La segunda agrega `instruction` a Contents y a los contenidos durables de comandos, y elimina las uniques temporales por Product. Su `Down` protege los datos y falla explícitamente si ya existen duplicados incompatibles con el schema anterior; nunca fusiona ni elimina líneas silenciosamente.

Preparation Start agregó la migración `20260831063910_AddPreparationStart`, que materializa la Historia y los comandos durables de Preparation. Ready reutiliza ese modelo y no agregó una migración adicional.

Las migraciones vigentes de Slice 4 son:

- `AddDeliveryState`, que crea el Estado 1:1 y hace backfill `DeliveredQuantity = 0` para Contents previos;
- `CapturePreparationRequirementAtConfirmation`, que agrega el snapshot histórico: hace backfill `true` cuando existía el Work exacto y `false` cuando no existía, y luego deja la columna `NOT NULL` sin default persistente;
- `AddDeliveryProgress`, que materializa `delivery_history` y `delivery_commands`.

Slice 6 agregó `20260904152524_AddAuthoritativePendingComposition` (marcador, comandos y columnas legacy nullable de actor), `20260905055531_AddLiquidation` (State, History y comandos) y `20260905101316_AddClosure` (State, History y comandos). El handoff anterior indicaba que no se modificaron migraciones durante aquella consolidación documental.

Delivery Correction agregó `20260906000853_AddDeliveryCorrection`, que materializa su History y comandos durables sin modificar `DeliveryState`, `PreparationWork`, `IncorporationContent` ni la Historia original de Delivery.

## Base Q/R/F S7-I2

`20260906120000_AddContentQuantityState` está implementada. Crea `content_quantity_states`, PK/FK 1:1 al Content y restricción `removed_by_correction_quantity >= 0`; hace backfill en cero para Contents existentes. El Down rechaza eliminar la tabla si existe R distinto de cero. Ese backfill de migración no autoriza un fallback runtime ante State faltante.

Evidencia: [migración](../../backend/src/NexoBar.OrderOperations/Migrations/20260906120000_AddContentQuantityState.cs), [pruebas de migración](../../backend/tests/NexoBar.OrderOperations.IntegrationTests/ContentQuantityStateMigrationTests.cs) y [pruebas de dominio/modelo](../../backend/tests/NexoBar.OrderOperations.IntegrationTests/ContentQuantityStateDomainTests.cs). La semántica vigente de [Q/R/C/F](confirmation.md#q-r-c-y-f-s7-i2-content-correction-ordinaria-s7-i3d-content-cancellation-ordinaria-s7-i4d) vive en Confirmation; este apartado conserva únicamente la migración base de S7-I2.

## Applied Price State S7-I8D

La migración de Applied Price State crea el `ContentAppliedPriceState` obligatorio por `(IncorporationId, ContentOrdinal)` y los registros de History/resultados durables de Applied Price Correction. Hace backfill de cada Content confirmado existente con `EffectiveAppliedPrice = IncorporationContent.AppliedPrice`; no inventa History de corrección, actor ni timestamp. Su Down protege State de corrección significativo, Historia y resultados durables: no los elimina ni degrada silenciosamente. Los paths históricos de migración fueron actualizados y verificados.

## Deuda de identificadores

- una auditoría realizada durante I3B detectó seis nombres de identificadores EF preexistentes de más de 63 bytes en `OrderOperations`. Son ajenos a los cambios de Inventory, no se corrigen en esta unidad documental y quedan señalados para una futura revisión de higiene de schema/migraciones.
