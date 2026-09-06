using System.Net;

namespace NexoBar.OrderOperations.IntegrationTests;

[Collection(OrderOperationsApiCollection.Name)]
public sealed class DeliveryCorrectionIdempotencyTests(OrderOperationsApiFixture fixture)
{
    [Fact]
    public async Task Replay_preserves_original_result_after_other_delivery_freeze_and_revocation()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var target = await DeliveryQuantityTestSupport.CreateDirectAsync(fixture, 3, token);
        await DeliveryCorrectionTestSupport.DeliverAsync(fixture, target, 3, token);
        var key = Guid.NewGuid();
        using var response = await DeliveryCorrectionTestSupport.PostAsync(fixture.OrderOperationsClient, target, key, 1, token);
        var original = await DeliveryCorrectionTestSupport.SuccessAsync(response, token);
        await DeliveryCorrectionTestSupport.DeliverAsync(fixture, target, 1, token);
        using var liquidation = await LiquidationTestSupport.PostExternalAsync(fixture.OrderOperationsClient, target.OperationalReference, Guid.NewGuid(), token);
        await LiquidationTestSupport.ReadSuccessAsync(liquidation, token);
        await fixture.RevokeOrderOperationsAssignmentAsync(fixture.DefaultOrderOperationsActor.IdentityId, token);
        await using var connection = await ClosureTestSupport.OpenConnectionAsync(fixture, token);
        await using var blocker = await ClosureTestSupport.LockOrderAsync(connection, target.OperationalReference, token);
        using var replay = await DeliveryCorrectionTestSupport.PostAsync(fixture.OrderOperationsClient, target, key, 1, token).WaitAsync(TimeSpan.FromSeconds(10), token);
        Assert.Equal(original, await DeliveryCorrectionTestSupport.SuccessAsync(replay, token));
        using var newIntent = await DeliveryCorrectionTestSupport.PostAsync(fixture.OrderOperationsClient, target, Guid.NewGuid(), 1, token);
        await DeliveryCorrectionTestSupport.ProblemAsync(newIntent, HttpStatusCode.Forbidden, "forbidden", token);
        await DeliveryCorrectionTestSupport.CountsAsync(fixture, 1, token);
        Assert.Equal(3, Assert.Single(await fixture.ReadDeliveryStatesAsync(token)).DeliveredQuantity);
    }

    [Theory]
    [InlineData("actor")]
    [InlineData("order")]
    [InlineData("incorporation")]
    [InlineData("ordinal")]
    [InlineData("quantity")]
    public async Task Fingerprint_rejects_incompatible_actor_and_exact_intent(string change)
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var target = await DeliveryQuantityTestSupport.CreateDirectAsync(fixture, 3, token);
        await DeliveryCorrectionTestSupport.DeliverAsync(fixture, target, 3, token);
        var key = Guid.NewGuid();
        using var first = await DeliveryCorrectionTestSupport.PostAsync(fixture.OrderOperationsClient, target, key, 1, token);
        await DeliveryCorrectionTestSupport.SuccessAsync(first, token);
        var actor = change == "actor" ? await fixture.CreateDeliveryActorAsync(false, false, null, token) : fixture.DefaultOrderOperationsActor;
        using var client = await fixture.LoginAsync(actor, token);
        target = change switch
        {
            "order" => target with { OperationalReference = Guid.CreateVersion7().ToString() },
            "incorporation" => target with { IncorporationId = Guid.CreateVersion7() },
            "ordinal" => target with { ContentOrdinal = 2 },
            _ => target
        };
        using var response = await DeliveryCorrectionTestSupport.PostAsync(client, target, key, change == "quantity" ? 2 : 1, token);
        await DeliveryCorrectionTestSupport.ProblemAsync(response, HttpStatusCode.Conflict, "idempotency_key_conflict", token);
        await DeliveryCorrectionTestSupport.CountsAsync(fixture, 1, token);
    }

    [Theory]
    [InlineData("anonymous")]
    [InlineData("inactive")]
    [InlineData("revoked-session")]
    [InlineData("no-responsibility")]
    public async Task Authentication_and_current_authority_are_required(string kind)
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var target = await DeliveryQuantityTestSupport.CreateDirectAsync(fixture, 3, token);
        await DeliveryCorrectionTestSupport.DeliverAsync(fixture, target, 3, token);
        var key = Guid.NewGuid();
        using var first = await DeliveryCorrectionTestSupport.PostAsync(fixture.OrderOperationsClient, target, key, 1, token);
        await DeliveryCorrectionTestSupport.SuccessAsync(first, token);
        if (kind == "inactive") await fixture.SetIdentityActiveAsync(fixture.DefaultOrderOperationsActor.IdentityId, false, token);
        if (kind == "revoked-session") await fixture.RevokeSessionsAsync(fixture.DefaultOrderOperationsActor.IdentityId, token);
        if (kind == "no-responsibility") await fixture.RevokeOrderOperationsAssignmentAsync(fixture.DefaultOrderOperationsActor.IdentityId, token);
        var client = kind == "anonymous" ? fixture.Client : fixture.OrderOperationsClient;
        using var response = await DeliveryCorrectionTestSupport.PostAsync(client, target, kind == "no-responsibility" ? Guid.NewGuid() : key, 1, token);
        Assert.Equal(kind == "no-responsibility" ? HttpStatusCode.Forbidden : HttpStatusCode.Unauthorized, response.StatusCode);
        await DeliveryCorrectionTestSupport.CountsAsync(fixture, 1, token);
    }
}
