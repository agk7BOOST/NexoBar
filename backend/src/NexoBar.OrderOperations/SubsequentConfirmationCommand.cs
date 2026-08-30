namespace NexoBar.OrderOperations;

internal sealed class SubsequentConfirmationCommand
{
    private SubsequentConfirmationCommand() { }

    internal SubsequentConfirmationCommand(
        Guid idempotencyKey,
        Guid intentOrderId,
        Guid resultIncorporationId)
    {
        IdempotencyKey = idempotencyKey;
        IntentOrderId = intentOrderId;
        ResultIncorporationId = resultIncorporationId;
    }

    internal Guid IdempotencyKey { get; private set; }
    internal Guid IntentOrderId { get; private set; }
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
