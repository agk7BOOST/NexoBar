namespace NexoBar.OrderOperations;

internal sealed class DeliveryHistory
{
    internal const string QuantityDeliveredEventKind = "QuantityDelivered";

    private DeliveryHistory() { }

    internal DeliveryHistory(
        Guid id,
        Guid incorporationId,
        int contentOrdinal,
        int quantity,
        Guid actorIdentityId,
        DateTimeOffset occurredAt,
        int resultingDeliveredQuantity)
    {
        Id = id;
        IncorporationId = incorporationId;
        ContentOrdinal = contentOrdinal;
        EventKind = QuantityDeliveredEventKind;
        Quantity = quantity;
        ActorIdentityId = actorIdentityId;
        OccurredAt = occurredAt;
        ResultingDeliveredQuantity = resultingDeliveredQuantity;
    }

    internal Guid Id { get; private set; }
    internal Guid IncorporationId { get; private set; }
    internal int ContentOrdinal { get; private set; }
    internal string EventKind { get; private set; } = string.Empty;
    internal int Quantity { get; private set; }
    internal Guid ActorIdentityId { get; private set; }
    internal DateTimeOffset OccurredAt { get; private set; }
    internal int ResultingDeliveredQuantity { get; private set; }
}

internal sealed class DeliveryCommand
{
    internal const string DeliverQuantityCommandKind = "DeliverQuantity";

    private DeliveryCommand() { }

    internal DeliveryCommand(
        Guid idempotencyKey,
        Guid actorIdentityId,
        Guid incorporationId,
        int contentOrdinal,
        int quantity,
        DeliveryCommandResult result)
    {
        IdempotencyKey = idempotencyKey;
        ActorIdentityId = actorIdentityId;
        CommandKind = DeliverQuantityCommandKind;
        IncorporationId = incorporationId;
        ContentOrdinal = contentOrdinal;
        Quantity = quantity;
        ResultHistoryId = result.HistoryId;
        ResultOccurredAt = result.OccurredAt;
        ResultDeliveredQuantity = result.DeliveredQuantity;
    }

    internal Guid IdempotencyKey { get; private set; }
    internal Guid ActorIdentityId { get; private set; }
    internal string CommandKind { get; private set; } = string.Empty;
    internal Guid IncorporationId { get; private set; }
    internal int ContentOrdinal { get; private set; }
    internal int Quantity { get; private set; }
    internal Guid ResultHistoryId { get; private set; }
    internal DateTimeOffset ResultOccurredAt { get; private set; }
    internal int ResultDeliveredQuantity { get; private set; }

    internal bool Matches(
        Guid actorIdentityId,
        Guid incorporationId,
        int contentOrdinal,
        int quantity) =>
        ActorIdentityId == actorIdentityId &&
        string.Equals(
            CommandKind,
            DeliverQuantityCommandKind,
            StringComparison.Ordinal) &&
        IncorporationId == incorporationId &&
        ContentOrdinal == contentOrdinal &&
        Quantity == quantity;

    internal DeliveryCommandResult ToResult() => new(
        IncorporationId,
        ContentOrdinal,
        ResultHistoryId,
        ResultOccurredAt,
        ResultDeliveredQuantity);
}

internal sealed record DeliveryCommandResult(
    Guid IncorporationId,
    int ContentOrdinal,
    Guid HistoryId,
    DateTimeOffset OccurredAt,
    int DeliveredQuantity);
