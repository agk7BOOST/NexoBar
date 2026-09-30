# Iteración A de Product Polish — implementación y verificación

Fecha: 30 de septiembre de 2026. Base: `53d0046639988aba1d0f89ee06a980a4b26376e7`. El usuario autorizó posteriormente el commit de la implementación.

Este informe registra cambios candidatos y evidencia técnica. No añade decisiones de dominio ni sustituye las Sources externas del Project. La autorización es el superprompt actualizado de Iteración A indicado por el usuario; la [segunda auditoría](product-polish-audit-2026-09-30.md) es su referencia. El lifecycle de Contextos/Destinos aprobado después de esa auditoría pertenece al baseline.

## 1. Archivos

Se modificaron 65 archivos versionados del frontend y se añadieron cuatro archivos del frontend y este informe. No se modificaron backend, clientes HTTP, contratos, dependencias ni configuración del harness.

### Implementación

- [frontend/src/App.tsx](../../frontend/src/App.tsx)
- [frontend/src/availability/ProductAvailabilityInterventionPanel.tsx](../../frontend/src/availability/ProductAvailabilityInterventionPanel.tsx)
- [frontend/src/catalog/CatalogPanel.tsx](../../frontend/src/catalog/CatalogPanel.tsx)
- [frontend/src/delivery/DeliveryPanel.tsx](../../frontend/src/delivery/DeliveryPanel.tsx)
- [frontend/src/generalConfiguration/ConfigurationLifecycleControls.tsx](../../frontend/src/generalConfiguration/ConfigurationLifecycleControls.tsx)
- [frontend/src/generalConfiguration/ContextConfigurationSection.tsx](../../frontend/src/generalConfiguration/ContextConfigurationSection.tsx)
- [frontend/src/generalConfiguration/GeneralConfigurationPanel.tsx](../../frontend/src/generalConfiguration/GeneralConfigurationPanel.tsx)
- [frontend/src/identity/LoginPanel.tsx](../../frontend/src/identity/LoginPanel.tsx)
- [frontend/src/identity/SessionBar.tsx](../../frontend/src/identity/SessionBar.tsx)
- [frontend/src/inventory/InventoryConfigurationItemControls.tsx](../../frontend/src/inventory/InventoryConfigurationItemControls.tsx)
- [frontend/src/inventory/InventoryHistory.tsx](../../frontend/src/inventory/InventoryHistory.tsx)
- [frontend/src/inventory/InventoryItemOperations.tsx](../../frontend/src/inventory/InventoryItemOperations.tsx)
- [frontend/src/inventory/InventoryPanel.tsx](../../frontend/src/inventory/InventoryPanel.tsx)
- [frontend/src/orderOperations/AppliedPriceCorrection.tsx](../../frontend/src/orderOperations/AppliedPriceCorrection.tsx)
- [frontend/src/orderOperations/CompleteCancellation.tsx](../../frontend/src/orderOperations/CompleteCancellation.tsx)
- [frontend/src/orderOperations/CompositionLineEditor.tsx](../../frontend/src/orderOperations/CompositionLineEditor.tsx)
- [frontend/src/orderOperations/OperationalInterventionPanel.tsx](../../frontend/src/orderOperations/OperationalInterventionPanel.tsx)
- [frontend/src/orderOperations/OrderEnding.tsx](../../frontend/src/orderOperations/OrderEnding.tsx)
- [frontend/src/orderOperations/OrderLookup.tsx](../../frontend/src/orderOperations/OrderLookup.tsx)
- [frontend/src/orderOperations/OrderWorkflow.tsx](../../frontend/src/orderOperations/OrderWorkflow.tsx)
- [frontend/src/orderOperations/TerminalOrderHistoryView.tsx](../../frontend/src/orderOperations/TerminalOrderHistoryView.tsx)
- [frontend/src/preparation/PreparationPanel.tsx](../../frontend/src/preparation/PreparationPanel.tsx)
- [frontend/src/styles.css](../../frontend/src/styles.css)
- [frontend/src/ui/CopyReference.tsx](../../frontend/src/ui/CopyReference.tsx)
- [frontend/src/ui/SensitiveActionDialog.tsx](../../frontend/src/ui/SensitiveActionDialog.tsx)

### Pruebas unitarias y soporte

- [frontend/src/App.test.tsx](../../frontend/src/App.test.tsx)
- [frontend/src/catalog/CatalogPanel.test.tsx](../../frontend/src/catalog/CatalogPanel.test.tsx)
- [frontend/src/delivery/ContentCancellation.test.tsx](../../frontend/src/delivery/ContentCancellation.test.tsx)
- [frontend/src/delivery/ContentCorrection.test.tsx](../../frontend/src/delivery/ContentCorrection.test.tsx)
- [frontend/src/delivery/DeliveryCorrection.test.tsx](../../frontend/src/delivery/DeliveryCorrection.test.tsx)
- [frontend/src/generalConfiguration/ConfigurationLifecycleControls.test.tsx](../../frontend/src/generalConfiguration/ConfigurationLifecycleControls.test.tsx)
- [frontend/src/generalConfiguration/ContextConfigurationSection.test.tsx](../../frontend/src/generalConfiguration/ContextConfigurationSection.test.tsx)
- [frontend/src/generalConfiguration/GeneralConfigurationPanel.test.tsx](../../frontend/src/generalConfiguration/GeneralConfigurationPanel.test.tsx)
- [frontend/src/identity/LoginPanel.test.tsx](../../frontend/src/identity/LoginPanel.test.tsx)
- [frontend/src/inventory/InventoryHistory.test.tsx](../../frontend/src/inventory/InventoryHistory.test.tsx)
- [frontend/src/inventory/InventoryPanel.test.tsx](../../frontend/src/inventory/InventoryPanel.test.tsx)
- [frontend/src/orderOperations/AppliedPriceCorrection.test.tsx](../../frontend/src/orderOperations/AppliedPriceCorrection.test.tsx)
- [frontend/src/orderOperations/CompleteCancellation.test.tsx](../../frontend/src/orderOperations/CompleteCancellation.test.tsx)
- [frontend/src/orderOperations/CompleteCancellationCoordination.test.tsx](../../frontend/src/orderOperations/CompleteCancellationCoordination.test.tsx)
- [frontend/src/orderOperations/OperationalInterventionPanel.test.tsx](../../frontend/src/orderOperations/OperationalInterventionPanel.test.tsx)
- [frontend/src/orderOperations/OrderEnding.test.tsx](../../frontend/src/orderOperations/OrderEnding.test.tsx)
- [frontend/src/orderOperations/OrderLookup.test.tsx](../../frontend/src/orderOperations/OrderLookup.test.tsx)
- [frontend/src/orderOperations/OrderWorkflow.test.tsx](../../frontend/src/orderOperations/OrderWorkflow.test.tsx)
- [frontend/src/orderOperations/TerminalOrderHistoryView.test.tsx](../../frontend/src/orderOperations/TerminalOrderHistoryView.test.tsx)
- [frontend/src/preparation/PreparationCorrection.test.tsx](../../frontend/src/preparation/PreparationCorrection.test.tsx)
- [frontend/src/test/setup.ts](../../frontend/src/test/setup.ts)
- [frontend/src/ui/CopyReference.test.tsx](../../frontend/src/ui/CopyReference.test.tsx)

### Escenarios de navegador

- [frontend/e2e/active-order-sse.spec.ts](../../frontend/e2e/active-order-sse.spec.ts)
- [frontend/e2e/app-shell.spec.ts](../../frontend/e2e/app-shell.spec.ts)
- [frontend/e2e/applied-price-correction.spec.ts](../../frontend/e2e/applied-price-correction.spec.ts)
- [frontend/e2e/catalog-cross-capability.spec.ts](../../frontend/e2e/catalog-cross-capability.spec.ts)
- [frontend/e2e/catalog-structure-lifecycle.spec.ts](../../frontend/e2e/catalog-structure-lifecycle.spec.ts)
- [frontend/e2e/complete-cancellation.spec.ts](../../frontend/e2e/complete-cancellation.spec.ts)
- [frontend/e2e/configuration-lifecycle.spec.ts](../../frontend/e2e/configuration-lifecycle.spec.ts)
- [frontend/e2e/context-configuration-change.spec.ts](../../frontend/e2e/context-configuration-change.spec.ts)
- [frontend/e2e/general-configuration.spec.ts](../../frontend/e2e/general-configuration.spec.ts)
- [frontend/e2e/helpers/select-initial-context.ts](../../frontend/e2e/helpers/select-initial-context.ts)
- [frontend/e2e/identity-delete.spec.ts](../../frontend/e2e/identity-delete.spec.ts)
- [frontend/e2e/inventory-lifecycle-unit-delete.spec.ts](../../frontend/e2e/inventory-lifecycle-unit-delete.spec.ts)
- [frontend/e2e/inventory-movement-correction.spec.ts](../../frontend/e2e/inventory-movement-correction.spec.ts)
- [frontend/e2e/inventory-operation-sse.spec.ts](../../frontend/e2e/inventory-operation-sse.spec.ts)
- [frontend/e2e/operational-intervention.spec.ts](../../frontend/e2e/operational-intervention.spec.ts)
- [frontend/e2e/preparation-configuration-e2e.spec.ts](../../frontend/e2e/preparation-configuration-e2e.spec.ts)
- [frontend/e2e/preparation-sse.spec.ts](../../frontend/e2e/preparation-sse.spec.ts)
- [frontend/e2e/product-availability-intervention.spec.ts](../../frontend/e2e/product-availability-intervention.spec.ts)
- [frontend/e2e/product-delete.spec.ts](../../frontend/e2e/product-delete.spec.ts)
- [frontend/e2e/terminal-order-history.spec.ts](../../frontend/e2e/terminal-order-history.spec.ts)
- [frontend/e2e/vertical-slice.spec.ts](../../frontend/e2e/vertical-slice.spec.ts)
- [frontend/e2e/product-polish.spec.ts](../../frontend/e2e/product-polish.spec.ts)

## 2. POLISH implementados

| Hallazgo   | Resultado de Iteración A                                                                                                             |
| ---------- | ------------------------------------------------------------------------------------------------------------------------------------ |
| POLISH-001 | La ayuda del nombre confirma su procedencia histórica. Snapshot y fallback permanecen.                                               |
| POLISH-002 | Lecturas de productos/contextos distinguen loading, error, vacío confirmado y falta de autorización; retry sólo GET con fencing.     |
| POLISH-003 | Fallo de comprobación de sesión separado de 401; cambio de acceso propio explicado en el login.                                      |
| POLISH-004 | Usuario/Contraseña, validación de vacío, mostrar/ocultar y autocomplete administrativo adecuado.                                     |
| POLISH-005 | Recuperación en lenguaje de tarea; abandonar retry local explica que no deshace la operación, sólo donde ya existía esa posibilidad. |
| POLISH-006 | Cinco eliminaciones definitivas comparten diálogo nativo con objeto, consecuencia y foco seguro.                                     |
| POLISH-007 | Foco al abrir/cerrar editores locales; selección única de nombre/precio; Escape seguro y retorno tras eliminación.                   |
| POLISH-008 | Feedback local nombra acción/objeto y cantidades confirmadas.                                                                        |
| POLISH-009 | Corrección de Inventario separa sending, rechazo conocido, uncertainty y éxito; bloquea doble envío.                                 |
| POLISH-010 | Comando confirmado separado del refresh fallido; recuperación de lectura sin repetir el comando.                                     |
| POLISH-011 | Títulos orientados a preparar/agregar productos; vocabulario de acceso, cantidades, finalización y unidad.                           |
| POLISH-012 | UUID y referencias auxiliares bajo Detalles; referencia exacta operativa permanece visible.                                          |
| POLISH-013 | Dimensiones auxiliares de Liquidación/Cierre y cantidades Q/R/C de intervención bajo Detalles.                                       |
| POLISH-014 | Variante destructiva; retiro/desactivación inline con consecuencias explícitas.                                                      |
| POLISH-015 | Nombres accesibles empiezan por el texto visible y añaden el objeto.                                                                 |
| POLISH-016 | Loading/success cortés, errores accionables alert, incertidumbre persistente; ayuda/error del login asociados a campos.              |
| POLISH-017 | Encabezados accesibles en tabla responsive y asociación headers/celdas.                                                              |
| POLISH-018 | Bordes de campos usan el token existente de texto secundario.                                                                        |
| POLISH-019 | Reutilización pequeña de diálogo y copia; se conservan clases locales de feedback.                                                   |
| POLISH-021 | Guía para cuentas sin tareas autorizadas y vacío confirmado de contextos con creación existente.                                     |
| POLISH-022 | Corrección editorial de conteo físico/voseo y formato de fechas de composición pendiente.                                            |

POLISH-020 queda fuera: las extracciones amplias de componentes pertenecen a otra iteración y el superprompt las excluye expresamente.

## 3. Correcciones de comportamiento

- Una falla de GET no se presenta como ausencia de objetos. Reintentar consulta conserva la composición y repite sólo la lectura pertinente.
- InventoryHistory muestra Guardando corrección durante el request y conserva editor/intención ante un refresh o rechazo funcional. Sólo una respuesta realmente incierta habilita el retry exacto; mantiene root, body, revisión, key y token originales.
- Contextos y Destinos distinguen creación/lifecycle confirmado de lista desactualizada. Catálogo, disponibilidad y correcciones de Inventario también conservan el resultado confirmado ante fallo de actualización.
- El error de /current conserva una pantalla de comprobación fallida; sólo 401 confirma falta de sesión.

## 4. Microcopy

Usuario de acceso, Contraseña, Configurar/Cambiar acceso, Destinos habilitados, Habilitar destino, Quitar habilitación, Preparar pedido, Agregar productos al pedido, Productos por confirmar, Importe de lo entregado, Medio de pago, Finalización, Ver historial y Tipo de movimiento corregido reemplazan expresiones técnicas según su contexto.

Se conservan Responsabilidades funcionales, Incorporación histórica (con ayuda Productos confirmados juntos), Liquidación, Cierre, Conteo, Reconciliación, Entrada, Salida manual, Merma y las diferencias entre retirar, desactivar, corregir, cancelar y eliminar. No se renombran campos ni códigos del dominio/API.

## 5. Sesión y login

Enter envía el formulario; vacío local evita POST; 401 no identifica cuál campo es incorrecto. Mostrar/ocultar no cambia el valor ni persiste la contraseña y conserva current-password. Nuevas contraseñas administrativas usan new-password. Cerrar sesión conserva la operación real. El acceso propio actualizado vuelve al login con motivo no sensible y no depende de un panel desmontado.

## 6. Quality of Life

Copiar referencia usa el valor completo del pedido creado/consultado, historial y target transferible de intervención. Copiado es un status local; si Clipboard falla, la referencia completa sigue visible y seleccionable. No hay abreviación, temporizador de descarte ni copia automática de otros UUID.

## 7. Diálogos y confirmaciones

SensitiveActionDialog usa dialog/showModal para Producto, Identity, Elemento de Inventario, Contexto y Destino. Volver recibe foco inicial. Escape cierra únicamente antes del envío; los botones se bloquean al enviar. La intención permanece en el padre, incluida la incertidumbre posterior.

Desactivar Identity, retirar Elemento de Inventario, Contexto y Destino explican consecuencias inline. No se modalizaron operaciones cotidianas ni se añadió Deshacer.

## 8. Foco y accesibilidad

Edición local de Catálogo, nombre/acceso de Identity, lifecycle e Inventario y correcciones de Entrega recibe foco útil. Volver/Escape restauran el origen; el objeto eliminado devuelve foco a encabezado lógico. SSE/refresh silencioso no activan solicitudes de foco. No se seleccionan contraseñas.

El navegador comprueba teclado, Escape, foco seguro/retorno y diálogo nativo. La tabla conserva diseño responsive con headers explícitos y encabezados disponibles al árbol accesible. Se ajustó únicamente el borde de campos usando la paleta existente. No se afirma certificación WCAG.

## 9. Decisiones no implementadas

Sin Iteración B, refactor amplio de CatalogPanel/GeneralConfigurationPanel/App, CompositionEditor/DeliveryContentCard, hooks de coordinación nuevos, router, store, librería UI/modal/motion, comandos globales, Storybook, toast global, búsqueda, nuevos endpoints ni animaciones.

No se extrajo ActionFeedback: los mensajes conservan manejo contextual distinto de errores/incertidumbre; una extracción adicional no simplificaba esta iteración. Se compartieron sólo presentación del diálogo y copia.

## 10. Pruebas y resultados

| Verificación                            | Resultado final                                     |
| --------------------------------------- | --------------------------------------------------- |
| Typecheck (tsc -b)                      | PASS                                                |
| ESLint                                  | PASS, sin errores ni warnings                       |
| Prettier en archivos modificados/nuevos | PASS                                                |
| git diff --check                        | PASS                                                |
| Vitest completo                         | 44 archivos, 655 pruebas, todas PASS                |
| Chromium completo                       | 53 escenarios, todos PASS; 12 nuevos de Iteración A |

El harness aplicó migraciones existentes y levantó backend/Vite contra PostgreSQL efímero aislado. Finalizó eliminando ese PostgreSQL y su volumen. No se ejecutó una suite backend independiente ni se utilizó una base productiva.

Se añadieron pruebas de fallos de lectura, confirmación seguida de refresh fallido, guardado en curso/rechazo conocido de Inventario, login y copia exacta. Los E2E nuevos cubren sesión, Contextos/Destinos, diálogos/foco, retry exacto de lifecycle e Inventario y Clipboard real. Los E2E existentes mantienen assertions sobre cantidades, referencias completas, importes, autoridad combinada, Historia, SSE y Liquidación/Cierre.

## 11. Fallos preexistentes

Se corrigieron los tres señalados por la auditoría: selector específico del resumen Pedido cerrado y apertura real de details antes de verificar previews de Preparation. No se abrió el contenido por defecto ni se debilitó la comprobación funcional.

La corrida conjunta detectó expectativas de texto antiguas en E2E y dependencia entre fixtures compartidos al cambiar credenciales/responsabilidades. Se actualizó el recorrido y se preservó la restauración de credencial/autoridad después de verificar revocación. Un refresh de Entrega falló intermitentemente en una corrida tras un POST 200 (importe autoritativo ya corregido); no se cambió la cantidad esperada ni la semántica del comando. La corrida completa final pasó también ese escenario sin alterar sus assertions; permanece como intermitencia observada, no como un fallo abierto reproducible.

## 12. Soluciones conservadoras

- Backend conserva elegibilidad y autoridad; no se calculan dependencias ni se resuelven conflictos automáticamente.
- No se resuelven nombres históricos de actores mediante administración viva. UUID quedan disponibles en Detalles.
- El snapshot de nombre del producto y los precios confirmado/efectivo permanecen separados.
- Listo, Entregado, Liquidado, congelación y Cerrado no se fusionan. Importe de lo entregado describe el cálculo existente, sin alterar cantidades exactas.
- Conteo no establece existencia sin Reconciliación; null, cero y negativos siguen distintos. La unidad observada es read-only y se envía como expected state sin alterarla.
- No se creó una vía para descartar incertidumbre donde no existía. Escape nunca cancela negocio ni borra intentos enviados.
- Los workspaces ocultos permanecen montados. No se modificaron FreshnessReadCoordinator, suscripciones, contratos ni requests de los clientes.

## 13. Lifecycle de Contextos/Destinos

Se pulieron nombres accesibles por fila, editor Rename y selección inicial, consecuencia de Retire, feedback con objeto, Delete nativo, retorno de foco y errores humanos por dependencias.

Los clientes de lifecycle y backend no cambiaron: mismos endpoints, métodos, cuerpos, expected name/state, target y key. Chromium verifica exclusión operativa de Contextos/Destinos retirados, reactivación y vuelta a selección, dependencia de productos activos rechazada sin cascadas, Rename, Delete elegible, POST confirmado + GET fallido (retry sólo GET) e incertidumbre de Retire (mismo URL/body/key/target al repetir). Administración conserva los retirados; no se reconfiguran productos ni habilitaciones automáticamente. La continuidad del Trabajo ya originado se conserva por los mismos contratos/reglas del baseline; no se añadió una operación para trasladarlo.

Los cambios son candidatos a revisión del usuario. .agents/, skills-lock.json y la auditoría previamente existente se preservaron fuera del commit autorizado. No se realizó push, migración ni despliegue.
