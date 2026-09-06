# Migraciones de OrderOperations

Este inventario incluye las migraciones materializadas hasta S7-I2 y no sustituye los archivos versionados.

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

## Base QRF S7-I2

`20260906120000_AddContentQuantityState` está implementada. Crea `content_quantity_states`, PK/FK 1:1 al Content y restricción `removed_by_correction_quantity >= 0`; hace backfill en cero para Contents existentes. El Down rechaza eliminar la tabla si existe R distinto de cero. Ese backfill de migración no autoriza un fallback runtime ante State faltante.

Evidencia: [migración](../../backend/src/NexoBar.OrderOperations/Migrations/20260906120000_AddContentQuantityState.cs), [pruebas de migración](../../backend/tests/NexoBar.OrderOperations.IntegrationTests/ContentQuantityStateMigrationTests.cs) y [pruebas de dominio/modelo](../../backend/tests/NexoBar.OrderOperations.IntegrationTests/ContentQuantityStateDomainTests.cs). La semántica de [Q/R/F](confirmation.md#q-r-y-f-s7-i2) vive en Confirmation.

## Deuda de identificadores

- una auditoría realizada durante I3B detectó seis nombres de identificadores EF preexistentes de más de 63 bytes en `OrderOperations`. Son ajenos a los cambios de Inventory, no se corrigen en esta unidad documental y quedan señalados para una futura revisión de higiene de schema/migraciones.
