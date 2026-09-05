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

internal sealed class Liquidation
{
    private Liquidation() { }

    internal Liquidation(
        Guid id,
        Guid orderId,
        string mode,
        decimal functionalAmount,
        string? declaredPaymentMedium,
        DateTimeOffset occurredAt,
        Guid actorIdentityId)
    {
        Id = id;
        OrderId = orderId;
        Mode = mode;
        FunctionalAmount = functionalAmount;
        DeclaredPaymentMedium = declaredPaymentMedium;
        OccurredAt = occurredAt;
        ActorIdentityId = actorIdentityId;
    }

    internal Guid Id { get; private set; }
    internal Guid OrderId { get; private set; }
    internal string Mode { get; private set; } = string.Empty;
    internal decimal FunctionalAmount { get; private set; }
    internal string? DeclaredPaymentMedium { get; private set; }
    internal DateTimeOffset OccurredAt { get; private set; }
    internal Guid ActorIdentityId { get; private set; }
}

internal sealed class LiquidationHistory
{
    internal const string LiquidatedEventKind = "Liquidated";

    private LiquidationHistory() { }

    internal LiquidationHistory(
        Guid id,
        Guid liquidationId,
        Guid orderId,
        string mode,
        decimal functionalAmount,
        string? declaredPaymentMedium,
        DateTimeOffset occurredAt,
        Guid actorIdentityId)
    {
        Id = id;
        LiquidationId = liquidationId;
        OrderId = orderId;
        EventKind = LiquidatedEventKind;
        Mode = mode;
        FunctionalAmount = functionalAmount;
        DeclaredPaymentMedium = declaredPaymentMedium;
        OccurredAt = occurredAt;
        ActorIdentityId = actorIdentityId;
    }

    internal Guid Id { get; private set; }
    internal Guid LiquidationId { get; private set; }
    internal Guid OrderId { get; private set; }
    internal string EventKind { get; private set; } = string.Empty;
    internal string Mode { get; private set; } = string.Empty;
    internal decimal FunctionalAmount { get; private set; }
    internal string? DeclaredPaymentMedium { get; private set; }
    internal DateTimeOffset OccurredAt { get; private set; }
    internal Guid ActorIdentityId { get; private set; }
}

internal sealed class LiquidationCommand
{
    internal const string LiquidateSimpleCommandKind = "LiquidateSimple";
    internal const string RecordExternalCollectionCommandKind = "RecordExternalCollection";

    private LiquidationCommand() { }

    internal LiquidationCommand(
        Guid idempotencyKey,
        Guid actorIdentityId,
        string commandKind,
        Guid orderId,
        string? declaredPaymentMedium,
        LiquidationCommandResult result)
    {
        IdempotencyKey = idempotencyKey;
        ActorIdentityId = actorIdentityId;
        CommandKind = commandKind;
        OrderId = orderId;
        DeclaredPaymentMedium = declaredPaymentMedium;
        ResultLiquidationId = result.LiquidationId;
        ResultMode = result.Mode;
        ResultFunctionalAmount = result.FunctionalAmount;
        ResultDeclaredPaymentMedium = result.DeclaredPaymentMedium;
        ResultOccurredAt = result.OccurredAt;
    }

    internal Guid IdempotencyKey { get; private set; }
    internal Guid ActorIdentityId { get; private set; }
    internal string CommandKind { get; private set; } = string.Empty;
    internal Guid OrderId { get; private set; }
    internal string? DeclaredPaymentMedium { get; private set; }
    internal Guid ResultLiquidationId { get; private set; }
    internal string ResultMode { get; private set; } = string.Empty;
    internal decimal ResultFunctionalAmount { get; private set; }
    internal string? ResultDeclaredPaymentMedium { get; private set; }
    internal DateTimeOffset ResultOccurredAt { get; private set; }

    internal bool Matches(
        Guid actorIdentityId,
        string commandKind,
        Guid orderId,
        string? declaredPaymentMedium) =>
        ActorIdentityId == actorIdentityId &&
        string.Equals(CommandKind, commandKind, StringComparison.Ordinal) &&
        OrderId == orderId &&
        string.Equals(
            DeclaredPaymentMedium,
            declaredPaymentMedium,
            StringComparison.Ordinal);

    internal LiquidationCommandResult ToResult() => new(
        ResultLiquidationId,
        OrderId,
        ResultMode,
        ResultFunctionalAmount,
        ResultDeclaredPaymentMedium,
        ResultOccurredAt);
}

internal sealed record LiquidationCommandResult(
    Guid LiquidationId,
    Guid OrderId,
    string Mode,
    decimal FunctionalAmount,
    string? DeclaredPaymentMedium,
    DateTimeOffset OccurredAt);

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
        bool requiresPreparationAtConfirmation,
        decimal appliedPrice,
        string? instruction)
    {
        IncorporationId = incorporationId;
        ContentOrdinal = contentOrdinal;
        ProductId = productId;
        Quantity = quantity;
        RequiresPreparationAtConfirmation = requiresPreparationAtConfirmation;
        AppliedPrice = appliedPrice;
        Instruction = instruction;
    }

    internal Guid IncorporationId { get; private set; }
    internal int ContentOrdinal { get; private set; }
    internal Guid ProductId { get; private set; }
    internal int Quantity { get; private set; }
    internal bool RequiresPreparationAtConfirmation { get; private set; }
    internal decimal AppliedPrice { get; private set; }
    internal string? Instruction { get; private set; }
}

internal sealed class DeliveryState
{
    private DeliveryState() { }

    internal DeliveryState(Guid incorporationId, int contentOrdinal)
    {
        IncorporationId = incorporationId;
        ContentOrdinal = contentOrdinal;
        DeliveredQuantity = 0;
    }

    internal Guid IncorporationId { get; private set; }
    internal int ContentOrdinal { get; private set; }
    internal int DeliveredQuantity { get; private set; }

    internal DeliveryTransition Deliver(int quantity, int deliverableQuantity)
    {
        if (quantity <= 0)
        {
            return DeliveryTransition.QuantityInvalid;
        }

        if (quantity > deliverableQuantity)
        {
            return DeliveryTransition.DeliverableQuantityInsufficient;
        }

        DeliveredQuantity += quantity;
        return DeliveryTransition.Delivered;
    }
}

internal enum DeliveryTransition
{
    Delivered,
    QuantityInvalid,
    DeliverableQuantityInsufficient
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

    internal PreparationStartTransition Start(int quantity)
    {
        if (quantity <= 0)
        {
            return PreparationStartTransition.QuantityInvalid;
        }

        if (PendingQuantity < quantity)
        {
            return PreparationStartTransition.PendingQuantityInsufficient;
        }

        PendingQuantity -= quantity;
        InPreparationQuantity += quantity;
        return PreparationStartTransition.Started;
    }

    internal PreparationReadyTransition MarkReady(int quantity)
    {
        if (quantity <= 0)
        {
            return PreparationReadyTransition.QuantityInvalid;
        }

        if (InPreparationQuantity < quantity)
        {
            return PreparationReadyTransition.InPreparationQuantityInsufficient;
        }

        InPreparationQuantity -= quantity;
        ReadyQuantity += quantity;
        return PreparationReadyTransition.MarkedReady;
    }
}

internal enum PreparationStartTransition
{
    Started,
    QuantityInvalid,
    PendingQuantityInsufficient
}

internal enum PreparationReadyTransition
{
    MarkedReady,
    QuantityInvalid,
    InPreparationQuantityInsufficient
}

internal sealed class ConfirmationHistory
{
    private ConfirmationHistory() { }

    internal ConfirmationHistory(
        Guid id,
        Guid incorporationId,
        string confirmedContext,
        Guid actorIdentityId,
        DateTimeOffset occurredAt)
    {
        Id = id;
        IncorporationId = incorporationId;
        ConfirmedContext = confirmedContext;
        ActorIdentityId = actorIdentityId;
        OccurredAt = occurredAt;
    }

    internal Guid Id { get; private set; }
    internal Guid IncorporationId { get; private set; }
    internal string ConfirmedContext { get; private set; } = string.Empty;
    internal Guid? ActorIdentityId { get; private set; }
    internal DateTimeOffset OccurredAt { get; private set; }
}

internal sealed class FirstConfirmationCommand
{
    private FirstConfirmationCommand() { }

    internal FirstConfirmationCommand(
        Guid idempotencyKey,
        Guid actorIdentityId,
        string intentContext,
        Guid resultIncorporationId)
    {
        IdempotencyKey = idempotencyKey;
        ActorIdentityId = actorIdentityId;
        IntentContext = intentContext;
        ResultIncorporationId = resultIncorporationId;
    }

    internal Guid IdempotencyKey { get; private set; }
    internal Guid? ActorIdentityId { get; private set; }
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
