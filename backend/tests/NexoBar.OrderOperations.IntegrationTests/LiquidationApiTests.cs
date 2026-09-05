using System.Globalization;
using System.Net;
using System.Net.Http.Json;

namespace NexoBar.OrderOperations.IntegrationTests;

[Collection(OrderOperationsApiCollection.Name)]
public sealed class LiquidationApiTests(OrderOperationsApiFixture fixture)
{
    [Fact]
    public async Task Functional_amount_uses_current_deliveries_and_mixed_applied_prices()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var order = await LiquidationTestSupport.CreateDirectOrderAsync(
            fixture,
            [("1.25", 2), ("3.10", 3)],
            token);
        var orderId = Guid.Parse(order.OperationalReference);
        var contents = await fixture.ReadConfirmedContentsAsync(token);
        await fixture.SetDeliveredQuantityAsync(
            contents[0].IncorporationId,
            contents[0].ContentOrdinal,
            1,
            token);
        await fixture.SetDeliveredQuantityAsync(
            contents[1].IncorporationId,
            contents[1].ContentOrdinal,
            2,
            token);

        var read = await LiquidationTestSupport.ReadOrderAsync(
            fixture.Client,
            order.OperationalReference,
            token);

        var expected = contents[0].ProductId == order.FirstIncorporation.Items[0].ProductId
            ? 1.25m + (2 * 3.10m)
            : 3.10m + (2 * 1.25m);
        Assert.Equal(expected, decimal.Parse(read.FunctionalAmount, CultureInfo.InvariantCulture));
        Assert.False(read.IsLiquidationEligible);
        Assert.Contains(
            LiquidationEligibilityBlockers.UnresolvedFulfillment,
            read.LiquidationBlockers);
        Assert.False(read.IsFrozen);
        Assert.Equal(orderId.ToString("D"), read.OperationalReference);
    }

    [Fact]
    public async Task Prepared_but_undelivered_content_is_excluded()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var created = await fixture.CreatePreparedWorkAsync(
            Guid.CreateVersion7(),
            token,
            quantity: 2);
        var work = Assert.Single(await fixture.ReadPreparationWorkAsync(token));
        await fixture.SetPreparationQuantitiesAsync(work.Id, 0, 0, 2, token);

        var read = await LiquidationTestSupport.ReadOrderAsync(
            fixture.Client,
            created.Confirmation.OperationalReference,
            token);

        Assert.Equal("0", read.FunctionalAmount);
        Assert.False(read.IsLiquidationEligible);
        Assert.Equal(
            [LiquidationEligibilityBlockers.UnresolvedFulfillment],
            read.LiquidationBlockers);
    }

    [Fact]
    public async Task Pending_composition_blocks_liquidation()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var order = await CreateFullyDeliveredOrderAsync(token);
        await fixture.StartPendingCompositionAsync(order.OperationalReference, token);

        using var response = await LiquidationTestSupport.PostSimpleAsync(
            fixture.OrderOperationsClient,
            order.OperationalReference,
            Guid.NewGuid(),
            "Cash",
            token);

        await LiquidationTestSupport.AssertProblemAsync(
            response,
            HttpStatusCode.Conflict,
            "order_operations.liquidation.pending_composition",
            token);
        var read = await LiquidationTestSupport.ReadOrderAsync(
            fixture.Client,
            order.OperationalReference,
            token);
        Assert.Contains(
            LiquidationEligibilityBlockers.PendingComposition,
            read.LiquidationBlockers);
    }

    [Fact]
    public async Task Unresolved_delivery_blocks_liquidation()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var order = await LiquidationTestSupport.CreateDirectOrderAsync(
            fixture,
            [("5", 2)],
            token);

        using var response = await LiquidationTestSupport.PostExternalAsync(
            fixture.OrderOperationsClient,
            order.OperationalReference,
            Guid.NewGuid(),
            token);

        await LiquidationTestSupport.AssertProblemAsync(
            response,
            HttpStatusCode.Conflict,
            "order_operations.liquidation.unresolved_fulfillment",
            token);
        Assert.Empty(await fixture.ReadLiquidationsAsync(token));
    }

    [Fact]
    public async Task Simple_liquidation_trims_outer_whitespace_and_preserves_declared_text()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var order = await CreateFullyDeliveredOrderAsync(token, price: "2.35", quantity: 4);
        var eligible = await LiquidationTestSupport.ReadOrderAsync(
            fixture.Client,
            order.OperationalReference,
            token);
        Assert.True(eligible.IsLiquidationEligible);
        Assert.Empty(eligible.LiquidationBlockers);
        Assert.Equal("9.40", eligible.FunctionalAmount);

        using var response = await LiquidationTestSupport.PostSimpleAsync(
            fixture.OrderOperationsClient,
            order.OperationalReference,
            Guid.NewGuid(),
            "  Mi Tarjeta / QR  ",
            token);
        var result = await LiquidationTestSupport.ReadSuccessAsync(response, token);

        Assert.Equal(LiquidationModes.Simple, result.Mode);
        Assert.Equal("9.40", result.FunctionalAmount);
        Assert.Equal("Mi Tarjeta / QR", result.DeclaredPaymentMedium);
        Assert.True(result.IsFrozen);
        var state = Assert.Single(await fixture.ReadLiquidationsAsync(token));
        Assert.Equal(fixture.DefaultOrderOperationsActor.IdentityId, state.ActorIdentityId);
        Assert.Equal(result.LiquidationId, state.Id);
        var history = Assert.Single(await fixture.ReadLiquidationHistoryAsync(token));
        Assert.Equal(LiquidationHistory.LiquidatedEventKind, history.EventKind);
        Assert.Equal(state.ActorIdentityId, history.ActorIdentityId);
        Assert.Equal(state.FunctionalAmount, history.FunctionalAmount);
        Assert.Single(await fixture.ReadLiquidationCommandsAsync(token));
    }

    [Fact]
    public async Task External_collection_has_no_declared_medium()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var order = await CreateFullyDeliveredOrderAsync(token);

        using var response = await LiquidationTestSupport.PostExternalAsync(
            fixture.OrderOperationsClient,
            order.OperationalReference,
            Guid.NewGuid(),
            token);
        var result = await LiquidationTestSupport.ReadSuccessAsync(response, token);

        Assert.Equal(LiquidationModes.ExternalCollection, result.Mode);
        Assert.Null(result.DeclaredPaymentMedium);
        Assert.Null(Assert.Single(
            await fixture.ReadLiquidationsAsync(token)).DeclaredPaymentMedium);
    }

    [Fact]
    public async Task New_liquidation_requires_usable_session_active_identity_and_responsibility()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var order = await CreateFullyDeliveredOrderAsync(token);

        using (var anonymous = await LiquidationTestSupport.PostExternalAsync(
                   fixture.Client,
                   order.OperationalReference,
                   Guid.NewGuid(),
                   token))
        {
            await LiquidationTestSupport.AssertProblemAsync(
                anonymous,
                HttpStatusCode.Unauthorized,
                "identities_and_capabilities.authentication_required",
                token);
        }

        var unauthorizedActor = await fixture.CreateDeliveryActorAsync(
            hasOrderOperations: false,
            hasPreparation: false,
            enabledResponsibilityId: null,
            token);
        using (var unauthorizedClient = await fixture.LoginAsync(unauthorizedActor, token))
        using (var forbidden = await LiquidationTestSupport.PostExternalAsync(
                   unauthorizedClient,
                   order.OperationalReference,
                   Guid.NewGuid(),
                   token))
        {
            await LiquidationTestSupport.AssertProblemAsync(
                forbidden,
                HttpStatusCode.Forbidden,
                "order_operations.liquidation.forbidden",
                token);
        }

        await fixture.SetIdentityActiveAsync(
            fixture.DefaultOrderOperationsActor.IdentityId,
            false,
            token);
        using var inactive = await LiquidationTestSupport.PostExternalAsync(
            fixture.OrderOperationsClient,
            order.OperationalReference,
            Guid.NewGuid(),
            token);
        await LiquidationTestSupport.AssertProblemAsync(
            inactive,
            HttpStatusCode.Unauthorized,
            "identities_and_capabilities.authentication_required",
            token);
        Assert.Empty(await fixture.ReadLiquidationsAsync(token));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Empty_declared_medium_is_invalid(string? medium)
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var order = await CreateFullyDeliveredOrderAsync(token);

        using var response = await LiquidationTestSupport.PostSimpleAsync(
            fixture.OrderOperationsClient,
            order.OperationalReference,
            Guid.NewGuid(),
            medium,
            token);

        await LiquidationTestSupport.AssertProblemAsync(
            response,
            HttpStatusCode.BadRequest,
            "order_operations.liquidation.declared_payment_medium_invalid",
            token);
        Assert.Empty(await fixture.ReadLiquidationsAsync(token));
    }

    [Fact]
    public async Task Medium_over_technical_limit_and_external_financial_fields_are_rejected()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var order = await CreateFullyDeliveredOrderAsync(token);

        using var tooLong = await LiquidationTestSupport.PostSimpleAsync(
            fixture.OrderOperationsClient,
            order.OperationalReference,
            Guid.NewGuid(),
            new string('x', LiquidationService.DeclaredPaymentMediumMaxLength + 1),
            token);
        await LiquidationTestSupport.AssertProblemAsync(
            tooLong,
            HttpStatusCode.BadRequest,
            "order_operations.liquidation.declared_payment_medium_invalid",
            token);

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/order-operations/orders/{order.OperationalReference}/record-external-collection")
        {
            Content = JsonContent.Create(new { declaredPaymentMedium = "Cash" })
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("D"));
        using var external = await OrderOperationsApiFixture.SendWithAntiforgeryAsync(
            fixture.OrderOperationsClient,
            request,
            token);
        Assert.Equal(HttpStatusCode.BadRequest, external.StatusCode);
        Assert.Empty(await fixture.ReadLiquidationsAsync(token));
    }

    [Fact]
    public async Task Replay_returns_original_amount_after_responsibility_revocation()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var order = await CreateFullyDeliveredOrderAsync(token, price: "4.25", quantity: 2);
        var key = Guid.NewGuid();
        using var firstResponse = await LiquidationTestSupport.PostSimpleAsync(
            fixture.OrderOperationsClient,
            order.OperationalReference,
            key,
            "Cuenta corriente",
            token);
        var first = await LiquidationTestSupport.ReadSuccessAsync(firstResponse, token);
        await fixture.RevokeOrderOperationsAssignmentAsync(
            fixture.DefaultOrderOperationsActor.IdentityId,
            token);

        using var replayResponse = await LiquidationTestSupport.PostSimpleAsync(
            fixture.OrderOperationsClient,
            order.OperationalReference,
            key,
            "Cuenta corriente",
            token);
        var replay = await LiquidationTestSupport.ReadSuccessAsync(replayResponse, token);
        Assert.Equal(first, replay);
        Assert.Equal("8.50", replay.FunctionalAmount);

        using var newIntent = await LiquidationTestSupport.PostSimpleAsync(
            fixture.OrderOperationsClient,
            order.OperationalReference,
            Guid.NewGuid(),
            "Cuenta corriente",
            token);
        await LiquidationTestSupport.AssertProblemAsync(
            newIntent,
            HttpStatusCode.Forbidden,
            "order_operations.liquidation.forbidden",
            token);
        Assert.Single(await fixture.ReadLiquidationsAsync(token));
        Assert.Single(await fixture.ReadLiquidationHistoryAsync(token));
        Assert.Single(await fixture.ReadLiquidationCommandsAsync(token));
    }

    [Fact]
    public async Task Failure_writing_history_rolls_back_state_history_and_command()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var order = await CreateFullyDeliveredOrderAsync(token);
        await fixture.SetLiquidationHistoryFailureAsync(true, token);
        try
        {
            using var response = await LiquidationTestSupport.PostExternalAsync(
                fixture.OrderOperationsClient,
                order.OperationalReference,
                Guid.NewGuid(),
                token);
            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        }
        finally
        {
            await fixture.SetLiquidationHistoryFailureAsync(false, token);
        }

        Assert.Empty(await fixture.ReadLiquidationsAsync(token));
        Assert.Empty(await fixture.ReadLiquidationHistoryAsync(token));
        Assert.Empty(await fixture.ReadLiquidationCommandsAsync(token));
    }

    [Fact]
    public async Task Freeze_rejects_subsequent_confirmation_and_delivery()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var order = await CreateFullyDeliveredOrderAsync(token);
        using (var response = await LiquidationTestSupport.PostExternalAsync(
                   fixture.OrderOperationsClient,
                   order.OperationalReference,
                   Guid.NewGuid(),
                   token))
        {
            await LiquidationTestSupport.ReadSuccessAsync(response, token);
        }

        var content = Assert.Single(await fixture.ReadConfirmedContentsAsync(token));
        using var delivery = await DeliveryQuantityTestSupport.PostAsync(
            fixture.OrderOperationsClient,
            content.IncorporationId,
            content.ContentOrdinal,
            Guid.NewGuid(),
            1,
            token);
        await LiquidationTestSupport.AssertProblemAsync(
            delivery,
            HttpStatusCode.Conflict,
            "order_operations.order.frozen",
            token);

        using var confirmationRequest = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/order-operations/orders/{order.OperationalReference}/confirmations")
        {
            Content = JsonContent.Create(new SubsequentConfirmationRequest(
                Guid.CreateVersion7(),
                [new SubsequentConfirmationItemRequest(content.ProductId, 1)]))
        };
        confirmationRequest.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("D"));
        using var confirmation = await OrderOperationsApiFixture.SendWithAntiforgeryAsync(
            fixture.OrderOperationsClient,
            confirmationRequest,
            token);
        await LiquidationTestSupport.AssertProblemAsync(
            confirmation,
            HttpStatusCode.Conflict,
            "order_operations.order.frozen",
            token);

        var read = await LiquidationTestSupport.ReadOrderAsync(
            fixture.Client,
            order.OperationalReference,
            token);
        Assert.True(read.IsLiquidated);
        Assert.True(read.IsFrozen);
        Assert.False(read.IsLiquidationEligible);
        Assert.Equal(LiquidationModes.ExternalCollection, read.LiquidationMode);
        Assert.Equal("10", read.LiquidatedAmount);
    }

    [Fact]
    public async Task Freeze_rejects_pending_composition_and_preparation_progress()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var responsibilityId = Guid.CreateVersion7();
        var created = await fixture.CreatePreparedWorkAsync(
            responsibilityId,
            token,
            quantity: 1);
        var work = Assert.Single(await fixture.ReadPreparationWorkAsync(token));
        await fixture.SetPreparationQuantitiesAsync(work.Id, 0, 0, 1, token);
        await fixture.SetAllDeliveredQuantitiesAsync(
            Guid.Parse(created.Confirmation.OperationalReference),
            token);
        var preparer = await fixture.CreatePreparationActorAsync(
            hasPreparation: true,
            responsibilityId,
            token);
        using var preparationClient = await fixture.LoginAsync(preparer, token);
        using (var response = await LiquidationTestSupport.PostExternalAsync(
                   fixture.OrderOperationsClient,
                   created.Confirmation.OperationalReference,
                   Guid.NewGuid(),
                   token))
        {
            await LiquidationTestSupport.ReadSuccessAsync(response, token);
        }

        using var pendingRequest = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/orders/{created.Confirmation.OperationalReference}/pending-composition");
        pendingRequest.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("D"));
        using var pending = await OrderOperationsApiFixture.SendWithAntiforgeryAsync(
            fixture.OrderOperationsClient,
            pendingRequest,
            token);
        await LiquidationTestSupport.AssertProblemAsync(
            pending,
            HttpStatusCode.Conflict,
            "order_operations.order.frozen",
            token);

        using var preparation = await PreparationStartTestSupport.PostAsync(
            preparationClient,
            work.Id,
            Guid.NewGuid(),
            1,
            token);
        await LiquidationTestSupport.AssertProblemAsync(
            preparation,
            HttpStatusCode.Conflict,
            "order_operations.order.frozen",
            token);
    }

    private async Task<FirstConfirmationResponse> CreateFullyDeliveredOrderAsync(
        CancellationToken cancellationToken,
        string price = "5",
        int quantity = 2)
    {
        var order = await LiquidationTestSupport.CreateDirectOrderAsync(
            fixture,
            [(price, quantity)],
            cancellationToken);
        await fixture.SetAllDeliveredQuantitiesAsync(
            Guid.Parse(order.OperationalReference),
            cancellationToken);
        return order;
    }
}
