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

Los comandos administrativos durables usan `Idempotency-Key` UUID v4 y persisten `ActorIdentityId`, command kind, fingerprint estructural y result payload. La misma combinación actor/key/intención hace replay; una key reutilizada con actor o intención diferentes produce conflicto. `SessionId` no es actor durable.

El replay exige todavía una sesión actual válida y una Identity activa. Si el resultado ya fue confirmado, se reproduce antes de revalidar `GeneralConfiguration`: revocar una capability después del éxito no reinterpreta el efecto histórico de ese comando. Para una intención de credencial, la comparación durable guarda un verifier lento de la intención y usa el mismo verificador de secretos; no guarda el secret crudo ni un digest rápido sin salt. Estos registros técnicos de comandos no equivalen a Historia funcional.

Bootstrap, recovery, Delete Identity y profundidad de Historia administrativa: [pendientes de seguridad](../architecture/security-boundaries.md).
