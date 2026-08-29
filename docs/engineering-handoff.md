# NexoBar - Engineering Handoff

## 1. Propósito y autoridad

- Este documento registra el baseline técnico aprobado para pasar de arquitectura a implementación.
- No reemplaza la Especificación Funcional, el Modelo Conceptual, UX, RNF, Arquitectura ni Adendas.
- Ante una discrepancia, prevalece la fuente normativa aplicable.
- Las decisiones posteriores o Adendas solo sustituyen la parte concreta que modifican.
- Los documentos históricos no son autoridad.
- Lo explícitamente OPEN, PAR o provisional sigue abierto salvo decisión posterior aplicable.

## 2. Plataforma y repositorio

- Monorepo Git único.
- Backend: C# sobre .NET 10 LTS y ASP.NET Core 10.
- Frontend: React 19, TypeScript y Vite 8.
- npm como package manager.
- Toolchains declarados en el repositorio.
- Desarrollo híbrido reproducible: backend y frontend pueden ejecutarse localmente; la infraestructura local se proporciona con contenedores.
- Docker no es requisito para toda edición cotidiana.
- No se usa Nx, Turborepo ni otro orquestador de monorepo.

## 3. Backend modular

- Una sola aplicación backend principal y una unidad principal de despliegue.
- Un proyecto host ASP.NET Core.
- Un proyecto/assembly por cada módulo superior:
  - `NexoBar.OrderOperations`
  - `NexoBar.Catalog`
  - `NexoBar.Inventory`
  - `NexoBar.IdentitiesAndCapabilities`
  - `NexoBar.OperationalConfiguration`
- Implementación interna por defecto y superficie pública mínima.
- No se crean proyectos por capa ni un `Shared`/`Common` genérico preventivo.
- No hay dependencias cíclicas entre módulos.
- La frontera Pedido/Cumplimiento - Preparación permanece dentro de `OrderOperations`.

## 4. Persistencia y datos

- PostgreSQL es la base relacional transaccional primaria compartida.
- EF Core 10 y Npgsql son la estrategia de persistencia predeterminada.
- Cada módulo superior posee su propio `DbContext`, mapping y evolución física.
- Compartir base no autoriza modificar directamente el Estado de otro módulo.
- Puede usarse SQL explícito cuando concurrencia, invariantes o rendimiento lo justifiquen.
- No se usan Repository Pattern genérico ni lazy loading por defecto.
- Las migraciones EF Core son explícitas, versionadas, revisables y probadas. La aplicación productiva no migra silenciosamente la base al arrancar.
- Estado vigente e Historia semántica son distintos y deben confirmarse atómicamente cuando sean consecuencias inseparables.
- Esto no es CQRS ni Event Sourcing.

## 5. Contratos y comandos

- HTTPS y JSON son la frontera ordinaria.
- Las consultas pueden orientarse a recursos.
- Las mutaciones representan intenciones o comandos explícitos, no reemplazo CRUD genérico del Estado autoritativo.
- ASP.NET Core Minimal APIs es la base de endpoints.
- Los handlers HTTP son delgados; las reglas de negocio permanecen dentro del módulo correspondiente.
- Problem Details es la estructura base de errores.
- OpenAPI describe el contrato técnico implementado, no el significado funcional.
- No hay versionado explícito de API mientras frontend y backend propios evolucionen coordinadamente y no exista una necesidad real.

## 6. Idempotencia y resultado incierto

- Todo comando externo que pueda producir significado operacional persistente usa por defecto identidad de comando.
- `Idempotency-Key` contiene un UUID v4 generado en el cliente antes del primer envío.
- Un reintento de la misma intención reutiliza exactamente la misma identidad.
- Identidad, efecto, Estado e Historia inseparables se vinculan dentro de la misma frontera transaccional.
- Repetir una identidad confirmada con la misma intención no crea un segundo efecto.
- Reutilizar la misma identidad para una intención distinta debe rechazarse.
- Un fallo de comunicación posterior al envío no se interpreta automáticamente como fracaso.
- No hay retry ciego universal para mutaciones.

## 7. Identificadores, exactitud y tiempo

- Los conceptos persistentes principales que necesiten identidad técnica estable usan por defecto UUID v7 generado por el backend. Esto no implica que todo concepto reciba UUID propio.
- UUID no es fuente autoritativa de orden funcional.
- Dinero y cantidades exactas usan `decimal` en C# y `numeric` en PostgreSQL.
- Precisión, escala y redondeo se resuelven just-in-time.
- Dinero y cantidades decimales exactas viajan por JSON mediante representación decimal textual estable.
- Los instantes operacionales autoritativos son asignados por el backend y persistidos en UTC, usando `DateTimeOffset`/`timestamptz` como base.
- Un timestamp no reemplaza mecanismos de concurrencia, revisión o coordinación.
- `PAR-TIME-01` permanece sin resolver.

## 8. Testing y calidad

- Backend: xUnit.net v3 para unit tests.
- Integración contra PostgreSQL real mediante Testcontainers cuando corresponda.
- PostgreSQL no se sustituye por SQLite/in-memory para validar comportamiento dependiente de transacciones, locking o concurrencia.
- Frontend: Vitest y React Testing Library.
- E2E críticos: Playwright, en cantidad reducida.
- No se fija un porcentaje arbitrario de coverage ni un mocking framework obligatorio inicialmente.
- TypeScript estricto.
- Nullable reference types habilitados en backend.
- Los warnings del código NexoBar se tratan como errores salvo excepción localizada y justificada.
- ESLint y Prettier en frontend.
- Verificación global mediante `scripts/verify.cmd` y `scripts/verify.sh`; las suites de tests se incorporarán a esos wrappers cuando existan.
- Existe una segunda capa E2E del vertical slice con Playwright sobre Chromium y PostgreSQL efímero aislado; no reutiliza la base de `compose.yaml`.
- La verificación ordinaria no ejecuta E2E. Para incluirla se usa `scripts\verify.cmd --e2e` en Windows o `./scripts/verify.sh --e2e` en Unix.
- La ejecución E2E requiere Docker operativo y Chromium de Playwright instalado. La preparación inicial del navegador se realiza desde `frontend` con `npm exec playwright install chromium`.
- Las migraciones E2E se aplican explícitamente mediante `NexoBar.E2E.DatabaseSetup` antes de iniciar la aplicación.
- Los E2E deben ejecutarse para cambios del vertical slice y antes de considerar cerrado I4.

## 9. Configuración y secretos

- La configuración técnica y de infraestructura no se confunde con la Configuración Operacional del dominio.
- ASP.NET Core usa prioritariamente configuración nativa y Options tipadas y validadas.
- Entornos iniciales: Development, Testing y Production.
- Los secretos reales nunca se versionan ni se hornean en artefactos.
- Las credenciales conocidas se permiten solo para infraestructura local descartable y explícitamente no productiva.
- El frontend no contiene secretos.
- El entorno no cambia silenciosamente reglas funcionales.

## 10. Infraestructura local

- PostgreSQL local se ejecuta con Docker Compose.
- El `compose.yaml` actual define el entorno de desarrollo aceptado.
- PostgreSQL local no equivale a la topología productiva.
- Producción continúa requiriendo runtime cloud gestionado y PostgreSQL gestionado conforme a Arquitectura; el proveedor y la configuración concreta siguen pendientes.

## 11. Protocolo Codex -> revisión -> commit

- `AGENTS.md` es el contexto operacional persistente de Codex.
- Cada tarea debe tener objetivo, contexto aplicable, alcance, criterios verificables y condiciones de detención.
- Codex puede inspeccionar, modificar dentro del alcance y ejecutar verificaciones no destructivas sin pedir permiso por cada acción.
- Debe detenerse si necesita inventar o resolver una decisión funcional, conceptual, UX, RNF o arquitectónica no aprobada.
- No añade dependencias sustanciales ni amplía el alcance silenciosamente.
- No modifica pruebas normativas solo para acomodar una implementación incorrecta.
- No hace commit, push ni operaciones Git destructivas salvo instrucción explícita.
- El working tree contiene el candidato; el commit representa un baseline revisado y aceptado.
- Al terminar una tarea reporta cambios, verificaciones, resultado y bloqueos o riesgos.

## 12. Cuestiones conscientemente diferidas

Siguen abiertas para resolución just-in-time, entre otras:

- modelo físico concreto del dominio;
- tablas, columnas, índices y constraints;
- precisión, escala y redondeo;
- locking concreto de Pedido;
- revisión concreta de Inventario;
- almacenamiento, fingerprint y retención de idempotencia;
- autenticación y transporte de sesión;
- recuperación de acceso;
- autorización concreta por consulta;
- contrato SSE;
- router y gestión de estado remoto del frontend;
- librerías de formularios/UI;
- generación o no de cliente TypeScript desde OpenAPI;
- proveedor cloud y packaging productivo;
- observabilidad productiva;
- backup/PITR concreto;
- retención;
- OPEN, PAR e HYP que las Sources mantengan vigentes.

## 13. Regla para decisiones futuras

- Las decisiones técnicas se resuelven progresivamente, en el momento mínimo necesario.
- No se hace BDUF del dominio técnico.
- Una nueva tecnología, patrón o complejidad requiere un driver concreto.
- Si la implementación revela una cuestión que cambia comportamiento funcional, conceptual, UX, RNF o arquitectura, no se resuelve en código: se eleva nuevamente al nivel correspondiente del Project y, si afecta una Source base cerrada, se utiliza el mecanismo de Adendas establecido.
