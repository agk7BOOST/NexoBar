using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NexoBar.IdentitiesAndCapabilities;
using NexoBar.OperationalConfiguration;
using NexoBar.OrderOperations;

namespace NexoBar.OrderOperations.IntegrationTests;

[Collection(OrderOperationsApiCollection.Name)]
public sealed class ConfiguredContextFirstConfirmationTests(
    OrderOperationsApiFixture fixture)
{
    [Fact]
    public async Task Narrow_lookup_is_order_authorized_and_returns_only_id_and_name()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(cancellationToken);
        var contextId = await fixture.EnsureConfiguredTestContextAsync("Selector Context", cancellationToken);
        using var allowed = await fixture.LoginWithoutLegacyRequestAdapterAsync(
            fixture.DefaultOrderOperationsActor,
            cancellationToken);
        using var listed = await allowed.GetAsync(
            "/api/operational-configuration/order-contexts",
            cancellationToken);
        Assert.Equal(HttpStatusCode.OK, listed.StatusCode);
        using var document = await JsonDocument.ParseAsync(
            await listed.Content.ReadAsStreamAsync(cancellationToken),
            cancellationToken: cancellationToken);
        var entry = Assert.Single(document.RootElement.EnumerateArray().ToArray());
        Assert.Equal(contextId, entry.GetProperty("id").GetGuid());
        Assert.Equal("Selector Context", entry.GetProperty("operationalName").GetString());
        Assert.Equal(
            new[] { "id", "operationalName" },
            entry.EnumerateObject().Select(property => property.Name).Order().ToArray());

        var generalOnly = await fixture.CreateDeliveryActorAsync(
            hasOrderOperations: false,
            hasPreparation: false,
            enabledResponsibilityId: null,
            cancellationToken);
        await using (var scope = fixture.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<IdentitiesAndCapabilitiesDbContext>()
                .ResponsibilityAssignments.Add(new ResponsibilityAssignment(
                    generalOnly.IdentityId,
                    FunctionalResponsibility.GeneralConfiguration));
            await scope.ServiceProvider.GetRequiredService<IdentitiesAndCapabilitiesDbContext>()
                .SaveChangesAsync(cancellationToken);
        }
        using var forbidden = await fixture.LoginWithoutLegacyRequestAdapterAsync(
            generalOnly,
            cancellationToken);
        using var denied = await forbidden.GetAsync(
            "/api/operational-configuration/order-contexts",
            cancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);

        using var unauthorizedMutation = await PostAsync(
            forbidden,
            new FirstConfirmationRequest(
                contextId,
                null,
                [new FirstConfirmationItemRequest(Guid.NewGuid(), 1)]),
            Guid.NewGuid(),
            cancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, unauthorizedMutation.StatusCode);
    }

    [Fact]
    public async Task Configured_context_is_persisted_and_exact_replay_returns_original_order()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(cancellationToken);
        var contextId = await fixture.EnsureConfiguredTestContextAsync(
            "Mesa Context ID",
            cancellationToken);
        var product = await fixture.CreateProductAsync("Agua Context ID", "8.50", cancellationToken);
        using var client = await fixture.LoginWithoutLegacyRequestAdapterAsync(
            fixture.DefaultOrderOperationsActor,
            cancellationToken);
        var key = Guid.NewGuid();
        var request = new FirstConfirmationRequest(
            contextId,
            null,
            [new FirstConfirmationItemRequest(product.Id, 1)]);

        using var first = await PostAsync(client, request, key, cancellationToken);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var original = Assert.IsType<FirstConfirmationResponse>(
            await first.Content.ReadFromJsonAsync<FirstConfirmationResponse>(cancellationToken));
        Assert.Equal(contextId, original.ContextId);
        Assert.Equal("Mesa Context ID", original.Context);
        Assert.Equal("8.50", Assert.Single(original.FirstIncorporation.Items).AppliedPrice);

        using var replay = await PostAsync(client, request, key, cancellationToken);
        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        var replayed = Assert.IsType<FirstConfirmationResponse>(
            await replay.Content.ReadFromJsonAsync<FirstConfirmationResponse>(cancellationToken));
        Assert.Equal(original.OperationalReference, replayed.OperationalReference);
        Assert.Equal(original.ContextId, replayed.ContextId);
        Assert.Equal(original.FirstIncorporation.Id, replayed.FirstIncorporation.Id);

        await using var scope = fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderOperationsDbContext>();
        var orderId = Guid.Parse(original.OperationalReference);
        var order = await db.Orders.SingleAsync(item => item.Id == orderId, cancellationToken);
        Assert.Equal(contextId, order.CurrentContextId);
        Assert.Equal("Mesa Context ID", order.CurrentContextOperationalName);
        var history = await db.ConfirmationHistory.SingleAsync(
            item => item.IncorporationId == original.FirstIncorporation.Id,
            cancellationToken);
        Assert.Equal(contextId, history.ConfirmedContextId);
        Assert.Equal("Mesa Context ID", history.ConfirmedContext);
        var command = await db.FirstConfirmationCommands.SingleAsync(
            item => item.IdempotencyKey == key,
            cancellationToken);
        Assert.Equal(contextId, command.IntentContextId);
        Assert.Equal("Mesa Context ID", command.IntentContext);
        Assert.Equal(1, await db.Orders.CountAsync(cancellationToken));
        Assert.Equal(1, await db.Incorporations.CountAsync(cancellationToken));

        using var secondOrder = await PostAsync(client, request, Guid.NewGuid(), cancellationToken);
        Assert.Equal(HttpStatusCode.Created, secondOrder.StatusCode);
        var second = Assert.IsType<FirstConfirmationResponse>(
            await secondOrder.Content.ReadFromJsonAsync<FirstConfirmationResponse>(cancellationToken));
        Assert.NotEqual(original.OperationalReference, second.OperationalReference);
        Assert.Equal(contextId, second.ContextId);
        Assert.Equal(2, await db.Orders.CountAsync(cancellationToken));

        using var activeRead = await client.GetAsync(
            $"/api/order-operations/orders/{original.OperationalReference}",
            cancellationToken);
        Assert.Equal(HttpStatusCode.OK, activeRead.StatusCode);
        var active = Assert.IsType<OrderQueryResponse>(
            await activeRead.Content.ReadFromJsonAsync<OrderQueryResponse>(cancellationToken));
        Assert.Equal(contextId, active.ContextId);
        Assert.Equal("Mesa Context ID", active.Context);
    }

    [Fact]
    public async Task Unknown_id_and_fresh_free_text_are_rejected()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(cancellationToken);
        var product = await fixture.CreateProductAsync("Agua unknown Context", "5", cancellationToken);
        using var client = await fixture.LoginWithoutLegacyRequestAdapterAsync(
            fixture.DefaultOrderOperationsActor,
            cancellationToken);
        var items = new[] { new FirstConfirmationItemRequest(product.Id, 1) };

        using var unknown = await PostAsync(
            client,
            new FirstConfirmationRequest(Guid.CreateVersion7(), null, items),
            Guid.NewGuid(),
            cancellationToken);
        Assert.Equal(HttpStatusCode.Conflict, unknown.StatusCode);
        Assert.Equal(
            "order_operations.first_confirmation.context_not_current",
            await ReadCodeAsync(unknown, cancellationToken));

        using var freeTextRequest = new HttpRequestMessage(
            HttpMethod.Post,
            "/api/order-operations/first-confirmations")
        {
            Content = JsonContent.Create(new { context = "Unconfigured", items })
        };
        freeTextRequest.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("D"));
        using var freeText = await OrderOperationsApiFixture.SendWithAntiforgeryAsync(
            client,
            freeTextRequest,
            cancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, freeText.StatusCode);
        Assert.Equal(
            "order_operations.first_confirmation.context_id_required",
            await ReadCodeAsync(freeText, cancellationToken));
        Assert.Equal(PersistenceCounts.Empty, await fixture.CountEffectsAsync(cancellationToken));
    }

    private static async Task<HttpResponseMessage> PostAsync(
        HttpClient client,
        FirstConfirmationRequest requestBody,
        Guid idempotencyKey,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/api/order-operations/first-confirmations")
        {
            Content = JsonContent.Create(requestBody)
        };
        request.Headers.Add("Idempotency-Key", idempotencyKey.ToString("D"));
        return await OrderOperationsApiFixture.SendWithAntiforgeryAsync(
            client,
            request,
            cancellationToken);
    }

    private static async Task<string?> ReadCodeAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        using var document = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken),
            cancellationToken: cancellationToken);
        return document.RootElement.TryGetProperty("code", out var code)
            ? code.GetString()
            : null;
    }
}
