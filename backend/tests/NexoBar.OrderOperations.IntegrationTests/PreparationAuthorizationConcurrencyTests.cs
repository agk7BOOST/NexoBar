using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NexoBar.Catalog;

namespace NexoBar.OrderOperations.IntegrationTests;

[Collection(OrderOperationsApiCollection.Name)]
public sealed class PreparationAuthorizationConcurrencyTests(
    OrderOperationsApiFixture fixture)
{
    [Fact]
    public async Task Authorization_locks_first_so_enablement_revocation_waits_for_read()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var responsibility = Guid.CreateVersion7();
        await fixture.CreatePreparedWorkAsync(responsibility, token);
        var actor = await fixture.CreatePreparationActorAsync(
            hasPreparation: true,
            responsibility,
            token);
        var lookupReached = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseLookup = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var application = fixture.CreateApplicationWithProductLookupDecorator(
            services => new BlockingProductOperationalReferenceLookup(
                new ProductOperationalReferenceLookup(
                    services.GetRequiredService<CatalogDbContext>()),
                lookupReached,
                releaseLookup));
        using var client = await fixture.LoginAsync(actor, token, application);

        var readTask = client.GetAsync(WorkUrl(responsibility), token);
        await lookupReached.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
        var revokeTask = fixture.RevokePreparationEnablementAsync(
            actor.IdentityId,
            responsibility,
            token);
        try
        {
            Assert.True(await fixture.WaitForIdentityMutationLockAsync(
                "preparation_enablements",
                TimeSpan.FromSeconds(10),
                token));
            Assert.False(revokeTask.IsCompleted);

            releaseLookup.TrySetResult();
            using var response = await readTask;
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            await revokeTask;
        }
        finally
        {
            releaseLookup.TrySetResult();
            await ObserveAsync(readTask);
            await ObserveAsync(revokeTask);
        }
    }

    [Fact]
    public async Task Enablement_revocation_committed_first_makes_new_read_forbidden()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var responsibility = Guid.CreateVersion7();
        await fixture.CreatePreparedWorkAsync(responsibility, token);
        var actor = await fixture.CreatePreparationActorAsync(true, responsibility, token);
        using var client = await fixture.LoginAsync(actor, token);
        await fixture.RevokePreparationEnablementAsync(
            actor.IdentityId,
            responsibility,
            token);

        using var response = await client.GetAsync(WorkUrl(responsibility), token);

        await AssertProblemAsync(response, HttpStatusCode.Forbidden, token);
    }

    [Fact]
    public async Task Preparation_revocation_committed_first_makes_new_read_forbidden()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var responsibility = Guid.CreateVersion7();
        await fixture.CreatePreparedWorkAsync(responsibility, token);
        var actor = await fixture.CreatePreparationActorAsync(true, responsibility, token);
        using var client = await fixture.LoginAsync(actor, token);
        await fixture.RevokePreparationAssignmentAsync(actor.IdentityId, token);

        using var response = await client.GetAsync(WorkUrl(responsibility), token);

        await AssertProblemAsync(response, HttpStatusCode.Forbidden, token);
    }

    [Fact]
    public async Task Identity_deactivation_committed_first_makes_new_read_unauthorized()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var responsibility = Guid.CreateVersion7();
        await fixture.CreatePreparedWorkAsync(responsibility, token);
        var actor = await fixture.CreatePreparationActorAsync(true, responsibility, token);
        using var client = await fixture.LoginAsync(actor, token);
        await fixture.SetIdentityActiveAsync(actor.IdentityId, false, token);

        using var response = await client.GetAsync(WorkUrl(responsibility), token);

        await AssertProblemAsync(response, HttpStatusCode.Unauthorized, token);
    }

    [Fact]
    public async Task Session_revocation_committed_first_makes_new_read_unauthorized()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var responsibility = Guid.CreateVersion7();
        await fixture.CreatePreparedWorkAsync(responsibility, token);
        var actor = await fixture.CreatePreparationActorAsync(true, responsibility, token);
        using var client = await fixture.LoginAsync(actor, token);
        await fixture.RevokeSessionsAsync(actor.IdentityId, token);

        using var response = await client.GetAsync(WorkUrl(responsibility), token);

        await AssertProblemAsync(response, HttpStatusCode.Unauthorized, token);
    }

    private static string WorkUrl(Guid responsibilityId) =>
        "/api/order-operations/preparation/work" +
        $"?preparationResponsibilityId={responsibilityId:D}";

    private static async Task AssertProblemAsync(
        HttpResponseMessage response,
        HttpStatusCode status,
        CancellationToken cancellationToken)
    {
        Assert.Equal(status, response.StatusCode);
        using var document = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken),
            cancellationToken: cancellationToken);
        Assert.Equal(
            status == HttpStatusCode.Forbidden
                ? "order_operations.preparation.forbidden"
                : "identities_and_capabilities.invalid_session",
            document.RootElement.GetProperty("code").GetString());
    }

    private static async Task ObserveAsync(Task task)
    {
        try
        {
            await task;
        }
        catch
        {
            // The original assertion preserves the relevant failure.
        }
    }
}

internal sealed class BlockingProductOperationalReferenceLookup(
    IProductOperationalReferenceLookup inner,
    TaskCompletionSource lookupReached,
    TaskCompletionSource releaseLookup) : IProductOperationalReferenceLookup
{
    public async Task<IReadOnlyList<ProductOperationalReference>> ReadByIdsAsync(
        IReadOnlyCollection<Guid> productIds,
        System.Data.Common.DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        var result = await inner.ReadByIdsAsync(
            productIds,
            transaction,
            cancellationToken);
        lookupReached.TrySetResult();
        await releaseLookup.Task.WaitAsync(cancellationToken);
        return result;
    }
}
