using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NexoBar.Catalog;
using NexoBar.IdentitiesAndCapabilities;

namespace NexoBar.OrderOperations.IntegrationTests;

// S8-I4A0 inventory (categories describe the pre-retrofit contracts):
// A: OrderQueryService (Context, confirmed Content, amount, liquidation/closure eligibility),
//    OrderDeliveryQueryService (also ordinary correction/cancellation quantities),
//    PendingCompositionService.FindAsync, AppliedPriceCorrectionService.EvaluateAsync.
//    The latter three already stabilize actor authority; their terminal boundary was missing.
// B: No separate general active read already enforced the complete AD-SEC-05 boundary.
// C: OperationalInterventionQueryService exact Content target; Preparation destination reads.
//    Preserve both independent responsibilities; neither grants general Order visibility.
// D: No public History/audit/admin Order read found. Economic/closure readers also serve
//    commands internally; those call paths and all command/replay contracts are unchanged.
// E: CompleteCancellationService.ExecuteAsync(key: null) combined current eligibility with
//    post-terminal cancellation facts. Its read branch is independently restricted before
//    constructing the evaluation. No historical authority is created by this retrofit.
[Collection(OrderOperationsApiCollection.Name)]
public sealed class ActiveOrderReadAuthorizationTests(OrderOperationsApiFixture fixture)
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static string[] Paths(DeliveryTarget target) =>
    [
        $"/api/order-operations/orders/{target.OperationalReference}",
        $"/api/order-operations/orders/{target.OperationalReference}/delivery",
        $"/api/orders/{target.OperationalReference}/pending-composition",
        $"/api/orders/{target.OperationalReference}/complete-cancellation",
        $"/api/order-operations/orders/{target.OperationalReference}/incorporations/{target.IncorporationId}/contents/{target.ContentOrdinal}/applied-price-correction"
    ];

    [Theory]
    [InlineData("absent", HttpStatusCode.Unauthorized)]
    [InlineData("inactive", HttpStatusCode.Unauthorized)]
    [InlineData("none", HttpStatusCode.Forbidden)]
    [InlineData("intervention", HttpStatusCode.Forbidden)]
    [InlineData("preparation", HttpStatusCode.Forbidden)]
    [InlineData("operations", HttpStatusCode.OK)]
    public async Task Current_authority_is_required_before_resource_discovery(string authority, HttpStatusCode expected)
    {
        await fixture.ResetAsync(Token);
        var target = await DeliveryQuantityTestSupport.CreateDirectAsync(fixture, 2, Token);
        var actor = await fixture.CreateDeliveryActorAsync(authority is "operations" or "inactive", authority == "preparation", null, Token);
        using var client = await fixture.LoginAsync(actor, Token);
        await using (var scope = fixture.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IdentitiesAndCapabilitiesDbContext>();
            if (authority == "inactive")
                await db.Identities.Where(x => x.Id == actor.IdentityId).ExecuteUpdateAsync(s => s.SetProperty(x => x.IsActive, false), Token);
            if (authority == "intervention")
            {
                db.ResponsibilityAssignments.Add(new(actor.IdentityId, FunctionalResponsibility.OperationalIntervention));
                await db.SaveChangesAsync(Token);
            }
        }
        var reader = authority == "absent" ? fixture.Client : client;
        foreach (var path in Paths(target))
        {
            using var response = await reader.GetAsync(path, Token);
            Assert.Equal(expected, response.StatusCode);
            if (expected != HttpStatusCode.OK)
            {
                var body = await response.Content.ReadAsStringAsync(Token);
                AssertNoState(body, target);
                using var missing = await reader.GetAsync(path.Replace(target.OperationalReference, Guid.NewGuid().ToString()), Token);
                Assert.Equal(expected, missing.StatusCode);
                var missingBody = await missing.Content.ReadAsStringAsync(Token);
                AssertNoState(missingBody, target);
                // An invalid session clears its cookie, so the next 401 may be the
                // existing anonymous challenge. Compare identical actor states for 403.
                if (expected == HttpStatusCode.Forbidden) Assert.Equal(StableProblem(body), StableProblem(missingBody));
            }
        }
        if (authority == "operations")
        {
            await fixture.RevokeOrderOperationsAssignmentAsync(actor.IdentityId, Token);
            foreach (var path in Paths(target))
            {
                using var revoked = await client.GetAsync(path, Token);
                Assert.Equal(HttpStatusCode.Forbidden, revoked.StatusCode);
                AssertNoState(await revoked.Content.ReadAsStringAsync(Token), target);
            }
        }
    }

    [Theory]
    [InlineData("ordinary")]
    [InlineData("zero")]
    [InlineData("frozen")]
    [InlineData("closed")]
    [InlineData("cancelled")]
    public async Task Read_visibility_depends_on_explicit_terminal_State(string lifecycle)
    {
        await fixture.ResetAsync(Token);
        var target = await DeliveryQuantityTestSupport.CreateDirectAsync(fixture, 2, Token);
        if (lifecycle == "zero")
        {
            using var request = new HttpRequestMessage(HttpMethod.Post,
                $"/api/order-operations/orders/{target.OperationalReference}/incorporations/{target.IncorporationId}/contents/{target.ContentOrdinal}/cancel-content-quantity")
            { Content = JsonContent.Create(new { quantity = 2 }) };
            request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
            using var response = await OrderOperationsApiFixture.SendWithAntiforgeryAsync(fixture.OrderOperationsClient, request, Token);
            response.EnsureSuccessStatusCode();
        }
        if (lifecycle is "frozen" or "closed")
        {
            await fixture.SetAllDeliveredQuantitiesAsync(Guid.Parse(target.OperationalReference), Token);
            using var response = await LiquidationTestSupport.PostExternalAsync(fixture.OrderOperationsClient, target.OperationalReference, Guid.NewGuid(), Token);
            response.EnsureSuccessStatusCode();
        }
        if (lifecycle == "closed")
        {
            using var response = await ClosureTestSupport.PostAsync(fixture.OrderOperationsClient, target.OperationalReference, Guid.NewGuid(), Token);
            response.EnsureSuccessStatusCode();
        }
        if (lifecycle == "cancelled")
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/orders/{target.OperationalReference}/complete-cancellation");
            request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
            using var response = await OrderOperationsApiFixture.SendWithAntiforgeryAsync(fixture.OrderOperationsClient, request, Token);
            response.EnsureSuccessStatusCode();
        }

        // A different actor/session has the same visibility: no creator or Context ACL.
        var actor = await fixture.CreateDeliveryActorAsync(true, false, null, Token);
        using var client = await fixture.LoginAsync(actor, Token);
        foreach (var path in Paths(target))
        {
            using var response = await client.GetAsync(path, Token);
            if (lifecycle is "closed" or "cancelled")
            {
                Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
                var body = await response.Content.ReadAsStringAsync(Token);
                AssertNoState(body, target);
                using var missing = await client.GetAsync(path.Replace(target.OperationalReference, Guid.NewGuid().ToString()), Token);
                Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
                Assert.Equal(StableProblem(body), StableProblem(await missing.Content.ReadAsStringAsync(Token)));
            }
            else Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        if (lifecycle is not ("closed" or "cancelled"))
        {
            using var response = await client.GetAsync(Paths(target)[0], Token);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Token));
            Assert.Equal(new[] { "operationalReference", "context", "incorporations", "functionalAmount", "isLiquidationEligible", "liquidationBlockers", "isLiquidated", "isFrozen", "liquidatedAmount", "liquidationMode", "declaredPaymentMedium", "isClosed", "closedAt", "isClosureEligible" }.Order(),
                json.RootElement.EnumerateObject().Select(x => x.Name).Order());
            var state = json.RootElement;
            Assert.Equal(target.OperationalReference, state.GetProperty("operationalReference").GetString());
            Assert.False(state.GetProperty("isClosed").GetBoolean());
            Assert.Equal(lifecycle == "frozen", state.GetProperty("isFrozen").GetBoolean());
            Assert.Equal(lifecycle is "zero" or "frozen", state.GetProperty(lifecycle == "frozen" ? "isClosureEligible" : "isLiquidationEligible").GetBoolean());
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Terminal_commit_waits_for_an_authorized_active_snapshot(bool close)
    {
        await fixture.ResetAsync(Token);
        var target = await DeliveryQuantityTestSupport.CreateDirectAsync(fixture, 2, Token);
        if (close)
        {
            await fixture.SetAllDeliveredQuantitiesAsync(Guid.Parse(target.OperationalReference), Token);
            using var liquidation = await LiquidationTestSupport.PostExternalAsync(fixture.OrderOperationsClient, target.OperationalReference, Guid.NewGuid(), Token);
            liquidation.EnsureSuccessStatusCode();
        }
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var application = fixture.CreateApplicationWithProductLookupDecorator(services =>
            new BlockingProductOperationalReferenceLookup(new ProductOperationalReferenceLookup(services.GetRequiredService<CatalogDbContext>()), reached, release));
        using var reader = await fixture.LoginAsync(fixture.DefaultOrderOperationsActor, Token, application);
        var read = reader.GetAsync(Paths(target)[1], Token);
        Task<HttpResponseMessage>? terminal = null;
        try
        {
            await reached.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
            terminal = EndAsync();
            Assert.True(await fixture.WaitForOrderRowLockWaitersAsync(1, TimeSpan.FromSeconds(10), Token));
            Assert.False(terminal.IsCompleted);
            release.TrySetResult();
            using var snapshot = await read.WaitAsync(TimeSpan.FromSeconds(10), Token);
            Assert.Equal(HttpStatusCode.OK, snapshot.StatusCode);
            using var ended = await terminal.WaitAsync(TimeSpan.FromSeconds(10), Token);
            ended.EnsureSuccessStatusCode();
            using var after = await reader.GetAsync(Paths(target)[1], Token);
            Assert.Equal(HttpStatusCode.NotFound, after.StatusCode);
        }
        finally
        {
            release.TrySetResult();
            try { (await read).Dispose(); } catch { /* Preserve the original failure. */ }
            if (terminal is not null)
                try { (await terminal).Dispose(); } catch { /* Preserve the original failure. */ }
        }

        async Task<HttpResponseMessage> EndAsync()
        {
            using var request = new HttpRequestMessage(HttpMethod.Post,
                $"/api/orders/{target.OperationalReference}/{(close ? "close" : "complete-cancellation")}");
            request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
            return await OrderOperationsApiFixture.SendWithAntiforgeryAsync(fixture.OrderOperationsClient, request, Token);
        }
    }

    private static void AssertNoState(string body, DeliveryTarget target)
    {
        Assert.DoesNotContain(target.OperationalReference, body);
        Assert.DoesNotContain(target.IncorporationId.ToString(), body);
        foreach (var field in new[] { "context", "operationalReference", "incorporations", "cancellationId", "closedAt", "functionalAmount", "order_completely_cancelled", "already_closed" })
            Assert.DoesNotContain(field, body, StringComparison.OrdinalIgnoreCase);
    }

    private static string StableProblem(string body)
    {
        if (string.IsNullOrEmpty(body)) return body;
        using var json = JsonDocument.Parse(body);
        return string.Join("|", json.RootElement.EnumerateObject().Where(x => x.Name != "traceId").Select(x => $"{x.Name}:{x.Value}"));
    }
}
