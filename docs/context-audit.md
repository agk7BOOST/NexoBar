# Auditoría de contexto para agentes — 2026-09-06

Informe de la reorganización sobre HEAD `790d2f1`, cuyo árbol estaba limpio antes de la primera pasada. La revisión final parte de esa reorganización sin commit e incorpora la confirmación del usuario de S7-I2 ya implementado y verificado. Es evidencia de auditoría, no una Source normativa ni lectura obligatoria de futuras tareas. Los resultados de conservación de la primera pasada se identifican como tales; la revisión final figura al final.

## Inspección y mapa de autoridad anterior

Se inventariaron los 325 archivos versionados y las rutas locales no generadas, se leyeron completos los dos documentos existentes y se contrastaron solución, proyectos, referencias, capacidades públicas, composition root, superficies frontend, configuración y harness. La inspección del código fue estructural y dirigida a las responsabilidades y desfases relevantes; no es una auditoría funcional de cada método.

Solo existían [AGENTS.md](../AGENTS.md) (100 líneas, 5.683 bytes) y [engineering-handoff.md](engineering-handoff.md) (985 líneas, 87.138 bytes) como documentación/instrucciones del repositorio. No había README raíz, AGENTS anidados, documentación histórica separada, configuración de agentes versionada, reglas, skills locales ni pipeline CI versionado. No existen `Directory.Build.props` ni `Directory.Packages.props`: las propiedades están en cada proyecto.

Las Sources normativas están fuera del repositorio y no se recibieron completas en esta tarea. La jerarquía conservada es: Source aplicable y decisión posterior aprobada en su asunto → baseline técnico versionado y contexto operacional subordinados. Código, tests y checkpoints son evidencia de implementación; no resuelven por sí mismos una decisión normativa ausente.

El contexto anterior llegaba mediante el AGENTS raíz precargado/referido por la sesión, el enlace al handoff y lecturas explícitas del repositorio. No se encontraron AGENTS adicionales en los ancestros inspeccionados ni AGENTS/override de usuario en la ubicación estándar. La configuración personal inspeccionada no mostró ajustes `project_doc_*` ni instrucciones específicas de NexoBar; no se modificó. Las instrucciones, plugins y skills de la sesión son externos a este cambio.

Codex descubre instrucciones desde la raíz hasta el directorio de trabajo al iniciar la sesión; iniciar en la raíz no garantiza precargar todos los AGENTS descendientes. Por eso la raíz ahora exige leer las instrucciones de cada ruta afectada. Los AGENTS locales conducen a documentos temáticos, y las pruebas tienen una ruta explícita al módulo hermano. No se aumentó el límite de contexto ni se añadieron hooks. Esta decisión de organización se contrastó usando OpenAI Docs y la [documentación oficial sobre AGENTS.md](https://learn.chatgpt.com/docs/agent-configuration/agents-md).

## Estructura anterior

```text
AGENTS.md
docs/
  engineering-handoff.md
backend/
  NexoBar.slnx
  src/
    NexoBar.Host/
    NexoBar.Catalog/
    NexoBar.Inventory/
    NexoBar.IdentitiesAndCapabilities/
    NexoBar.OperationalConfiguration/
    NexoBar.OrderOperations/          # incluye Preparation y Delivery
  tests/
    NexoBar.{cinco módulos}.IntegrationTests/
    NexoBar.E2E.DatabaseSetup/
frontend/
  src/
    App.tsx, main.tsx, styles.css
    catalog/, identity/, orderOperations/, preparation/, delivery/, inventory/
    test/
  e2e/
scripts/
  verify.cmd, verify.sh
global.json, .nvmrc, compose.yaml, compose.e2e.yaml
```

Se excluyeron como contexto técnico ordinario los productos generados/ignorados: bin, obj, node_modules, dist y resultados de pruebas. No se borró nada.

## Módulos, dependencias y archivos principales

Los seis proyectos productivos son Host y los cinco módulos establecidos. Hay cinco suites de integración y un proyecto de setup E2E. El grafo de ProjectReference coincide con [arquitectura](architecture/overview.md): Host → cinco módulos; OrderOperations → Catalog + Identities; Catalog → OperationalConfiguration; Inventory → Identities; Identities → OperationalConfiguration. No se detectaron ciclos. Las capacidades públicas observadas son confirmación y nombres de Product, existencia/nombres de destinos, estabilización de sesión y capacidades, y nombres de Identity.

| Área | Archivos versionados anteriores |
| --- | ---: |
| Host | 5 |
| Catalog / su suite | 18 / 6 |
| Inventory / su suite | 23 / 25 |
| IdentitiesAndCapabilities / su suite | 36 / 9 |
| OperationalConfiguration / su suite | 11 / 4 |
| OrderOperations / su suite | 67 / 50 |
| Setup E2E backend | 2 |
| Frontend completo | 58 |

Las entidades, servicios, contratos y mapping están mayormente planos dentro de cada proyecto backend. Frontend ya tiene seis carpetas por superficie. Preparation y Delivery no tienen subcarpetas backend: crear una jerarquía de carpetas productivas para ellas habría ampliado la tarea.

| Archivo principal | Líneas anteriores | Responsabilidad observada |
| --- | ---: | --- |
| docs/engineering-handoff.md | 985 | Baseline, detalles de todos los módulos, frontend, pruebas, deuda e hitos |
| OrderOperationsModule.cs | 1.379 | Registro y numerosos endpoints/handlers |
| OrderOperationsDbContext.cs | 991 | Mapping de entidades y comandos del módulo |
| OrderOperationsApiFixture.cs | 1.475 | Setup, helpers y soporte de integración |
| OrderWorkflow.tsx | 1.241 | Composición, marcadores, confirmación e incertidumbre |
| OrderWorkflow.test.tsx | 1.168 | Pruebas del workflow |
| InventoryModule.cs | 694 | Registro y endpoints de Inventory |
| frontend/e2e/vertical-slice.spec.ts | 776 | Escenarios integrados |
| OrderOperationsDbContextModelSnapshot.cs | 1.413 | Snapshot EF generado |
| AddContentQuantityState.Designer.cs | 1.415 | Metadata EF de la última migración |
| frontend/package-lock.json | 3.919 | Dependencias reproducibles, no guía de agentes |

Esas concentraciones se documentan para localizar lecturas; no se refactorizaron.

## Estructura nueva y lista completa de archivos afectados

`M` = archivo existente modificado; `A` = archivo creado. El árbol siguiente enumera los 46 archivos finales del cambio. El contenido del handoff se trasladó a los documentos indicados. En la revisión final se retiró el archivo no versionado de diagnóstico de cantidades; sus reglas vigentes y deuda se integraron en Confirmation, migraciones y pendientes.

```text
M AGENTS.md
backend/
  A AGENTS.md
  src/
    NexoBar.Host/AGENTS.md (A)
    NexoBar.Catalog/AGENTS.md (A)
    NexoBar.Inventory/AGENTS.md (A)
    NexoBar.IdentitiesAndCapabilities/AGENTS.md (A)
    NexoBar.OperationalConfiguration/AGENTS.md (A)
    NexoBar.OrderOperations/AGENTS.md (A)
  tests/
    A AGENTS.md
frontend/
  A AGENTS.md
  src/
    catalog/AGENTS.md (A)
    identity/AGENTS.md (A)
    orderOperations/AGENTS.md (A)
    preparation/AGENTS.md (A)
    delivery/AGENTS.md (A)
    inventory/AGENTS.md (A)
  e2e/
    A AGENTS.md
scripts/
  A AGENTS.md
docs/
  A AGENTS.md
  M engineering-handoff.md
  A context-audit.md
  architecture/
    A overview.md
    A persistence.md
    A http-and-idempotency.md
    A development.md
    A security-boundaries.md
  catalog/
    A README.md
    A confirmation-collaboration.md
  operational-configuration/
    A README.md
  identities-and-capabilities/
    A security.md
    A administration.md
  inventory/
    A README.md
    A frontend.md
  order-operations/
    A confirmation.md
    A preparation.md
    A delivery.md
    A ending.md
    A contracts-and-history.md
    A migrations.md
    A pending.md
    A frontend.md
    A preparation-frontend.md
    A delivery-frontend.md
  frontend/
    A README.md
  testing/
    A verification.md
  history/
    A slice-checkpoints.md
```

Resultado final frente a HEAD: 2 modificados y 44 creados; 19 AGENTS en total. Los restantes archivos del repositorio conservan su ubicación y contenido.

## Qué quedó global y qué se localizó

La raíz conserva autoridad, tratamiento de decisiones abiertas/contradicciones, carga de contexto por alcance, límites de la tarea, respeto de cambios ajenos, restricciones Git, calidad de las pruebas, verificación y entrega. Son reglas aplicables también a CSS, documentación, scripts o tests.

Plataforma y arquitectura transversal viven en un baseline enlazado, obligatorio cuando la tarea los afecta y desde backend. Backend contiene sus reglas de compilación y rutas a persistencia/HTTP/seguridad. Frontend contiene strict TypeScript, consumo de autoridad y rutas a superficies. Las reglas particulares no se repiten completas en los AGENTS.

| Contenido anterior del handoff | Propietario actual |
| --- | --- |
| §1, §25: autoridad y protocolo | AGENTS raíz; el índice referencia esa autoridad |
| §2: plataforma, módulos y dependencias | architecture/overview.md |
| §3: persistencia | architecture/persistence.md |
| §4: destinos y lifecycle | operational-configuration/README.md |
| §5 y destinos de §11; sesión web de §16 | identities-and-capabilities/security.md |
| §6: administración | identities-and-capabilities/administration.md |
| §7 y §9: Catalog y colaboración | catalog/README.md y confirmation-collaboration.md |
| §8; migraciones/deuda Inventory de §18/§20 | inventory/README.md |
| §10: Confirmation y Content | order-operations/confirmation.md |
| §11 salvo destinos | order-operations/preparation.md |
| §12 salvo terminación | order-operations/delivery.md |
| Terminación de §12 y completitud de §20 | order-operations/ending.md |
| §13; matching local de §14 y contratos locales de §15 | order-operations/contracts-and-history.md |
| Reglas comunes de §14/§15 | architecture/http-and-idempotency.md |
| §16: App y cada superficie | frontend/README.md y documentos frontend del área |
| §17: verificación | testing/verification.md |
| §18, §19: tooling/visibilidad y migraciones | architecture/development.md y documento del módulo |
| §20: deuda/abiertos | development, security-boundaries, pending y frontend del área |
| §21–24: hitos, commits y declaraciones de cierre de slices | history/slice-checkpoints.md, etiquetado histórico |

Las reglas del AGENTS original también tienen destino explícito:

| Grupo original | Destino y conservación |
| --- | --- |
| Sources, precedencia, no invención, límites, Git y entrega | Raíz |
| Monorepo, stack, cinco módulos, ownership, colaboración en proceso, no ciclos ni Shared | Arquitectura; entradas backend/frontend |
| PostgreSQL, DbContext propios, EF/Npgsql, SQL excepcional, no Repository/lazy loading, migraciones explícitas | Persistencia |
| Estado/Historia, atomicidad, no CQRS/Event Sourcing, exactitud numérica | Persistencia; consumo exacto también en frontend |
| Intenciones, HTTPS/JSON, recursos para consultas, Minimal APIs, Problem Details, OpenAPI e idempotencia | HTTP e idempotencia |
| Connected-to-authority y SSE primario sin ser autoridad | Arquitectura y ruta frontend; SSE continúa pendiente |
| Composición/Pedido, primera y posteriores Confirmations, Corrección/Cancelación | Confirmation y fronteras abiertas |
| Pending/InPreparation/Ready, Ready/Delivered | Preparation y Delivery |
| Liquidation/Closure | Terminación |
| Product/InventoryItem y ausencia de movimientos por ventas | Catalog e Inventory |
| Identity/Responsibility | Seguridad de Identity |
| TypeScript estricto; nullable y warnings como errores con excepción localizada | AGENTS frontend/backend |

## Conservación y referencias

La comparación de la primera pasada con el handoff original examinó 588 líneas no vacías de contenido, excluyendo encabezados y delimitadores de bloques. 572 se conservaron literalmente. Las 16 restantes tienen tratamiento explícito:

- líneas 5–7 y 982–985: autoridad y protocolo consolidados en la raíz;
- 699, 932, 948 y 978: referencias a secciones numéricas sustituidas por enlaces temáticos;
- 791: cifras de pruebas preservadas, etiquetadas como resultado consignado anteriormente;
- 844: la afirmación sobre no modificar migraciones queda atribuida a aquella consolidación;
- 854 y 864: rótulos de deuda y fronteras distribuidos por tema;
- 899: SSE pendiente integrado en límites transversales.

Ese resultado corresponde al traslado inicial, previo a la actualización explícita de S7-I2 solicitada en la revisión final. Esta comparación verifica conservación textual; la revisión de la matriz anterior verifica las reglas de la raíz reformuladas. No se eliminó ninguna regla, decisión, invariante ni pendiente contenido en los documentos locales inspeccionados. No se afirma haber auditado Sources externas no suministradas.

No había enlaces Markdown internos a secciones del handoff, pero sí referencias textuales a §§12, 16 y 20, ahora actualizadas. Los enlaces nuevos son relativos y los fragmentos se verifican contra encabezados existentes. Los checkpoints conservan sus commits y alcance pasado; las restricciones todavía relevantes de Inventory también quedaron en su documento vigente.

## Eficiencia de futuras sesiones

| Entrada | Antes | Después |
| --- | ---: | ---: |
| AGENTS raíz | 100 líneas / 5.683 bytes | 26 líneas / 2.809 bytes |
| Handoff | 985 líneas / 87.138 bytes | 32 líneas / 3.376 bytes |

La entrada raíz baja aproximadamente 51% en bytes; el handoff, 96%. Son tamaños de archivos, no una medición de tokens ni una promesa de ahorro idéntico por tarea. El volumen documental total aumenta por rutas, encabezados y evidencia de auditoría, mientras disminuye el contexto que una tarea debe cargar.

- CSS puro en styles.css: raíz + frontend y el componente afectado; no concurrencia backend ni migraciones.
- Catalog: raíz + backend + Catalog y su baseline local; colaboración solo si se afectan snapshots, precios o la capacidad consumida.
- Preparation backend: raíz + backend + OrderOperations selecciona Preparation, contratos/Historia, Freeze y pendientes, con la sección Q/R/F vigente cuando corresponde.
- Preparation frontend: su AGENTS requiere invariantes y flujo local, incluyendo buckets, actores, autorización, retry y Freeze.
- Pruebas de Preparation: tests/AGENTS enlaza al AGENTS de OrderOperations; no depende de que las pruebas hereden instrucciones de un directorio hermano.
- Los checkpoints históricos y este informe nunca son lecturas de arranque obligatorias.

## Hallazgos y deuda deliberadamente no resuelta

1. **Documentación Q/R/F resuelta en la revisión final.** S7-I2 está implementado y verificado, confirmado por el usuario. [La base vigente](order-operations/confirmation.md#q-r-y-f-s7-i2) reemplaza el diagnóstico anterior: se actualizaron creación, validaciones, fórmulas y locks. Continúan sin implementarse Content Correction, cantidades de Cancellation y Estado de precio efectivo; no son un bloqueo de la base Q/R/F.
2. **Repeticiones anteriores.** Estado/Historia, matching/replay, clasificación prepared/direct, incertidumbre frontend y límites de slices aparecen en varios contextos. Se separaron consumidores, contratos y checkpoints y se consolidó autoridad/protocolo; se mantuvieron repeticiones explicativas que preservan alcance local. La transición vive en su tema, los registros/contratos en su documento y los hitos en history. No se intentó reescribir toda la semántica para eliminar cada reiteración.
3. **Seguridad no globalmente cerrada.** Se conservaron endpoints anónimos pendientes de retrofit, bootstrap/recovery, PAR-SEC-02 provisional, brute-force/lockout, Delete Identity, frontend administrativo e Historia administrativa. No se implementó seguridad adicional.
4. **Persistencia/operación.** Continúan igualdad de connection strings sin validación automática, tooling general de migraciones pendiente, validación runtime/generación TypeScript diferida y los seis identificadores EF mayores de 63 bytes que consignaba el handoff. El último dato se conserva como hallazgo previo, no como nueva auditoría de schema.
5. **Fronteras funcionales.** Continúan Corrections/excepciones, Cancellation, lifecycle de Inventory/destinos, cantidades fraccionarias, History UI, SSE y persistencia cross-reload. La instrucción confirmada sigue sin edición; los errores post-Liquidation siguen fuera del flujo ordinario. Liquidation occurredAt existe, pero falta en el read después de reload.
6. **Fuentes externas.** Falta una copia/versionado local de las Sources y Adendas completas. No se inventó esa autoridad ni se convirtió el código o los checkpoints en su reemplazo.
7. **Archivos productivos grandes.** Se registraron sus responsabilidades y rutas de lectura. División de handlers, mapping, workflow o fixtures queda fuera de esta tarea.

## Verificación de la primera pasada

- Compilación de la solución: `dotnet build backend/NexoBar.slnx --no-restore`, correcta, 0 warnings y 0 errores.
- Frontend: `npm run typecheck` y `npm run format:check`, correctos. Prettier incluye los AGENTS locales nuevos.
- Conservación: comparación literal y revisión de las 16 transformaciones, más matriz de reglas raíz.
- Enlaces, fragmentos y bloques Markdown: 47 documentos y 198 enlaces locales verificados, sin referencias rotas ni bloques desbalanceados.
- Dependencias: los seis proyectos coinciden con el grafo documentado. La primera comparación auxiliar falló por precedencia de operadores PowerShell; corregida la expresión del verificador, no hubo discrepancias. No requirió cambios en el repositorio.
- `git diff --check`: correcto. Git solo informa normalización futura LF/CRLF conforme a la configuración existente; no hubo errores de whitespace.
- Alcance: diff restringido a Markdown; código, configuración ejecutable, contratos, base, migraciones, tests y scripts de verificación intactos.
- No se ejecutaron suites de integración ni E2E: el cambio es exclusivamente documental y no afecta el recorrido integrado. Los totales históricos no se presentan como ejecución actual.
- Sin commit, push, staged changes ni operaciones destructivas. Los cambios quedaron en el árbol de trabajo para revisión.

La retirada posterior del diagnóstico no elimina conocimiento: su base implementada está en Confirmation, las protecciones de migración en migraciones y las transiciones futuras en pendientes. El archivo no estaba versionado; su contenido previo permanece en la conversación de auditoría y fue integrado antes de retirarlo.

## Revisión final antes de commit

### Resultado y autoridad S7-I2

Recomendación: **aprobar el commit documental**. La jerarquía se conserva. S7-I2 ya está implementado, verificado y committed; la revisión anterior había dejado su base Q/R/F como un problema de conciliación documental. Se resolvió usando la confirmación explícita del usuario y el código existente, sin alterar software.

El único documento de diagnóstico retirado se integró en tres propietarios existentes: Confirmation conserva condiciones confirmadas inmutables, Q/R/F, creación atómica con R=0 y ausencia de fallback; migraciones conserva PK/FK, backfill y Down protegido; pendientes conserva las futuras transiciones. Preparation, Delivery y terminación aplican F donde corresponde, y el importe continúa DeliveredQuantity × AppliedPrice. No existe comando de Content Correction, cantidad de Cancellation ni Estado de precio efectivo. TotalQuantity > 0 de Preparation permanece sin cambios.

Los resultados de S7-I2 son OrderOperations **362/362**, backend **679/679** y frontend último verificado **235/235**, informados por el usuario. Esta revisión solo ejecutó verificaciones documentales y formato; no volvió a ejecutar esas suites.

### Responsabilidad final de la raíz y seguridad global

La raíz mantiene 26 líneas. Conserva autoridad normativa frente a historia, scope, respeto del trabajo ajeno, prohibición de commit/push/destrucción sin instrucción, verificación proporcional y entrega. Su ruta global obliga a leer, desde cualquier área y según impacto:

| Regla global | Fuente técnica única que la desarrolla |
| --- | --- |
| Backend autoritativo; ownership y prohibición de acceso a almacenamiento ajeno | architecture/overview.md |
| Estado/Historia, confirmación atómica junto al resultado durable, exactitud decimal, migraciones y no Event Sourcing | architecture/persistence.md |
| Comandos, HTTP y matching/retry | architecture/http-and-idempotency.md |
| Autoridad de seguridad y actor IdentityId, alcance real de protección | architecture/security-boundaries.md |
| Sesión, cookie, antiforgery o estabilización si se modifican esos mecanismos | identities-and-capabilities/security.md |
| Git, verificación y precedencia normativa/histórica | AGENTS raíz; testing/verification.md para comandos concretos |

La corrección concreta fue hacer obligatoria y visible la ruta de seguridad/actores en la raíz, y unir explícitamente State+History+resultado durable en persistencia. No se copió el contenido técnico completo en la raíz. Las lecturas condicionales permiten que CSS puro omita esas reglas profundas mientras las tareas que cambian Estado o intenciones no puedan omitirlas.

### Jerarquía y matriz representativa

Los 19 AGENTS permanecen en las mismas rutas: raíz; backend; Host y cinco módulos; tests backend; frontend; sus seis superficies; e2e; scripts; docs. Para una sesión iniciada en raíz, solo la raíz es necesariamente descubierta de antemano; las instrucciones exigen cargar los AGENTS de las rutas afectadas. Al iniciar dentro de una subcarpeta, los ancestros forman la cadena descubierta. No se presupone carga automática de documentos enlazados ni de AGENTS hermanos. Mecánica contrastada con la documentación oficial de AGENTS.md citada al inicio del informe.

Estimaciones por archivo completo, contado una sola vez, salvo la sección Q/R/F explícitamente seleccionada. Incluyen la guía de verificación; excluyen código, tests, instrucciones externas, este informe e índice opcional. El baseline comparativo es **100 + 985 = 1.085 líneas**. Una línea puede contener un párrafo largo: no son medidas de tokens ni de reducción real de razonamiento.

| Tarea representativa | AGENTS pertinentes por ruta | Docs explícitos normalmente necesarios | Líneas AGENTS + docs | Frente a 1.085 |
| --- | --- | --- | ---: | ---: |
| A · CSS frontend | raíz → frontend | testing | 36 + 32 = **68** | −1.017 |
| B · Catalog Price Change (backend) | raíz → backend → Catalog | arquitectura, persistencia, HTTP/idempotencia, autoridad de seguridad, testing; Catalog + colaboración Confirmation | 43 + 191 = **234** | −851 |
| C · Subsequent Confirmation | raíz → backend → OrderOperations | los cinco transversales de B; Confirmation + colaboración Catalog + contratos/Historia + terminación | 51 + 340 = **391** | −694 |
| D · Preparation Start/Ready | raíz → backend → OrderOperations | los cinco transversales de B; Preparation + contratos/Historia + terminación + pendientes + sección Q/R/F | 51 + 386 = **437** | −648 |
| E · Inventory migration | raíz → backend → Inventory | arquitectura, persistencia, Development, Inventory, testing | 40 + 177 = **217** | −868 |
| F · E2E harness (esperas) | raíz → frontend → e2e | Development + testing; harness afectado como código aparte | 44 + 54 = **98** | −987 |

A se refiere a styles.css o presentación sin cambio semántico; en una superficie anidada se añade su AGENTS breve, pero no se activan las lecturas de comportamiento. B cambia un Price Change backend existente, sin rediseñar autenticación. C concreta “OrderOperations command” como Subsequent Confirmation; otros comandos sustituyen el tema local, no acumulan todos los temas. D conserva autorización por destino, buckets, Historia, retry y Freeze.

E afecta una migración de Inventory, sin cambiar su autorización. F afecta solo esperas/mecánica del harness E2E; ahora su AGENTS distingue ese caso de cambiar escenarios o expectativas funcionales. Si un E2E modifica expectativas de Preparation/Delivery o un fixture, debe añadir esos AGENTS y temas; 98 líneas no estima un cambio funcional arbitrario de toda la suite. Si C/D/E modifican también pruebas backend, se agrega tests/AGENTS.md (16 líneas) y los temas ya seleccionados se cuentan una sola vez.

Rutas exactas usadas para el cálculo:

- **A · CSS frontend**: AGENTS `AGENTS.md`, `frontend/AGENTS.md`. Documentos `docs/testing/verification.md`.

- **B · Catalog Price Change (backend)**: AGENTS `AGENTS.md`, `backend/AGENTS.md`, `backend/src/NexoBar.Catalog/AGENTS.md`. Documentos `docs/architecture/overview.md`, `docs/architecture/persistence.md`, `docs/architecture/http-and-idempotency.md`, `docs/architecture/security-boundaries.md`, `docs/testing/verification.md`, `docs/catalog/README.md`, `docs/catalog/confirmation-collaboration.md`.

- **C · Subsequent Confirmation**: AGENTS `AGENTS.md`, `backend/AGENTS.md`, `backend/src/NexoBar.OrderOperations/AGENTS.md`. Documentos `docs/architecture/overview.md`, `docs/architecture/persistence.md`, `docs/architecture/http-and-idempotency.md`, `docs/architecture/security-boundaries.md`, `docs/testing/verification.md`, `docs/order-operations/confirmation.md`, `docs/catalog/confirmation-collaboration.md`, `docs/order-operations/contracts-and-history.md`, `docs/order-operations/ending.md`.

- **D · Preparation Start/Ready**: AGENTS `AGENTS.md`, `backend/AGENTS.md`, `backend/src/NexoBar.OrderOperations/AGENTS.md`. Documentos `docs/architecture/overview.md`, `docs/architecture/persistence.md`, `docs/architecture/http-and-idempotency.md`, `docs/architecture/security-boundaries.md`, `docs/testing/verification.md`, `docs/order-operations/preparation.md`, `docs/order-operations/contracts-and-history.md`, `docs/order-operations/ending.md`, `docs/order-operations/pending.md`; sección `docs/order-operations/confirmation.md#q-r-y-f-s7-i2`.

- **E · Inventory migration**: AGENTS `AGENTS.md`, `backend/AGENTS.md`, `backend/src/NexoBar.Inventory/AGENTS.md`. Documentos `docs/architecture/overview.md`, `docs/architecture/persistence.md`, `docs/architecture/development.md`, `docs/inventory/README.md`, `docs/testing/verification.md`.

- **F · E2E harness (esperas)**: AGENTS `AGENTS.md`, `frontend/AGENTS.md`, `frontend/e2e/AGENTS.md`. Documentos `docs/architecture/development.md`, `docs/testing/verification.md`.

### Fragmentación y fuente de verdad

Solo se fusionó el contenido del diagnóstico de cantidades; no se cambiaron directorios ni se añadieron documentos. Era una autoridad ambigua: preservaba fórmulas viejas junto a evidencia de código nuevo pese a S7-I2 ya cerrado.

Se mantuvieron los documentos cortos que sirven a distintos consumidores: frontend/README contiene coordinación de App y refrescos compartidos por Catalog/Order; OperationalConfiguration conserva una capacidad con ownership independiente; seguridad transversal permite leer autoridad y deuda sin cargar credenciales/sesiones completas. Development y testing se leen juntos en E2E, pero tienen usos independientes en provisión y verificación. Contratos/Historia es compartido por Confirmation, Preparation y Delivery; fusionarlo con uno obligaría a los otros a cargar ese tema completo.

Existen resúmenes explicativos de atomicidad, clasificación e idempotencia en documentos de dominio. No se detectaron versiones contradictorias vigentes entre ellos después de corregir Q/R/F. Cada transición tiene su documento propietario; los contratos describen sus registros, y los checkpoints relatan el alcance pasado. No se intentó eliminar todas las reiteraciones explicativas.

engineering-handoff sigue siendo un índice de 32 líneas: dirige a S7-I2 sin copiar fórmulas, reglas de negocio ni totales de verificación. La comparación de conservación de la primera pasada sigue siendo evidencia histórica del traslado, no una prohibición de actualizar el baseline con decisiones aprobadas.

### Estructura anterior frente a final y archivos

Respecto de la jerarquía que llegó a esta revisión: mismas carpetas, mismos 19 AGENTS; el documento de diagnóstico desaparece y su contenido vigente/deuda se integra en Confirmation, migraciones y pendientes. Respecto del repositorio previo a la reorganización: un AGENTS global + handoff monolítico pasan al árbol temático de 46 documentos enumerado arriba.

Esta revisión modificó 15 archivos existentes en el árbol de trabajo:

- `AGENTS.md`
- `backend/src/NexoBar.OrderOperations/AGENTS.md`
- `docs/architecture/persistence.md`
- `docs/architecture/security-boundaries.md`
- `docs/context-audit.md`
- `docs/engineering-handoff.md`
- `docs/order-operations/confirmation.md`
- `docs/order-operations/delivery.md`
- `docs/order-operations/ending.md`
- `docs/order-operations/migrations.md`
- `docs/order-operations/pending.md`
- `docs/order-operations/preparation.md`
- `docs/testing/verification.md`
- `frontend/AGENTS.md`
- `frontend/e2e/AGENTS.md`

No creó archivos. Retiró `docs/order-operations/quantity-state-gap.md`, que era nuevo/no versionado de la primera pasada; no se borró ningún archivo de HEAD ni se perdió contenido útil. Frente a HEAD, el conjunto final de la reorganización tiene 2 archivos modificados y 44 nuevos, todos Markdown.

### Comprobaciones finales y deuda

- Enlaces locales y fragmentos: **46 documentos / 192 enlaces**, sin roturas; bloques Markdown balanceados.
- Búsqueda de referencias obsoletas: no quedan enlaces al diagnóstico retirado, avisos que presenten Q/R/F como base pendiente, fórmulas de obligación actual contra Q ni referencia a conciliación normativa de cantidades. Las menciones a Content Correction futura y Work de total cero son deuda real, no falsos pendientes de S7-I2.
- `git diff --check`: correcto; solo aviso habitual de normalización futura LF/CRLF.
- `npm run format:check`: correcto; incluye los AGENTS de frontend modificados.
- Archivos de software cambiados: **ninguno**. Producción, tests, migraciones, configuración ejecutable y scripts existentes conservan contenido. No se ejecutó git add, commit ni push; el índice sigue vacío.
- Deuda documental no bloqueante: las Sources/Adendas completas siguen fuera del repositorio; la referencia a seis identificadores EF largos es un hallazgo previo no revalidado aquí. Los checkpoints y resultados antiguos se conservan etiquetados con su alcance temporal.
- Deuda futura conservada: Content Correction/Cancellation/precio efectivo, excepciones y cantidades cero, seguridad global, parámetros provisionales, lifecycle, SSE, Historia UI, continuidad cross-reload y occurredAt de Liquidation en el read. Esta revisión no decide ni implementa ninguna de esas fronteras.

**Aprobar el commit de la reorganización documental.** No quedan defectos documentales bloqueantes identificados en el alcance revisado.
