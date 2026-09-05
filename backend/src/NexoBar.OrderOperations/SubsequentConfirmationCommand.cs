namespace NexoBar.OrderOperations;

internal sealed class SubsequentConfirmationCommand
{
    private SubsequentConfirmationCommand() { }

    internal SubsequentConfirmationCommand(
        Guid idempotencyKey,
        Guid actorIdentityId,
        Guid intentOrderId,
        Guid intentPendingCompositionId,
        Guid resultIncorporationId)
    {
        IdempotencyKey = idempotencyKey;
        ActorIdentityId = actorIdentityId;
        IntentOrderId = intentOrderId;
        IntentPendingCompositionId = intentPendingCompositionId;
        ResultIncorporationId = resultIncorporationId;
    }

    internal Guid IdempotencyKey { get; private set; }
    internal Guid? ActorIdentityId { get; private set; }
    internal Guid IntentOrderId { get; private set; }
    internal Guid? IntentPendingCompositionId { get; private set; }
    internal Guid ResultIncorporationId { get; private set; }
}

internal sealed class SubsequentConfirmationCommandContent
{
    private SubsequentConfirmationCommandContent() { }

    internal SubsequentConfirmationCommandContent(
        Guid idempotencyKey,
        int lineOrdinal,
        Guid productId,
        int quantity,
        string? instruction)
    {
        IdempotencyKey = idempotencyKey;
        LineOrdinal = lineOrdinal;
        ProductId = productId;
        Quantity = quantity;
        Instruction = instruction;
    }

    internal Guid IdempotencyKey { get; private set; }
    internal int LineOrdinal { get; private set; }
    internal Guid ProductId { get; private set; }
    internal int Quantity { get; private set; }
    internal string? Instruction { get; private set; }
}
