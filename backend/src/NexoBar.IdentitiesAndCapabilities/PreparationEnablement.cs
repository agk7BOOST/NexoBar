namespace NexoBar.IdentitiesAndCapabilities;

internal sealed class PreparationEnablement
{
    private PreparationEnablement()
    {
    }

    internal PreparationEnablement(
        Guid identityId,
        Guid preparationResponsibilityId)
    {
        IdentityId = identityId;
        PreparationResponsibilityId = preparationResponsibilityId;
    }

    internal Guid IdentityId { get; private set; }

    internal Guid PreparationResponsibilityId { get; private set; }
}
