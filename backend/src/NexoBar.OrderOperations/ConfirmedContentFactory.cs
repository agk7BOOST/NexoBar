using NexoBar.Catalog;

namespace NexoBar.OrderOperations;

internal static class ConfirmedContentFactory
{
    internal static ConfirmedContentCreation CreateConfirmedContentAndPreparationWork(
        Guid incorporationId,
        int contentOrdinal,
        int quantity,
        string? instruction,
        OrderConfirmationCatalogProduct product)
    {
        var content = new IncorporationContent(
            incorporationId,
            contentOrdinal,
            product.ProductId,
            quantity,
            product.Price,
            instruction);

        if (!product.RequiresPreparation)
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

            return new ConfirmedContentCreation(content, null);
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
        return new ConfirmedContentCreation(content, work);
    }
}

internal sealed record ConfirmedContentCreation(
    IncorporationContent Content,
    PreparationWork? PreparationWork);
