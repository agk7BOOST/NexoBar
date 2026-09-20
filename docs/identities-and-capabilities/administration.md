# Administración de Identity

## Administración de Identity

El backend implementa intenciones específicas para Create Identity, Change Operational Name, Activate, Deactivate, Set/Replace Local Credential, Assign/Revoke Functional Responsibility y Grant/Revoke Preparation Enablement. No expone CRUD genérico ni Delete Identity.

```text
POST /api/identities
POST /api/identities/{identityId}/change-operational-name
POST /api/identities/{identityId}/activate
POST /api/identities/{identityId}/deactivate
POST /api/identities/{identityId}/credential
POST /api/identities/{identityId}/responsibilities/{code}/assign
POST /api/identities/{identityId}/responsibilities/{code}/revoke
POST /api/identities/{identityId}/preparation-enablement/{responsibilityId}/grant
POST /api/identities/{identityId}/preparation-enablement/{responsibilityId}/revoke
```

`GET /api/identities` requiere `GeneralConfiguration` y devuelve Estado de Identity, capabilities, enablements y login identifier; no devuelve verifier, tokens ni detalles de sesiones.

La administración ordinaria debe preservar al menos un camino operacional vigente de `GeneralConfiguration`. La definición técnica actual del camino es: Identity activa + assignment `GeneralConfiguration` + `LocalCredential` utilizable. No requiere una sesión activa. Un advisory lock estable, transaction-scoped, serializa las mutaciones administrativas relevantes y evita carreras de revocación/desactivación que dejen cero caminos. Esto no implementa recovery extraordinario: `AD-SEC-01` continúa pendiente productivamente.

## Vertical web S9-I2 — General Configuration

El frontend expone la superficie administrativa **Configuración general** sólo cuando la proyección actual de `GET /api/identity-sessions/current` contiene `GeneralConfiguration`. Ese chequeo de capability controla exclusivamente el montaje/navegación de la superficie cliente; la autorización de cada read o comando permanece en el backend.

La superficie implementada permite listar Identities, crear Identity, cambiar su nombre operacional, activar/desactivar, asignar/revocar Functional Responsibilities, otorgar/revocar habilitaciones de Preparation y configurar/reemplazar `LocalCredential`. No implementa Delete Identity, recovery ni administración arbitraria de Sessions.

El listado y cada resultado de mutación son Estado autoritativo del backend. El cliente reconcilia el resultado recibido y no trata una mutación optimista como Estado confirmado. Ante incertidumbre de red conserva la misma intención de comando y `Idempotency-Key` para un reintento explícito.

Si una mutación afecta las Functional Responsibilities de la Identity actuante, el frontend reconcilia la sesión actual cuando corresponde. Una auto-revocación exitosa de `GeneralConfiguration` retira la superficie sólo después de que el snapshot autoritativo de Identity actual ya no contiene esa responsabilidad; el frontend no concede ni revoca autoridad por sí mismo.

El backend rechaza la remoción de la última vía ordinaria utilizable con `identities_and_capabilities.last_general_configuration_path`. La UI muestra su rechazo de negocio específico y conserva el Estado autoritativo; no reproduce el algoritmo de salvaguarda como autoridad cliente.

Las habilitaciones de Preparation se administran independientemente de la Functional Responsibility `Preparation`. La lectura de `OperationalConfiguration` autorizada por `GeneralConfiguration` resuelve sus IDs a nombres operacionales de Preparation Responsibility; otorgar o revocar una habilitación no asigna ni revoca implícitamente `Preparation`, y los IDs desconocidos no se descartan silenciosamente. S9-I2 no agrega UI para crear ni administrar el lifecycle de Preparation Responsibility.

Para una Identity sin credencial, la configuración exige identificador de acceso y secret explícitos. Para una credencial existente se soporta reemplazar el secret y el identificador se conserva salvo reemplazo explícito. El secret es Estado transitorio del frontend: no es legible desde backend. Reemplazar una credencial revoca todas las Sessions de la Identity objetivo. En auto-reemplazo, un POST exitoso se reconoce primero como committed y el frontend vuelve después a login; la invalidación posterior de Session no reinterpreta el comando como fallo.

Create Identity no crea `LocalCredential`. Cuando el request omite `isActive`, el backend aplica `false`; la UI actual envía sólo el nombre operacional, por lo que crea una Identity sin credencial e inactiva. El checkpoint E2E S9-I2 lo observó contra backend real; no debe confundirse con el provisioning técnico inicial, que crea la primera vía administrativa activa.

### Provisioning inicial técnico (`AD-SEC-06`)

El primer camino de una instalación nueva procede del subcomando Host `provision-initial-admin`. Crea exactamente una Identity activa, una `LocalCredential` y la asignación `GeneralConfiguration`; no acepta responsabilidades adicionales, habilitaciones de Preparation ni un flag de actividad.

Sus entradas obligatorias son `--operational-name`, `--login-identifier`, `--command-id` UUID v4 y el secret de credencial por stdin redirigido. El secret no es un argumento. Una invocación conceptual es:

```text
<protected-secret-source> | dotnet NexoBar.Host.dll provision-initial-admin --operational-name "<operational-name>" --login-identifier "<login>" --command-id "<uuid-v4>"
```

`<protected-secret-source>` representa un mecanismo de secreto del entorno de despliegue que escribe una sola línea; no debe reemplazarse por un secret literal en el historial de shell. Stdin interactivo se rechaza. El Host entra en este modo antes de construir `WebApplication`: compone sólo lo necesario, no abre listeners HTTP y termina al devolver el resultado.

La autoridad es la de ejecución del proceso/deployment. Antes del provisioning no existe Session ni Identity NexoBar que autorice el acto; no existe responsabilidad Bootstrap y no se crea una Identity técnica, superadministrador ni comando administrativo ordinario.

Hay dos gates acumulativos: el proceso debe invocar explícitamente `provision-initial-admin` y no debe existir `InstallationProvisioningFact`. La ausencia del fact no autoriza el startup ordinario del Host. Si el fact existe, siempre prevalece sobre argumentos o configuración de deployment: una tentativa independiente devuelve `already_initialized` y no muta Estado.

El fact pertenece a `IdentitiesAndCapabilities`. La migración inserta exactamente un `LegacyBackfill` si una base pre-feature contiene cualquier Identity, incluso inactiva o sin credencial/`GeneralConfiguration`; una base vacía no recibe un fact inventado. Por eso el operador sólo debe ejecutar el subcomando contra una base positivamente destinada a una instalación nueva: una base vacía no se considera automáticamente fresca o segura.

Identity, `LocalCredential`, asignación `GeneralConfiguration` y `InstallationProvisioningFact` se confirman juntos en una transacción de `IdentitiesAndCapabilities`. Un advisory transaction lock PostgreSQL serializa las tentativas y deja como máximo una tentativa independiente exitosa.

El operador/deployment conserva el command ID mientras el resultado sea incierto. El mismo command ID, intención canónica (nombre operacional y login normalizado) y secret produce `replayed_success`; el mismo ID con intención o secret distinto produce `intent_conflict`; un ID distinto tras el éxito produce `already_initialized`. El reintento compara el secret mediante un verifier lento persistido; no expone `RetrySecretVerifier`.

Los outcomes y exits son estables para scripts: `success`/`replayed_success` = 0, `infrastructure_failure` = 1, `invalid_input` = 2, `already_initialized` = 3, `intent_conflict` = 4 y `duplicate_login` = 5. La salida ordinaria no contiene secret ni verifier/hash.

`InstallationProvisioningFact` es el hecho durable del provisioning exitoso. Los logs estructurados registran metadatos seguros de intento, outcome, command ID y, en el éxito, Identity ID; excluyen secrets y verifiers. No existe por ello una facilidad genérica de auditoría administrativa durable.

Una vez inicializada la instalación, bootstrap no es administración ordinaria ni recovery. Desactivar/eliminar administradores, perder credenciales o perder Sessions no reactiva el subcomando. La recuperación extraordinaria posterior pertenece a `AD-SEC-01`, que sigue separada y pendiente. Véanse las [fronteras de seguridad](../architecture/security-boundaries.md).

Los comandos administrativos durables usan `Idempotency-Key` UUID v4 y persisten `ActorIdentityId`, command kind, fingerprint estructural y result payload. La misma combinación actor/key/intención hace replay; una key reutilizada con actor o intención diferentes produce conflicto. `SessionId` no es actor durable.

El replay exige todavía una sesión actual válida y una Identity activa. Si el resultado ya fue confirmado, se reproduce antes de revalidar `GeneralConfiguration`: revocar una capability después del éxito no reinterpreta el efecto histórico de ese comando. Para una intención de credencial, la comparación durable guarda un verifier lento de la intención y usa el mismo verificador de secretos; no guarda el secret crudo ni un digest rápido sin salt. Estos registros técnicos de comandos no equivalen a Historia funcional.

Recovery, Delete Identity y profundidad de Historia administrativa: [pendientes de seguridad](../architecture/security-boundaries.md).
