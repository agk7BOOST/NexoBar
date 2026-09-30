# Iteración B de Product Polish — fronteras de componentes

Fecha: 30 de septiembre de 2026. El usuario autorizó posteriormente el commit de la implementación. Este informe registra implementación y verificación, sin añadir decisiones de dominio ni sustituir las Sources externas del Project.

## 1. Baseline y árbol inicial

Se utilizó `e9715e16f7adcbb8692bf9a06564ab6c8afe9104`, `feat(frontend): implement product polish iteration A`. No se volvió al baseline anterior `53d0046639988aba1d0f89ee06a980a4b26376e7`.

El árbol inicial no tenía cambios en archivos versionados. Tenía tres entradas sin seguimiento: `.agents/`, `skills-lock.json` y [la segunda auditoría](product-polish-audit-2026-09-30.md). Se preservaron sin modificaciones y quedan fuera de esta implementación.

Se consultaron las instrucciones aplicables, el [informe de Iteración A](product-polish-iteration-a-2026-09-30.md), la auditoría, el frontend transversal, la documentación de las superficies afectadas y las guías del harness. El superprompt actualizado de B prevalece sobre las propuestas más amplias de la auditoría. Se aplicó `vercel-react-best-practices` para definir componentes estables en el ámbito del módulo; no se añadieron dependencias ni optimizaciones sin evidencia.

## 2. Candidatos evaluados

| Padre                     | Evaluación sobre el estado posterior a A                                                                                                                                                 |
| ------------------------- | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| GeneralConfigurationPanel | El detalle de la identidad seleccionada es una tarea reconocible, separable del listado, alta y coordinación administrativa.                                                             |
| DeliveryPanel             | La identidad del contenido, cantidades y estado forman una tarjeta coherente. Los formularios comparten mapas de intenciones, inputs y foco con la coordinación del panel.               |
| CatalogPanel              | Los editores corresponden a comandos independientes y pueden coexistir. El formulario dedicado de preparación es una subzona cohesiva.                                                   |
| OrderWorkflow             | Ya usa CompositionLineEditor. Una frontera mayor mezclaría selección, lectura de productos, Contexto, confirmación y composición pendiente, o añadiría un envoltorio con poco beneficio. |
| PreparationPanel          | renderAction ya comparte la presentación de acciones conservando sus diferencias. No se encontró otra frontera repetida que justificara extraer filas por rutina.                        |
| App                       | El estado expresa coordinación de sesión, capacidades, navegación, bloqueos y refrescos. Mover su JSX no simplifica esas responsabilidades.                                              |

## 3. Extracciones realizadas

- **IdentityAdministrationDetail:** encabezado, estado/acceso, responsabilidades, destinos habilitados y acciones de una identidad. Recibe el snapshot existente, la referencia de foco, bloqueos independientes y callbacks explícitos. No recibe setters ni intenciones inciertas. Permite modificar la presentación administrativa sin recorrer la creación de identidades, Contextos y Destinos.
- **DeliveryContentCard:** artículo semántico con nombre, incorporación, instrucción, cantidades autoritativas y presentación de Entregado. Tiene cinco props, incluido `children`, por donde se componen los controles y mensajes existentes. Esto separa la presentación del contenido sin trasladar reglas, cálculos o coordinación de comandos.
- **CatalogPreparationEditor:** formulario controlado específico de preparación, con siete props. Conserva el tipo de draft existente, los destinos del lookup y callbacks de edición, submit y cancelación. La edición de esta configuración puede revisarse sin recorrer los demás comandos de Catálogo.

Los tres componentes se declaran fuera de sus padres y no agregan wrappers al DOM.

## 4. Extracciones deliberadamente pospuestas

No se extrajo un editor universal de producto: precio, nombre, grupo, lifecycle y preparación tienen estados e intenciones independientes. Se limitó el candidato CatalogProductEditor a CatalogPreparationEditor.

No se trasladaron los formularios de corrección/cancelación de Entrega a la tarjeta. Su interfaz completa habría arrastrado numerosos inputs, estados de apertura, previews, callbacks e intenciones. La tarjeta recibe esos controles por composición; el padre conserva su orden y ownership.

Los editores de nombre/acceso, la confirmación de desactivación y el diálogo definitivo de Identity siguen en el padre. Se relacionan con notices, reconciliación de listado, sesión propia e incertidumbre; incluirlos en el detalle ampliaba innecesariamente la frontera.

CompositionEditor, PreparationPanel y App permanecen sin cambios por las razones de la evaluación. No se creó ActionFeedback: los mensajes administrativos y por contenido conservan distintos tipos, fases, persistencia y reintentos. Una presentación común aportaba poco sin ocultar esas diferencias. SensitiveActionDialog y CopyReference permanecen intactos.

## 5. Archivos

| Acción     | Archivo                                                                                                      |
| ---------- | ------------------------------------------------------------------------------------------------------------ |
| Modificado | [GeneralConfigurationPanel.tsx](../../frontend/src/generalConfiguration/GeneralConfigurationPanel.tsx)       |
| Creado     | [IdentityAdministrationDetail.tsx](../../frontend/src/generalConfiguration/IdentityAdministrationDetail.tsx) |
| Creado     | [responsibilityLabels.ts](../../frontend/src/generalConfiguration/responsibilityLabels.ts)                   |
| Modificado | [DeliveryPanel.tsx](../../frontend/src/delivery/DeliveryPanel.tsx)                                           |
| Creado     | [DeliveryContentCard.tsx](../../frontend/src/delivery/DeliveryContentCard.tsx)                               |
| Modificado | [CatalogPanel.tsx](../../frontend/src/catalog/CatalogPanel.tsx)                                              |
| Creado     | [CatalogPreparationEditor.tsx](../../frontend/src/catalog/CatalogPreparationEditor.tsx)                      |
| Creado     | Este informe                                                                                                 |

No se modificaron pruebas, clientes HTTP, DTOs, backend, dependencias, configuración, CSS ni documentación normativa.

## 6. Responsabilidades de los padres

GeneralConfigurationPanel sigue coordinando lista/selección, creación, destinos, sesión actual, comandos administrativos, reconciliación, feedback, editores y diálogos. Delega la presentación del detalle y conecta sus callbacks con los comandos existentes.

DeliveryPanel sigue coordinando reads de Delivery/Order/Preparation, SSE, fencing, cantidades elegibles, validación, inputs, apertura de editores, foco, mensajes e intenciones por `(incorporationId, contentOrdinal)`. Delega el artículo y su resumen; los controles siguen declarados en el padre.

CatalogPanel sigue coordinando catálogo, grupos, lookup de destinos, drafts, snapshot observado, comandos, actualización posterior, incertidumbre y foco. Delega únicamente el formulario de preparación.

## 7. Ownership de estado

No se movió ningún estado React, efecto ni ref de coordinación a los hijos. Los tres componentes nuevos son controlados o de presentación y no tienen hooks, reads autónomos ni comandos.

Los drafts de Identity y Catálogo permanecen junto a sus intenciones y resultados. Los mapas de inputs y apertura de Entrega permanecen junto a su limpieza posterior al comando, fencing y bloqueo por contenido. No hay sincronización adicional, snapshots duplicados ni otra fuente de verdad.

La referencia del detalle de Identity sigue apuntando a su misma sección semántica. Catálogo conserva `catalog-preparation-editor`, sus campos y el efecto de foco del padre. Entrega conserva la key por target y los controles de foco/Escape en su owner original.

## 8. Duplicación

No se encontraron patrones completos de feedback con lifecycle idéntico que justificaran otra abstracción compartida. No se afirma una reducción artificial de duplicación: la mejora principal son las fronteras de presentación.

El mapa ya existente de etiquetas de responsabilidades se trasladó a responsibilityLabels.ts para que listado, feedback y detalle usen una sola definición. Se conservaron exactamente sus valores; no se copió el mapa en el hijo.

## 9. Preservación funcional y visual

Se conservaron copy, orden de controles, clases, headings, sections, listas, artículo, tabla responsive, details/dialog, roles, aria, labels, IDs, tabIndex y referencias de foco. No hay diferencias visuales intencionales ni animaciones.

No cambiaron endpoints, métodos, cuerpos, expected state, claves de idempotencia, tokens, DTOs ni clientes. Los padres conservan el orden de operaciones, retry exacto, incertidumbre, respuesta confirmada seguida de refresh fallido, autoridad backend y gating existente. No cambió el montaje de workspaces bajo `hidden`, SSE ni generaciones/fencing.

Listo/Entregado/Liquidado/Cerrado, Retirar/Desactivar/Eliminar y Conteo/Reconciliación siguen separados. El lifecycle de Contextos/Destinos no se modificó.

## 10. Verificación ejecutada

| Verificación                                                                                           | Resultado                                              |
| ------------------------------------------------------------------------------------------------------ | ------------------------------------------------------ |
| Configuración: GeneralConfigurationPanel, ContextConfigurationSection y ConfigurationLifecycleControls | 3 archivos, 52 pruebas PASS                            |
| Entrega: suite src/delivery                                                                            | 5 archivos, 117 pruebas PASS                           |
| Catálogo: suite src/catalog                                                                            | 2 archivos, 41 pruebas PASS                            |
| Typecheck después de cada extracción y al cierre: `tsc -b`                                             | PASS                                                   |
| ESLint completo                                                                                        | PASS, sin errores ni warnings                          |
| Prettier en los siete archivos de código y este informe                                                | PASS                                                   |
| `git diff --check`                                                                                     | PASS                                                   |
| Vitest completo                                                                                        | 44 archivos, 655 pruebas PASS; 0 fallos                |
| Chromium completo                                                                                      | 53 escenarios PASS; 0 fallos, sin retries configurados |

No se añadieron pruebas duplicadas: las nuevas fronteras no incorporan comportamiento propio y la cobertura existente se mantuvo íntegra. No se borraron assertions ni se cambiaron selectores.

El primer arranque del harness E2E quedó impedido por el acceso del sandbox a Docker; no llegó a ejecutar escenarios. La ejecución posterior con acceso a Docker completó el harness existente: migraciones compiladas sobre PostgreSQL efímero, backend/Vite, Chromium y eliminación del contenedor y red aislados. No se usó una base productiva ni se ejecutó una suite backend independiente.

## 11. Revisión final del diff

Se revisaron el diff completo de los tres padres y el contenido de los cuatro archivos nuevos. El diff se limita a imports, traslado del mapa/tipo existentes, JSX extraído y adaptadores de callbacks. Los handlers de comandos no se modificaron.

Una comparación estructural con TypeScript contra HEAD comprobó que todos los statements previos al return final y los helpers de los tres padres permanecen idénticos: incluye estados, efectos, reads, generación de keys, retry y helpers de foco. También confirmó la conservación de textos JSX y literales de runtime entre cada padre y sus archivos extraídos.

Al expandir las tres nuevas fronteras y sustituir sus props, la estructura semántica del JSX, condiciones y atributos no asociados a eventos coincide con el baseline. Los adaptadores de eventos se revisaron por separado: mantienen targets, guardas, actualizaciones y orden. Esta comprobación complementa la revisión del código y los recorridos reales; no reemplaza las pruebas con snapshots.

No se detectaron cambios funcionales accidentales ni modificaciones ajenas a B. Los archivos locales iniciales se preservaron.

## 12. Deuda conservada y límites

Los padres siguen siendo grandes: conservan familias independientes de comandos, incertidumbre y coordinación autoritativa. Entrega mantiene sus formularios; Catálogo mantiene los demás editores; Configuración mantiene sus editores y diálogos. Las búsquedas de campos de Catálogo mediante IDs continúan como acoplamiento existente, sin reescribir foco por conveniencia del refactor.

No se resolvieron persistencia de intenciones entre recargas, coordinación transversal de App, feedback universal, nuevos hallazgos de UX ni otros pendientes funcionales. No se detectó un bug funcional nuevo durante B. Sin bloqueos ni decisiones normativas nuevas. El commit fue autorizado por el usuario después de completar la implementación y su verificación. No se inició otra iteración ni se realizó push o despliegue.
