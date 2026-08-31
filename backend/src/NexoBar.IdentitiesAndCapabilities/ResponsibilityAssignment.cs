namespace NexoBar.IdentitiesAndCapabilities;

internal sealed class ResponsibilityAssignment
{
    private ResponsibilityAssignment()
    {
    }

    internal ResponsibilityAssignment(
        Guid identityId,
        FunctionalResponsibility responsibilityCode)
    {
        IdentityId = identityId;
        ResponsibilityCode = responsibilityCode;
    }

    internal Guid IdentityId { get; private set; }

    internal FunctionalResponsibility ResponsibilityCode { get; private set; }
}
