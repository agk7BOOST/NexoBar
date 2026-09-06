# Verificación y herramientas del repositorio

Antes de cambiar scripts, lee [testing](../docs/testing/verification.md) y [Development](../docs/architecture/development.md). Si el cambio afecta composición o conexión de módulos, añade [persistencia](../docs/architecture/persistence.md).

Para una reorganización exclusivamente documental, comprueba enlaces/rutas, conservación del contenido, `git diff --check` y que el diff no toque software. Los AGENTS de frontend también pasan por `npm run format:check`. No presentes totales de pruebas históricos como resultados de la tarea actual.
