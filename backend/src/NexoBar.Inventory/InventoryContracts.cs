using System.Text.Json.Serialization;

namespace NexoBar.Inventory;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record CreateInventoryItemRequest(
    string? OperationalName,
    string? OperationalUnit);

public sealed record InventoryItemResponse(
    Guid ItemId,
    string OperationalName,
    string OperationalUnit,
    string? CurrentRegisteredQuantity,
    long MovementRevision);

public sealed record InventoryConfigurationItemResponse(
    Guid ItemId,
    string OperationalName,
    string OperationalUnit);

public sealed record InventoryOperationalItemResponse(
    Guid ItemId,
    string OperationalName,
    string OperationalUnit,
    string? CurrentRegisteredQuantity,
    bool QuantityEstablished,
    bool HasNegativeBalanceInconsistency,
    long AsOfMovementRevision);
