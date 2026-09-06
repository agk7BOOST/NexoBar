# NexoBar: instrucciones globales

## Autoridad

NexoBar tiene comportamiento funcional, modelo conceptual, UX, RNF y arquitectura definidos por Sources normativas externas del Project. Este archivo es contexto operacional; [engineering-handoff](docs/engineering-handoff.md) es la entrada al baseline técnico versionado. Ambos están subordinados a esas Sources y a decisiones posteriores aprobadas.

- No inventes decisiones ausentes ni conviertas conveniencias de implementación en normas.
- Una decisión posterior o Adenda prevalece únicamente en el asunto que modifica. Los documentos históricos no son fuente de decisiones actuales.
- Lo abierto, provisional o parametrizado sigue abierto hasta recibir una decisión aplicable. Ante una cuestión no definida o contradicción normativa, detén la parte afectada y repórtala.

## Carga de contexto por alcance

- Antes de analizar o editar un área, lee los `AGENTS.md` de su ruta, de arriba hacia abajo, incluidos los anidados que no hayan sido precargados. Sus instrucciones rigen únicamente su ámbito.
- Entradas: [backend](backend/AGENTS.md), [frontend](frontend/AGENTS.md), [documentación](docs/AGENTS.md) y [verificación](scripts/AGENTS.md). Sigue sus lecturas requeridas según la tarea; no cargues todos los módulos, migraciones o checkpoints por defecto.
- Si una tarea cruza áreas, consulta las instrucciones de cada una y la capacidad compartida afectada. Para plataforma, dependencias, despliegue o contratos, usa las rutas transversales del [índice técnico](docs/engineering-handoff.md).
- La autoridad operacional reside en backend; el cliente trabaja conectado a ella. Desde cualquier ámbito, son lecturas obligatorias según impacto: [arquitectura](docs/architecture/overview.md) para propiedad/dependencias modulares; [persistencia](docs/architecture/persistence.md) para Estado/Historia, atomicidad, cantidades exactas o migraciones; [HTTP e idempotencia](docs/architecture/http-and-idempotency.md) para comandos/clientes; [autoridad de seguridad y actores](docs/architecture/security-boundaries.md) para comandos humanos, autorización, actores o credenciales. Lee sus detalles enlazados solo si los modifica la tarea.

## Trabajo y Git

- Mantén los cambios en el propósito de la tarea. No añadas dependencias sustanciales, frameworks, tooling ni refactors adyacentes sin autorización; reporta mejoras ajenas por separado.
- Si completar la tarea exige ampliar materialmente su alcance, detente y explica la necesidad.
- Respeta cambios existentes ajenos. Los cambios de Codex son candidatos hasta la revisión del usuario.
- No hagas commit, push, `reset --hard`, rebase destructivo, limpieza destructiva ni reescritura del historial sin instrucción explícita.
- No modifiques pruebas para acomodar una implementación incorrecta. Ejecuta verificaciones aplicables y proporcionales al riesgo; diagnostica y reporta cualquier fallo, sin ocultarlo ni ignorarlo.

Al terminar informa concisamente: qué cambió; verificaciones y resultados; qué quedó fuera de alcance; bloqueos, riesgos o decisiones pendientes.
