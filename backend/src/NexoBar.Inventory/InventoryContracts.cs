using System.Text.Json.Serialization;

namespace NexoBar.Inventory;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record CreateInventoryItemRequest(
    string? OperationalName,
    string? OperationalUnit);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record RecordInventoryCountRequest(string? ObservedQuantity);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ReconcileInventoryCountRequest(Guid CountObservationId);

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

public sealed record CountObservationResponse(
    Guid CountObservationId,
    Guid ItemId,
    string ObservedQuantity,
    long ObservedMovementRevision,
    string ObservedOperationalUnit,
    DateTimeOffset ObservedAt);

public sealed record ReconcileInventoryCountResponse(
    Guid ItemId,
    Guid CountObservationId,
    string Outcome,
    Guid? MovementId,
    DateTimeOffset? OccurredAt,
    string? PreviousRegisteredQuantity,
    string ObservedQuantity,
    string? Difference,
    string ResultingRegisteredQuantity,
    long MovementRevision);
