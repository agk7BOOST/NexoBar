# Inventory frontend

`InventoryPanel` mantiene dos superficies independientes: **Configuración de Inventario** y **Estado actual de Inventario**. Cada una carga su propio read model y presenta su `403`; el cliente no oculta que las responsabilidades son independientes.

## Configuración de Inventario

Una Identity con `InventoryConfiguration` puede crear y listar Elements, corregir Unit si no hay Movement History, retirar/reactivar Elements y eliminar definitivamente aquellos con `deleteEligible = true`. La superficie muestra lifecycle, readiness operacional y Unit. Retire y Delete son acciones distintas; Delete exige confirmación explícita.

Si la Unit no es corregible porque existe cualquier Movement, la UI conserva la Unit y muestra la guía para retirar el Element anterior, crear otro con la Unit corregida y establecer su existencia por Count/Reconciliation. Reactivate puede pedir un nombre operacional de reemplazo cuando el anterior está ocupado por otro Element activo; no existe una acción general de rename para Inventory.

Después de una mutación local o un conflicto conocido, Configuración vuelve a leer el endpoint autoritativo. No existe `inventory.configuration` SSE: esta superficie no promete push freshness frente a cambios de otros operadores.

## Estado actual de Inventario

Una Identity con `InventoryOperation` consulta la superficie operacional y puede realizar Count, Reconciliation, Entry, ManualExit, Waste y consultar Movement History. Esa responsabilidad no concede acceso a Configuración; `InventoryConfiguration` tampoco concede autoridad operacional. Una Identity puede tener ambas asignaciones, pero ninguna implica la otra.

- Un Element activo y listo muestra la cantidad actual autoritativa y su Unit; habilita Entry, ManualExit y Waste.
- Un Element activo que espera Reconciliation permanece visible con su Unit. Muestra “Existencia física no establecida” y cantidad actual no establecida, no cero. Count/Reconciliation están disponibles; Entry, ManualExit y Waste no.
- Un Element retirado no aparece en Operación. Al reactivarlo vuelve activo sin existencia establecida y requiere un Count físico nuevo antes de los Movements ordinarios.
- Un Element eliminado no aparece en ninguno de los dos reads.

Cada Count registra una observación sin cambiar el saldo. El usuario ejecuta Reconciliation explícitamente. Los saldos negativos siguen visibles como inconsistencia operacional; no se recortan ni se rechazan.

## History, intenciones y frescura

**Movimientos** presenta History paginada, separa Reconciliation de Entry, ManualExit y Waste, e incluye actor, timestamp, efecto y saldos. El establecimiento inicial se presenta sin diferencia ficticia. La Unit del Element no puede cambiar después de su primer Movement; por eso la History mantiene un único significado de Unit sin snapshots históricos ni migración de Movements.

Cada intención nueva congela tipo, Element, body e `Idempotency-Key`. Ante network, timeout o `5xx` incierto, la UI conserva esos datos, bloquea otra acción sobre el Element y ofrece reintentar exactamente la misma operación. Los intents inciertos viven en memoria y una recarga puede perder la key; no hay cola offline ni retry automático en background.

Mientras la superficie operacional autorizada está montada, `InventoryPanel` consume `inventory.operation`. Apertura, reconexión, invalidación y mutaciones que alteran la lista o sus datos visibles llevan a un GET autoritativo cercado por `FreshnessReadCoordinator`. Esto incluye cambios de lifecycle y Unit, Delete, Reconciliation y Movements ordinarios. SSE es una invalidación no autoritativa: no transporta ni muta cantidades o revisiones. No se introdujo un scope SSE de Configuración, History ni Elements individuales.
