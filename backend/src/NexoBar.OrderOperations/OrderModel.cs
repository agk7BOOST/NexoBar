namespace NexoBar.OrderOperations;

internal sealed class Order
{
    private Order() { }

    internal Order(Guid id, string context)
    {
        Id = id;
        Context = context;
    }

    internal Guid Id { get; private set; }
    internal string Context { get; private set; } = string.Empty;
}

internal sealed class Incorporation
{
    private Incorporation() { }

    internal Incorporation(Guid id, Guid orderId, int ordinal)
    {
        Id = id;
        OrderId = orderId;
        Ordinal = ordinal;
    }

    internal Guid Id { get; private set; }
    internal Guid OrderId { get; private set; }
    internal int Ordinal { get; private set; }
}

internal sealed class IncorporationContent
{
    private IncorporationContent() { }

    internal IncorporationContent(
        Guid incorporationId,
        int contentOrdinal,
        Guid productId,
        int quantity,
        decimal appliedPrice,
        string? instruction)
    {
        IncorporationId = incorporationId;
        ContentOrdinal = contentOrdinal;
        ProductId = productId;
        Quantity = quantity;
        AppliedPrice = appliedPrice;
        Instruction = instruction;
    }

    internal Guid IncorporationId { get; private set; }
    internal int ContentOrdinal { get; private set; }
    internal Guid ProductId { get; private set; }
    internal int Quantity { get; private set; }
    internal decimal AppliedPrice { get; private set; }
    internal string? Instruction { get; private set; }
}

internal sealed class PreparationWork
{
    private PreparationWork() { }

    internal PreparationWork(
        Guid id,
        Guid incorporationId,
        int contentOrdinal,
        Guid preparationResponsibilityId,
        int totalQuantity)
    {
        Id = id;
        IncorporationId = incorporationId;
        ContentOrdinal = contentOrdinal;
        PreparationResponsibilityId = preparationResponsibilityId;
        TotalQuantity = totalQuantity;
        PendingQuantity = totalQuantity;
        InPreparationQuantity = 0;
        ReadyQuantity = 0;
    }

    internal Guid Id { get; private set; }
    internal Guid IncorporationId { get; private set; }
    internal int ContentOrdinal { get; private set; }
    internal Guid PreparationResponsibilityId { get; private set; }
    internal int TotalQuantity { get; private set; }
    internal int PendingQuantity { get; private set; }
    internal int InPreparationQuantity { get; private set; }
    internal int ReadyQuantity { get; private set; }
}

internal sealed class ConfirmationHistory
{
    private ConfirmationHistory() { }

    internal ConfirmationHistory(
        Guid id,
        Guid incorporationId,
        string confirmedContext,
        DateTimeOffset occurredAt)
    {
        Id = id;
        IncorporationId = incorporationId;
        ConfirmedContext = confirmedContext;
        OccurredAt = occurredAt;
    }

    internal Guid Id { get; private set; }
    internal Guid IncorporationId { get; private set; }
    internal string ConfirmedContext { get; private set; } = string.Empty;
    internal DateTimeOffset OccurredAt { get; private set; }
}

internal sealed class FirstConfirmationCommand
{
    private FirstConfirmationCommand() { }

    internal FirstConfirmationCommand(
        Guid idempotencyKey,
        string intentContext,
        Guid resultIncorporationId)
    {
        IdempotencyKey = idempotencyKey;
        IntentContext = intentContext;
        ResultIncorporationId = resultIncorporationId;
    }

    internal Guid IdempotencyKey { get; private set; }
    internal string IntentContext { get; private set; } = string.Empty;
    internal Guid ResultIncorporationId { get; private set; }
}

internal sealed class FirstConfirmationCommandContent
{
    private FirstConfirmationCommandContent() { }

    internal FirstConfirmationCommandContent(
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
