using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NexoBar.IdentitiesAndCapabilities;

namespace NexoBar.OrderOperations.IntegrationTests;

public sealed partial class OperationalInterventionTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Intervention_authority_is_independent_and_read_is_exact(bool ready)
    {
        var s = await Setup(); using var client = s.Client;
        var work = Assert.Single(await fixture.ReadPreparationWorkAsync(Token));
        using var preparer = await fixture.LoginAsync(await fixture.CreatePreparationActorAsync(true, work.PreparationResponsibilityId, Token), Token);
        using var unauthorizedCommand = await Intervene(preparer, s.Target, ready);
        using var unauthorizedRead = await preparer.GetAsync(ReadPath(s.Target), Token);
        using var operationsRead = await fixture.OrderOperationsClient.GetAsync(ReadPath(s.Target), Token);
        Assert.All(new[] { unauthorizedCommand, unauthorizedRead, operationsRead }, r => Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode));
        using var queue = await client.GetAsync($"/api/order-operations/preparation/work?preparationResponsibilityId={work.PreparationResponsibilityId}", Token);
        Assert.Equal(HttpStatusCode.Forbidden, queue.StatusCode);
        foreach (var action in new[] { "start", "ready", "correct-start", "correct-ready" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/order-operations/preparation/work/{work.Id}/{action}")
            { Content = JsonContent.Create(new { quantity = 1 }) };
            request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
            using var response = await OrderOperationsApiFixture.SendWithAntiforgeryAsync(client, request, Token);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
        using var read = await client.GetAsync(ReadPath(s.Target), Token);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        var target = (await read.Content.ReadFromJsonAsync<OperationalInterventionTargetResponse>(Token))!;
        Assert.Equal((Guid.Parse(s.Target.OperationalReference), work.Id, work.IncorporationId, work.ContentOrdinal),
            (target.OrderId, target.WorkId, target.IncorporationId, target.ContentOrdinal));
        Assert.Equal((7, 0, 0, 7, 2, 2, 3, 7, 1, false, 2, 2),
            (target.ConfirmedQuantity, target.RemovedByCorrectionQuantity, target.CancelledQuantity, target.FulfillmentQuantity,
                target.PendingQuantity, target.InPreparationQuantity, target.ReadyQuantity, target.TotalQuantity,
                target.DeliveredQuantity, target.IsFrozen, target.IntervenableInPreparationQuantity, target.IntervenableReadyQuantity));
        using var json = JsonDocument.Parse(await read.Content.ReadAsStringAsync(Token));
        Assert.Equal(new[] { "orderId", "workId", "incorporationId", "contentOrdinal", "productId", "productOperationalName", "instruction",
            "confirmedQuantity", "removedByCorrectionQuantity", "cancelledQuantity", "fulfillmentQuantity", "pendingQuantity",
            "inPreparationQuantity", "readyQuantity", "totalQuantity", "deliveredQuantity", "isFrozen",
            "intervenableInPreparationQuantity", "intervenableReadyQuantity" }.Order(), json.RootElement.EnumerateObject().Select(p => p.Name).Order());
        using var broad = await client.GetAsync("/api/order-operations/intervention/work", Token);
        Assert.Equal(HttpStatusCode.NotFound, broad.StatusCode);
        using var missing = await client.GetAsync(ReadPath(s.Target with { ContentOrdinal = 999 }), Token);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        using var allowed = await Intervene(client, s.Target, ready);
        await Success(allowed);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Replay_is_durable_and_survives_capability_revocation_but_requires_usable_identity_and_session(bool ready)
    {
        var s = await Setup(); using var client = s.Client;
        var key = Guid.NewGuid();
        using var first = await Intervene(client, s.Target, ready, key: key);
        await Success(first);
        var original = await first.Content.ReadAsStringAsync(Token);
        using var later = await Intervene(client, s.Target, ready);
        await Success(later);
        await using (var scope = fixture.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<IdentitiesAndCapabilitiesDbContext>().ResponsibilityAssignments
                .Where(x => x.IdentityId == s.Actor.IdentityId && x.ResponsibilityCode == FunctionalResponsibility.OperationalIntervention).ExecuteDeleteAsync(Token);
        using var replay = await Intervene(client, s.Target, ready, key: key);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(original, await replay.Content.ReadAsStringAsync(Token));
        using var forbidden = await Intervene(client, s.Target, ready);
        using var read = await client.GetAsync(ReadPath(s.Target), Token);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, read.StatusCode);
        Assert.Equal(4, (await fixture.ReadPreparationHistoryAsync(Token)).Count);
        Assert.Equal(4, (await fixture.ReadPreparationCommandsAsync(Token)).Count);
        await fixture.SetIdentityActiveAsync(s.Actor.IdentityId, false, Token);
        using var inactive = await Intervene(client, s.Target, ready, key: key);
        using var inactiveRead = await client.GetAsync(ReadPath(s.Target), Token);
        Assert.Equal(HttpStatusCode.Unauthorized, inactive.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, inactiveRead.StatusCode);
        await fixture.SetIdentityActiveAsync(s.Actor.IdentityId, true, Token);
        await fixture.RevokeSessionsAsync(s.Actor.IdentityId, Token);
        using var revoked = await Intervene(client, s.Target, ready, key: key);
        Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);
    }

    [Theory]
    [InlineData(false, "quantity")] [InlineData(true, "quantity")]
    [InlineData(false, "stage")] [InlineData(true, "stage")]
    [InlineData(false, "target")] [InlineData(true, "target")]
    [InlineData(false, "actor")] [InlineData(true, "actor")]
    [InlineData(false, "preparation")] [InlineData(true, "preparation")]
    public async Task Mismatched_key_rejects_every_intent_dimension(bool ready, string mismatch)
    {
        var s = await Setup(); using var client = s.Client; var key = Guid.NewGuid();
        using var first = await Intervene(client, s.Target, ready, key: key);
        await Success(first);
        using var other = await fixture.LoginAsync(await CreateActor(), Token);
        using var response = mismatch == "preparation"
            ? await PreparationStartTestSupport.PostAsync(client, s.Target.WorkId!.Value, key, 1, Token)
            : await Intervene(mismatch == "actor" ? other : client,
                mismatch == "target" ? s.Target with { WorkId = Guid.CreateVersion7() } : s.Target,
                mismatch == "stage" ? !ready : ready, mismatch == "quantity" ? 2 : 1, key);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(3, (await fixture.ReadPreparationHistoryAsync(Token)).Count);
        Assert.Equal(3, (await fixture.ReadPreparationCommandsAsync(Token)).Count);
        await AssertInvariants();
    }

    [Theory]
    [InlineData("missing-key")] [InlineData("v7-key")] [InlineData("antiforgery")]
    [InlineData("actor")] [InlineData("fraction")] [InlineData("malformed")]
    public async Task Http_contract_rejects_invalid_intents(string invalid)
    {
        var s = await Setup(); using var client = s.Client;
        var body = invalid switch { "actor" => "{\"quantity\":1,\"actorIdentityId\":\"" + Guid.NewGuid() + "\"}",
            "fraction" => "{\"quantity\":1.5}", "malformed" => "{", _ => "{\"quantity\":1}" };
        using var request = new HttpRequestMessage(HttpMethod.Post, Path(s.Target, false)) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        if (invalid != "missing-key") request.Headers.Add("Idempotency-Key", (invalid == "v7-key" ? Guid.CreateVersion7() : Guid.NewGuid()).ToString());
        using var response = invalid == "antiforgery" ? await client.SendAsync(request, Token)
            : await OrderOperationsApiFixture.SendWithAntiforgeryAsync(client, request, Token);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(2, (await fixture.ReadPreparationHistoryAsync(Token)).Count);
    }
}
