namespace NexoBar.IdentitiesAndCapabilities;

internal sealed record CreateIdentityRequest(string? OperationalName, bool? IsActive);

internal sealed record ChangeIdentityOperationalNameRequest(string? OperationalName);

internal sealed record SetLocalCredentialRequest(string? LoginIdentifier, string? Secret);

internal sealed record IdentityAdministrationResponse(
    Guid IdentityId,
    string OperationalName,
    bool IsActive,
    bool HasLocalCredential,
    string? LoginIdentifier,
    IReadOnlyList<string> Responsibilities,
    IReadOnlyList<Guid> PreparationEnablements);

internal sealed record IdentityAdministrationListResult(
    IdentityAdministrationOutcome Outcome,
    IReadOnlyList<IdentityAdministrationResponse>? Identities)
{
    internal static IdentityAdministrationListResult Succeeded(
        IReadOnlyList<IdentityAdministrationResponse> identities) =>
        new(IdentityAdministrationOutcome.Succeeded, identities);

    internal static IdentityAdministrationListResult AuthenticationRequired() =>
        new(IdentityAdministrationOutcome.AuthenticationRequired, null);

    internal static IdentityAdministrationListResult GeneralConfigurationRequired() =>
        new(IdentityAdministrationOutcome.GeneralConfigurationRequired, null);
}

internal sealed record IdentityAdministrationResult(
    IdentityAdministrationOutcome Outcome,
    IdentityAdministrationResponse? Identity,
    string? InvalidField)
{
    internal static IdentityAdministrationResult Succeeded(
        IdentityAdministrationResponse identity) =>
        new(IdentityAdministrationOutcome.Succeeded, identity, null);

    internal static IdentityAdministrationResult Invalid(string field) =>
        new(IdentityAdministrationOutcome.Invalid, null, field);

    internal static IdentityAdministrationResult AuthenticationRequired() =>
        new(IdentityAdministrationOutcome.AuthenticationRequired, null, null);

    internal static IdentityAdministrationResult GeneralConfigurationRequired() =>
        new(IdentityAdministrationOutcome.GeneralConfigurationRequired, null, null);

    internal static IdentityAdministrationResult NotFound() =>
        new(IdentityAdministrationOutcome.NotFound, null, null);

    internal static IdentityAdministrationResult PreparationResponsibilityNotFound() =>
        new(
            IdentityAdministrationOutcome.PreparationResponsibilityNotFound,
            null,
            null);

    internal static IdentityAdministrationResult LastGeneralConfigurationPath() =>
        new(
            IdentityAdministrationOutcome.LastGeneralConfigurationPath,
            null,
            null);

    internal static IdentityAdministrationResult DuplicateLoginIdentifier() =>
        new(
            IdentityAdministrationOutcome.DuplicateLoginIdentifier,
            null,
            null);

    internal static IdentityAdministrationResult IdempotencyConflict() =>
        new(IdentityAdministrationOutcome.IdempotencyConflict, null, null);
}

internal enum IdentityAdministrationOutcome
{
    Succeeded,
    Invalid,
    AuthenticationRequired,
    GeneralConfigurationRequired,
    NotFound,
    PreparationResponsibilityNotFound,
    LastGeneralConfigurationPath,
    DuplicateLoginIdentifier,
    IdempotencyConflict
}
