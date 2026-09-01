using System.Net;

namespace NexoBar.OrderOperations.IntegrationTests;

[Collection(OrderOperationsApiCollection.Name)]
public sealed class DeliveryQuantityIdempotencyTests(OrderOperationsApiFixture fixture)
{
    [Fact]
    public async Task Exact_replay_returns_original_result_without_second_effect()
    {
        var token = TestContext.Current.CancellationToken;
        var scenario = await CreateScenarioAsync(token);
        using var client = scenario.Client;
        var key = Guid.NewGuid();
        using var firstResponse = await DeliveryQuantityTestSupport.PostAsync(
            client,
            scenario.Target.IncorporationId,
            scenario.Target.ContentOrdinal,
            key,
            1,
            token);
        var first = await DeliveryQuantityTestSupport.ReadSuccessAsync(firstResponse, token);

        using var laterResponse = await DeliveryQuantityTestSupport.PostAsync(
            client,
            scenario.Target.IncorporationId,
            scenario.Target.ContentOrdinal,
            Guid.NewGuid(),
            1,
            token);
        var later = await DeliveryQuantityTestSupport.ReadSuccessAsync(laterResponse, token);
        using var replayResponse = await DeliveryQuantityTestSupport.PostAsync(
            client,
            scenario.Target.IncorporationId,
            scenario.Target.ContentOrdinal,
            key,
            1,
            token);
        var replay = await DeliveryQuantityTestSupport.ReadSuccessAsync(replayResponse, token);

        Assert.Equal(first, replay);
        Assert.Equal(1, replay.DeliveredQuantity);
        Assert.Equal(2, later.DeliveredQuantity);
        Assert.Equal(2, Assert.Single(await fixture.ReadDeliveryStatesAsync(token))
            .DeliveredQuantity);
        Assert.Equal(2, (await fixture.ReadDeliveryHistoryAsync(token)).Count);
        Assert.Equal(2, (await fixture.ReadDeliveryCommandsAsync(token)).Count);
    }

    [Theory]
    [InlineData(IdempotencyConflictKind.Quantity)]
    [InlineData(IdempotencyConflictKind.Target)]
    public async Task Same_actor_and_key_with_incompatible_intent_is_conflict(
        IdempotencyConflictKind conflictKind)
    {
        var token = TestContext.Current.CancellationToken;
        var scenario = await CreateScenarioAsync(token);
        using var client = scenario.Client;
        var key = Guid.NewGuid();
        using var firstResponse = await DeliveryQuantityTestSupport.PostAsync(
            client,
            scenario.Target.IncorporationId,
            scenario.Target.ContentOrdinal,
            key,
            1,
            token);
        await DeliveryQuantityTestSupport.ReadSuccessAsync(firstResponse, token);

        var incompatibleTarget = conflictKind == IdempotencyConflictKind.Target
            ? await CreateAdditionalTargetAsync(scenario.Target, token)
            : scenario.Target;
        var quantity = conflictKind == IdempotencyConflictKind.Quantity ? 2 : 1;
        using var conflict = await DeliveryQuantityTestSupport.PostAsync(
            client,
            incompatibleTarget.IncorporationId,
            incompatibleTarget.ContentOrdinal,
            key,
            quantity,
            token);

        await AssertConflictAsync(conflict, token);
        await AssertSingleEffectAsync(1, token);
    }

    [Fact]
    public async Task Same_key_from_other_identity_is_conflict_before_capability_check()
    {
        var token = TestContext.Current.CancellationToken;
        var scenario = await CreateScenarioAsync(token);
        using var firstClient = scenario.Client;
        var key = Guid.NewGuid();
        using var firstResponse = await DeliveryQuantityTestSupport.PostAsync(
            firstClient,
            scenario.Target.IncorporationId,
            scenario.Target.ContentOrdinal,
            key,
            1,
            token);
        await DeliveryQuantityTestSupport.ReadSuccessAsync(firstResponse, token);
        var other = await fixture.CreateDeliveryActorAsync(false, false, null, token);
        using var otherClient = await fixture.LoginAsync(other, token);

        using var conflict = await DeliveryQuantityTestSupport.PostAsync(
            otherClient,
            scenario.Target.IncorporationId,
            scenario.Target.ContentOrdinal,
            key,
            1,
            token);

        await AssertConflictAsync(conflict, token);
        await AssertSingleEffectAsync(1, token);
    }

    [Fact]
    public async Task Capability_revocation_allows_replay_but_forbids_new_key()
    {
        var token = TestContext.Current.CancellationToken;
        var scenario = await CreateScenarioAsync(token);
        using var client = scenario.Client;
        var key = Guid.NewGuid();
        using var firstResponse = await DeliveryQuantityTestSupport.PostAsync(
            client,
            scenario.Target.IncorporationId,
            scenario.Target.ContentOrdinal,
            key,
            1,
            token);
        var first = await DeliveryQuantityTestSupport.ReadSuccessAsync(firstResponse, token);
        await fixture.RevokeOrderOperationsAssignmentAsync(scenario.Actor.IdentityId, token);

        using var replayResponse = await DeliveryQuantityTestSupport.PostAsync(
            client,
            scenario.Target.IncorporationId,
            scenario.Target.ContentOrdinal,
            key,
            1,
            token);
        var replay = await DeliveryQuantityTestSupport.ReadSuccessAsync(replayResponse, token);
        using var newIntent = await DeliveryQuantityTestSupport.PostAsync(
            client,
            scenario.Target.IncorporationId,
            scenario.Target.ContentOrdinal,
            Guid.NewGuid(),
            1,
            token);

        Assert.Equal(first, replay);
        await DeliveryQuantityTestSupport.AssertProblemAsync(
            newIntent, HttpStatusCode.Forbidden, "order_operations.delivery.forbidden", token);
        await AssertSingleEffectAsync(1, token);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Invalid_identity_or_session_blocks_replay(bool deactivateIdentity)
    {
        var token = TestContext.Current.CancellationToken;
        var scenario = await CreateScenarioAsync(token);
        using var client = scenario.Client;
        var key = Guid.NewGuid();
        using var firstResponse = await DeliveryQuantityTestSupport.PostAsync(
            client,
            scenario.Target.IncorporationId,
            scenario.Target.ContentOrdinal,
            key,
            1,
            token);
        await DeliveryQuantityTestSupport.ReadSuccessAsync(firstResponse, token);
        if (deactivateIdentity)
        {
            await fixture.SetIdentityActiveAsync(scenario.Actor.IdentityId, false, token);
        }
        else
        {
            await fixture.RevokeSessionsAsync(scenario.Actor.IdentityId, token);
        }

        using var replay = await DeliveryQuantityTestSupport.PostAsync(
            client,
            scenario.Target.IncorporationId,
            scenario.Target.ContentOrdinal,
            key,
            1,
            token);

        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
        await AssertSingleEffectAsync(1, token);
    }

    private async Task<DeliveryScenario> CreateScenarioAsync(CancellationToken token)
    {
        await fixture.ResetAsync(token);
        var target = await DeliveryQuantityTestSupport.CreateDirectAsync(fixture, 5, token);
        var actor = await fixture.CreateDeliveryActorAsync(true, false, null, token);
        return new DeliveryScenario(target, actor, await fixture.LoginAsync(actor, token));
    }

    private async Task AssertSingleEffectAsync(int delivered, CancellationToken token)
    {
        Assert.Equal(delivered, (await fixture.ReadDeliveryStatesAsync(token))
            .Sum(state => state.DeliveredQuantity));
        Assert.Single(await fixture.ReadDeliveryHistoryAsync(token));
        Assert.Single(await fixture.ReadDeliveryCommandsAsync(token));
    }

    private async Task<DeliveryTarget> CreateAdditionalTargetAsync(
        DeliveryTarget firstTarget,
        CancellationToken token)
    {
        var product = await fixture.CreateProductAsync("Otro directo", "4", token);
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/order-operations/orders/{firstTarget.OperationalReference}/confirmations")
        {
            Content = System.Net.Http.Json.JsonContent.Create(
                new SubsequentConfirmationRequest(
                    [new SubsequentConfirmationItemRequest(product.Id, 2)]))
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("D"));
        using var response = await fixture.Client.SendAsync(request, token);
        response.EnsureSuccessStatusCode();
        var content = (await fixture.ReadConfirmedContentsAsync(token))
            .Single(candidate => candidate.IncorporationId != firstTarget.IncorporationId);
        return new DeliveryTarget(
            firstTarget.OperationalReference,
            content.IncorporationId,
            content.ContentOrdinal,
            null);
    }

    private static Task AssertConflictAsync(
        HttpResponseMessage response,
        CancellationToken token) =>
        DeliveryQuantityTestSupport.AssertProblemAsync(
            response,
            HttpStatusCode.Conflict,
            "order_operations.delivery.idempotency_key_conflict",
            token);

    private sealed record DeliveryScenario(
        DeliveryTarget Target,
        PreparationActor Actor,
        HttpClient Client);
}

public enum IdempotencyConflictKind
{
    Quantity,
    Target
}
