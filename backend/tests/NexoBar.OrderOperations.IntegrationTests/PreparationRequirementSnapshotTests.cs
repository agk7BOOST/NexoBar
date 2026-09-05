using System.Net;
using System.Net.Http.Json;
using NexoBar.OrderOperations;

namespace NexoBar.OrderOperations.IntegrationTests;

[Collection(OrderOperationsApiCollection.Name)]
public sealed class PreparationRequirementSnapshotTests(
    OrderOperationsApiFixture fixture)
{
    private readonly Dictionary<Guid, Guid> pendingCompositionByConfirmationKey = [];

    [Fact]
    public async Task First_confirmation_captures_direct_and_prepared_requirements()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var direct = await fixture.CreateProductAsync("Agua snapshot", "3", token);
        var prepared = await fixture.CreateProductAsync("Papas snapshot", "7", token);
        var responsibilityId = Guid.CreateVersion7();
        await fixture.SetProductPreparationAsync(
            prepared.Id,
            responsibilityId,
            token);

        using var response = await PostFirstAsync(
            [
                new FirstConfirmationItemRequest(direct.Id, 2),
                new FirstConfirmationItemRequest(prepared.Id, 3)
            ],
            Guid.NewGuid(),
            token);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var contents = await fixture.ReadConfirmedContentsAsync(token);
        Assert.False(contents.Single(content => content.ProductId == direct.Id)
            .RequiresPreparationAtConfirmation);
        Assert.True(contents.Single(content => content.ProductId == prepared.Id)
            .RequiresPreparationAtConfirmation);

        var work = Assert.Single(await fixture.ReadPreparationWorkAsync(token));
        Assert.Equal(prepared.Id, work.ProductId);
        Assert.Equal(responsibilityId, work.PreparationResponsibilityId);
        var deliveryStates = await fixture.ReadDeliveryStatesAsync(token);
        Assert.Equal(2, deliveryStates.Count);
        Assert.All(deliveryStates, state => Assert.Equal(0, state.DeliveredQuantity));
    }

    [Fact]
    public async Task Later_confirmations_capture_prospective_changes_and_responsibility_snapshots()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var product = await fixture.CreateProductAsync("Milanesa snapshot", "11", token);

        var first = await ConfirmFirstAsync(product.Id, Guid.NewGuid(), token);
        var responsibilityA = Guid.CreateVersion7();
        await fixture.SetProductPreparationAsync(product.Id, responsibilityA, token);
        await ConfirmSubsequentAsync(
            first.OperationalReference,
            product.Id,
            Guid.NewGuid(),
            token);

        var responsibilityB = Guid.CreateVersion7();
        await fixture.SetProductPreparationAsync(product.Id, responsibilityB, token);
        await ConfirmSubsequentAsync(
            first.OperationalReference,
            product.Id,
            Guid.NewGuid(),
            token);

        await fixture.SetProductPreparationAsync(product.Id, null, token);
        await ConfirmSubsequentAsync(
            first.OperationalReference,
            product.Id,
            Guid.NewGuid(),
            token);

        var contents = await fixture.ReadConfirmedContentsAsync(token);
        Assert.Equal(4, contents.Count);
        Assert.Equal([1, 2, 3, 4], contents.Select(content =>
            content.IncorporationOrdinal));
        Assert.Equal([false, true, true, false], contents.Select(content =>
            content.RequiresPreparationAtConfirmation));

        var works = await fixture.ReadPreparationWorkAsync(token);
        Assert.Equal(2, works.Count);
        Assert.Equal(
            responsibilityA,
            works.Single(work => work.IncorporationId == contents[1].IncorporationId)
                .PreparationResponsibilityId);
        Assert.Equal(
            responsibilityB,
            works.Single(work => work.IncorporationId == contents[2].IncorporationId)
                .PreparationResponsibilityId);
        Assert.Equal(4, await fixture.CountDeliveryStatesAsync(token));
    }

    [Fact]
    public async Task Replays_preserve_original_requirement_without_recreating_state()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var product = await fixture.CreateProductAsync("Tostado snapshot", "8", token);
        var firstKey = Guid.NewGuid();
        var first = await ConfirmFirstAsync(product.Id, firstKey, token);

        var responsibilityId = Guid.CreateVersion7();
        await fixture.SetProductPreparationAsync(product.Id, responsibilityId, token);
        var firstReplay = await ConfirmFirstAsync(product.Id, firstKey, token);
        Assert.Equal(first.OperationalReference, firstReplay.OperationalReference);

        var subsequentKey = Guid.NewGuid();
        await ConfirmSubsequentAsync(
            first.OperationalReference,
            product.Id,
            subsequentKey,
            token);
        await fixture.SetProductPreparationAsync(product.Id, null, token);
        await ConfirmSubsequentAsync(
            first.OperationalReference,
            product.Id,
            subsequentKey,
            token);

        var contents = await fixture.ReadConfirmedContentsAsync(token);
        Assert.Equal(2, contents.Count);
        Assert.Equal([false, true], contents.Select(content =>
            content.RequiresPreparationAtConfirmation));
        var work = Assert.Single(await fixture.ReadPreparationWorkAsync(token));
        Assert.Equal(responsibilityId, work.PreparationResponsibilityId);
        Assert.Equal(2, await fixture.CountDeliveryStatesAsync(token));
    }

    private async Task<FirstConfirmationResponse> ConfirmFirstAsync(
        Guid productId,
        Guid key,
        CancellationToken token)
    {
        using var response = await PostFirstAsync(
            [new FirstConfirmationItemRequest(productId, 1)],
            key,
            token);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return Assert.IsType<FirstConfirmationResponse>(
            await response.Content.ReadFromJsonAsync<FirstConfirmationResponse>(token));
    }

    private async Task<HttpResponseMessage> PostFirstAsync(
        IReadOnlyList<FirstConfirmationItemRequest> items,
        Guid key,
        CancellationToken token)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/api/order-operations/first-confirmations")
        {
            Content = JsonContent.Create(new FirstConfirmationRequest(
                "Mesa snapshot",
                items))
        };
        request.Headers.Add("Idempotency-Key", key.ToString("D"));
        return await OrderOperationsApiFixture.SendWithAntiforgeryAsync(
            fixture.OrderOperationsClient,
            request,
            token);
    }

    private async Task ConfirmSubsequentAsync(
        string operationalReference,
        Guid productId,
        Guid key,
        CancellationToken token)
    {
        if (!pendingCompositionByConfirmationKey.TryGetValue(
                key,
                out var pendingCompositionId))
        {
            pendingCompositionId = (await fixture.StartPendingCompositionAsync(
                operationalReference,
                token)).PendingCompositionId;
            pendingCompositionByConfirmationKey[key] = pendingCompositionId;
        }
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/order-operations/orders/{operationalReference}/confirmations")
        {
            Content = JsonContent.Create(new SubsequentConfirmationRequest(
                pendingCompositionId,
                [new SubsequentConfirmationItemRequest(productId, 1)]))
        };
        request.Headers.Add("Idempotency-Key", key.ToString("D"));
        using var response = await OrderOperationsApiFixture.SendWithAntiforgeryAsync(
            fixture.OrderOperationsClient,
            request,
            token);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }
}
