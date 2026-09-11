using System.Text.Json.Serialization;

namespace NexoBar.OrderOperations;

internal sealed class ContentAppliedPriceState
{
    private ContentAppliedPriceState() { }

    internal ContentAppliedPriceState(Guid incorporationId, int contentOrdinal, decimal effectiveAppliedPrice)
    {
        IncorporationId = incorporationId;
        ContentOrdinal = contentOrdinal;
        EffectiveAppliedPrice = effectiveAppliedPrice;
    }

    internal Guid IncorporationId { get; private set; }
    internal int ContentOrdinal { get; private set; }
    internal decimal EffectiveAppliedPrice { get; private set; }

    internal void Correct(decimal effectiveAppliedPrice)
    {
        if (effectiveAppliedPrice < 0) throw new InvalidOperationException("Effective AppliedPrice cannot be negative.");
        EffectiveAppliedPrice = effectiveAppliedPrice;
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ApplyCurrentCatalogPriceRequest;

internal sealed record AppliedPriceCorrectionResponse(
    Guid OrderId,
    Guid IncorporationId,
    int ContentOrdinal,
    Guid HistoryId,
    string PreviousEffectiveAppliedPrice,
    string ResultingEffectiveAppliedPrice,
    DateTimeOffset OccurredAt);

internal sealed record AppliedPriceCorrectionEvaluationResponse(
    Guid OrderId, Guid IncorporationId, int ContentOrdinal,
    string AppliedPrice, string EffectiveAppliedPrice, string? CurrentCatalogPrice,
    bool IsCorrectionAvailable, IReadOnlyList<string> Blockers);

internal sealed class AppliedPriceCorrectionHistory
{
    private AppliedPriceCorrectionHistory() { }

    internal AppliedPriceCorrectionHistory(AppliedPriceCorrectionResponse result, Guid actorIdentityId)
    {
        Id = result.HistoryId;
        OrderId = result.OrderId;
        IncorporationId = result.IncorporationId;
        ContentOrdinal = result.ContentOrdinal;
        PreviousEffectiveAppliedPrice = decimal.Parse(result.PreviousEffectiveAppliedPrice, System.Globalization.CultureInfo.InvariantCulture);
        ResultingEffectiveAppliedPrice = decimal.Parse(result.ResultingEffectiveAppliedPrice, System.Globalization.CultureInfo.InvariantCulture);
        ActorIdentityId = actorIdentityId;
        OccurredAt = result.OccurredAt;
    }

    internal Guid Id { get; private set; }
    internal string EventKind { get; private set; } = "AppliedPriceCorrected";
    internal Guid OrderId { get; private set; }
    internal Guid IncorporationId { get; private set; }
    internal int ContentOrdinal { get; private set; }
    internal decimal PreviousEffectiveAppliedPrice { get; private set; }
    internal decimal ResultingEffectiveAppliedPrice { get; private set; }
    internal Guid ActorIdentityId { get; private set; }
    internal DateTimeOffset OccurredAt { get; private set; }
}

internal sealed class AppliedPriceCorrectionCommand
{
    internal const string Kind = "ApplyCurrentCatalogPrice";
    private AppliedPriceCorrectionCommand() { }

    internal AppliedPriceCorrectionCommand(Guid key, Guid actorIdentityId, AppliedPriceCorrectionResponse result)
    {
        IdempotencyKey = key;
        ActorIdentityId = actorIdentityId;
        OrderId = result.OrderId;
        IncorporationId = result.IncorporationId;
        ContentOrdinal = result.ContentOrdinal;
        ResultHistoryId = result.HistoryId;
        ResultPreviousEffectiveAppliedPrice = decimal.Parse(result.PreviousEffectiveAppliedPrice, System.Globalization.CultureInfo.InvariantCulture);
        ResultEffectiveAppliedPrice = decimal.Parse(result.ResultingEffectiveAppliedPrice, System.Globalization.CultureInfo.InvariantCulture);
        ResultOccurredAt = result.OccurredAt;
    }

    internal Guid IdempotencyKey { get; private set; }
    internal Guid ActorIdentityId { get; private set; }
    internal string CommandKind { get; private set; } = Kind;
    internal Guid OrderId { get; private set; }
    internal Guid IncorporationId { get; private set; }
    internal int ContentOrdinal { get; private set; }
    internal Guid ResultHistoryId { get; private set; }
    internal decimal ResultPreviousEffectiveAppliedPrice { get; private set; }
    internal decimal ResultEffectiveAppliedPrice { get; private set; }
    internal DateTimeOffset ResultOccurredAt { get; private set; }

    internal bool Matches(Guid actor, Guid orderId, Guid incorporationId, int contentOrdinal) =>
        ActorIdentityId == actor && OrderId == orderId && IncorporationId == incorporationId &&
        ContentOrdinal == contentOrdinal && CommandKind == Kind;

    internal AppliedPriceCorrectionResponse ToResponse() => new(OrderId, IncorporationId, ContentOrdinal,
        ResultHistoryId,
        ResultPreviousEffectiveAppliedPrice.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ResultEffectiveAppliedPrice.ToString(System.Globalization.CultureInfo.InvariantCulture), ResultOccurredAt);
}
