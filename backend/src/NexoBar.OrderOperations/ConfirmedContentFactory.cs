using NexoBar.Catalog;

namespace NexoBar.OrderOperations;

internal static class ConfirmedContentFactory
{
    internal static ConfirmedContentCreation CreateConfirmedContent(
        Guid incorporationId,
        int contentOrdinal,
        int quantity,
        string? instruction,
        OrderConfirmationCatalogProduct product)
    {
        var requiresPreparationAtConfirmation = product.RequiresPreparation;
        var content = new IncorporationContent(
            incorporationId,
            contentOrdinal,
            product.ProductId,
            quantity,
            requiresPreparationAtConfirmation,
            product.Price,
            instruction);
        var deliveryState = new DeliveryState(incorporationId, contentOrdinal);

        if (!requiresPreparationAtConfirmation)
        {
            if (instruction is not null)
            {
                throw new InvalidOperationException(
                    "A non-prepared confirmed Content cannot have an Instruction.");
            }

            if (product.PreparationResponsibilityId is not null)
            {
                throw new InvalidOperationException(
                    "A Product that does not require preparation cannot have a Preparation Responsibility.");
            }

            return new ConfirmedContentCreation(content, null, deliveryState);
        }

        var responsibilityId = product.PreparationResponsibilityId
            ?? throw new InvalidOperationException(
                "A Product requiring preparation must have a Preparation Responsibility.");
        var work = new PreparationWork(
            Guid.CreateVersion7(),
            incorporationId,
            contentOrdinal,
            responsibilityId,
            quantity);
        return new ConfirmedContentCreation(content, work, deliveryState);
    }
}

internal sealed record ConfirmedContentCreation(
    IncorporationContent Content,
    PreparationWork? PreparationWork,
    DeliveryState DeliveryState);
