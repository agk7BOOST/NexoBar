using System.Net;
using System.Net.Http.Json;
using NexoBar.IdentitiesAndCapabilities;

namespace NexoBar.Inventory.IntegrationTests;

[Collection(InventoryApiCollection.Name)]
public sealed class InventoryLifecycleAndUnitApiTests(InventoryApiFixture fixture)
{
    [Fact]
    public async Task Retire_reactivate_and_reconcile_restores_operation_from_no_current_existence()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var actor = await fixture.CreateActorAsync(
            "lifecycle",
            token,
            FunctionalResponsibility.InventoryConfiguration,
            FunctionalResponsibility.InventoryOperation);
        var item = await fixture.AddItemAsync("Lifecycle item", "kg", token);
        await fixture.LoginAsync(actor, token);

        using (var count = await fixture.PostCountAsync(
                   item.Id,
                   Guid.NewGuid(),
                   "5",
                   token))
        {
            Assert.Equal(HttpStatusCode.Created, count.StatusCode);
            var observation = (await count.Content.ReadFromJsonAsync<CountObservationResponse>(
                token))!;
            using var reconcile = await fixture.PostReconcileAsync(
                item.Id,
                Guid.NewGuid(),
                observation.CountObservationId,
                token);
            Assert.Equal(HttpStatusCode.OK, reconcile.StatusCode);
        }

        var beforeRetire = await fixture.ReadItemAsync(item.Id, token);
        var movementCountBeforeRetire = (await fixture.ReadMovementsAsync(token)).Count;
        using (var retire = await fixture.PostRetireAsync(
                   item.Id,
                   Guid.NewGuid(),
                   true,
                   token))
        {
            Assert.Equal(HttpStatusCode.OK, retire.StatusCode);
            var result = (await retire.Content.ReadFromJsonAsync<InventoryLifecycleResponse>(
                token))!;
            Assert.False(result.IsActive);
            Assert.False(result.OrdinaryOperationReady);
            Assert.False(result.RequiresReconciliation);
        }

        var retired = await fixture.ReadItemAsync(item.Id, token);
        Assert.False(retired.IsActive);
        Assert.Null(retired.CurrentRegisteredQuantity);
        Assert.Equal(beforeRetire.MovementRevision, retired.MovementRevision);
        Assert.Equal(
            movementCountBeforeRetire,
            (await fixture.ReadMovementsAsync(token)).Count);
        Assert.DoesNotContain(
            item.Id,
            (await fixture.GetOperationalItemsAsync(token)).Select(value => value.ItemId));
        using (var history = await fixture.GetMovementHistoryAsync(item.Id, token))
        {
            Assert.Equal(HttpStatusCode.OK, history.StatusCode);
        }

        using (var reactivate = await fixture.PostReactivateAsync(
                   item.Id,
                   Guid.NewGuid(),
                   false,
                   null,
                   token))
        {
            Assert.Equal(HttpStatusCode.OK, reactivate.StatusCode);
            var result = (await reactivate.Content.ReadFromJsonAsync<InventoryLifecycleResponse>(
                token))!;
            Assert.True(result.IsActive);
            Assert.False(result.OrdinaryOperationReady);
            Assert.True(result.RequiresReconciliation);
        }

        var awaiting = Assert.Single(
            await fixture.GetOperationalItemsAsync(token),
            value => value.ItemId == item.Id);
        Assert.Null(awaiting.CurrentRegisteredQuantity);
        Assert.True(awaiting.RequiresReconciliation);

        using (var blocked = await fixture.PostEntryAsync(
                   item.Id,
                   Guid.NewGuid(),
                   "1",
                   token))
        {
            await InventoryTestAssertions.AssertProblemAsync(
                blocked,
                HttpStatusCode.Conflict,
                "inventory.item.reconciliation_required",
                token);
        }

        CountObservationResponse newObservation;
        using (var count = await fixture.PostCountAsync(
                   item.Id,
                   Guid.NewGuid(),
                   "5",
                   token))
        {
            Assert.Equal(HttpStatusCode.Created, count.StatusCode);
            newObservation = (await count.Content.ReadFromJsonAsync<CountObservationResponse>(
                token))!;
        }

        using (var reconcile = await fixture.PostReconcileAsync(
                   item.Id,
                   Guid.NewGuid(),
                   newObservation.CountObservationId,
                   token))
        {
            Assert.Equal(HttpStatusCode.OK, reconcile.StatusCode);
            var result = (await reconcile.Content.ReadFromJsonAsync<ReconcileInventoryCountResponse>(
                token))!;
            Assert.Equal("reconciled", result.Outcome);
            Assert.Null(result.PreviousRegisteredQuantity);
        }

        using (var entry = await fixture.PostEntryAsync(
                   item.Id,
                   Guid.NewGuid(),
                   "1",
                   token))
        {
            Assert.Equal(HttpStatusCode.OK, entry.StatusCode);
        }
    }

    [Fact]
    public async Task Unit_correction_invalidates_pending_counts_and_is_rejected_after_history()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var actor = await fixture.CreateActorAsync(
            "unit-lifecycle",
            token,
            FunctionalResponsibility.InventoryConfiguration,
            FunctionalResponsibility.InventoryOperation);
        var item = await fixture.AddItemAsync("Unit correction item", "liters", token);
        await fixture.LoginAsync(actor, token);

        CountObservationResponse oldObservation;
        using (var count = await fixture.PostCountAsync(
                   item.Id,
                   Guid.NewGuid(),
                   "10",
                   token))
        {
            oldObservation = (await count.Content.ReadFromJsonAsync<CountObservationResponse>(
                token))!;
        }

        using (var correction = await fixture.PostUnitCorrectionAsync(
                   item.Id,
                   Guid.NewGuid(),
                   "liters",
                   "bottles",
                   token))
        {
            Assert.Equal(HttpStatusCode.OK, correction.StatusCode);
        }

        var corrected = await fixture.ReadItemAsync(item.Id, token);
        Assert.Equal("bottles", corrected.OperationalUnit.Value);
        Assert.Equal(0, corrected.MovementRevision);
        Assert.NotNull((await fixture.ReadCountObservationsAsync(token)).Single().InvalidatedAtUtc);

        using (var reverse = await fixture.PostUnitCorrectionAsync(
                   item.Id,
                   Guid.NewGuid(),
                   "bottles",
                   "liters",
                   token))
        {
            Assert.Equal(HttpStatusCode.OK, reverse.StatusCode);
        }

        using (var invalidated = await fixture.PostReconcileAsync(
                   item.Id,
                   Guid.NewGuid(),
                   oldObservation.CountObservationId,
                   token))
        {
            await InventoryTestAssertions.AssertProblemAsync(
                invalidated,
                HttpStatusCode.Conflict,
                "inventory.reconciliation.observation_invalidated",
                token);
        }

        CountObservationResponse usableObservation;
        using (var count = await fixture.PostCountAsync(
                   item.Id,
                   Guid.NewGuid(),
                   "10",
                   token))
        {
            usableObservation = (await count.Content.ReadFromJsonAsync<CountObservationResponse>(
                token))!;
        }
        using (var reconcile = await fixture.PostReconcileAsync(
                   item.Id,
                   Guid.NewGuid(),
                   usableObservation.CountObservationId,
                   token))
        {
            Assert.Equal(HttpStatusCode.OK, reconcile.StatusCode);
        }

        using (var afterHistory = await fixture.PostUnitCorrectionAsync(
                   item.Id,
                   Guid.NewGuid(),
                   "liters",
                   "bottles",
                   token))
        {
            await InventoryTestAssertions.AssertProblemAsync(
                afterHistory,
                HttpStatusCode.Conflict,
                "inventory.item.unit_correction_requires_replacement",
                token);
        }

        var final = await fixture.ReadItemAsync(item.Id, token);
        Assert.Equal("liters", final.OperationalUnit.Value);
        Assert.Single(await fixture.ReadMovementsAsync(token));
    }

    [Fact]
    public async Task Retired_names_can_be_reused_but_reactivation_requires_collision_resolution()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var actor = await fixture.CreateActorAsync(
            "name-collision",
            token,
            FunctionalResponsibility.InventoryConfiguration);
        await fixture.LoginAsync(actor, token);

        InventoryItemResponse original;
        using (var create = await fixture.PostItemAsync(
                   Guid.NewGuid(),
                   "Reusable name",
                   "kg",
                   token))
        {
            original = (await create.Content.ReadFromJsonAsync<InventoryItemResponse>(token))!;
        }
        using (var retire = await fixture.PostRetireAsync(
                   original.ItemId,
                   Guid.NewGuid(),
                   true,
                   token))
        {
            Assert.Equal(HttpStatusCode.OK, retire.StatusCode);
        }

        using (var createReplacement = await fixture.PostItemAsync(
                   Guid.NewGuid(),
                   "Reusable name",
                   "bottles",
                   token))
        {
            Assert.Equal(HttpStatusCode.Created, createReplacement.StatusCode);
        }

        using (var collision = await fixture.PostReactivateAsync(
                   original.ItemId,
                   Guid.NewGuid(),
                   false,
                   null,
                   token))
        {
            await InventoryTestAssertions.AssertProblemAsync(
                collision,
                HttpStatusCode.Conflict,
                "inventory.item.reactivation_name_conflict",
                token);
        }

        using (var resolved = await fixture.PostReactivateAsync(
                   original.ItemId,
                   Guid.NewGuid(),
                   false,
                   "Reusable name successor",
                   token))
        {
            Assert.Equal(HttpStatusCode.OK, resolved.StatusCode);
            var result = (await resolved.Content.ReadFromJsonAsync<InventoryLifecycleResponse>(
                token))!;
            Assert.Equal("Reusable name successor", result.OperationalName);
        }
    }

    [Fact]
    public async Task Configuration_and_operation_responsibilities_remain_separate()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var configurationActor = await fixture.CreateActorAsync(
            "configuration-only",
            token,
            FunctionalResponsibility.InventoryConfiguration);
        var operationActor = await fixture.CreateActorAsync(
            "operation-only",
            token,
            FunctionalResponsibility.InventoryOperation);
        var item = await fixture.AddItemAsync("Responsibility item", "kg", token);

        await fixture.LoginAsync(configurationActor, token);
        using (var count = await fixture.PostCountAsync(
                   item.Id,
                   Guid.NewGuid(),
                   "2",
                   token))
        {
            await InventoryTestAssertions.AssertProblemAsync(
                count,
                HttpStatusCode.Forbidden,
                "inventory.operation.forbidden",
                token);
        }
        await fixture.LoginAsync(operationActor, token);
        using (var unit = await fixture.PostUnitCorrectionAsync(
                   item.Id,
                   Guid.NewGuid(),
                   "kg",
                   "bottles",
                   token))
        {
            await InventoryTestAssertions.AssertProblemAsync(
                unit,
                HttpStatusCode.Forbidden,
                "inventory.configuration.forbidden",
                token);
        }
        using (var retire = await fixture.PostRetireAsync(
                   item.Id,
                   Guid.NewGuid(),
                   true,
                   token))
        {
            await InventoryTestAssertions.AssertProblemAsync(
                retire,
                HttpStatusCode.Forbidden,
                "inventory.configuration.forbidden",
                token);
        }
    }

    [Fact]
    public async Task Retire_invalidates_an_unconsumed_count_observation()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var actor = await fixture.CreateActorAsync(
            "retire-count",
            token,
            FunctionalResponsibility.InventoryConfiguration,
            FunctionalResponsibility.InventoryOperation);
        var item = await fixture.AddItemAsync("Retire count item", "kg", token);
        await fixture.LoginAsync(actor, token);

        CountObservationResponse observation;
        using (var count = await fixture.PostCountAsync(
                   item.Id,
                   Guid.NewGuid(),
                   "3",
                   token))
        {
            observation = (await count.Content.ReadFromJsonAsync<CountObservationResponse>(
                token))!;
        }

        using (var retire = await fixture.PostRetireAsync(
                   item.Id,
                   Guid.NewGuid(),
                   true,
                   token))
        {
            Assert.Equal(HttpStatusCode.OK, retire.StatusCode);
        }

        Assert.NotNull((await fixture.ReadCountObservationsAsync(token)).Single().InvalidatedAtUtc);
        using (var reactivate = await fixture.PostReactivateAsync(
                   item.Id,
                   Guid.NewGuid(),
                   false,
                   null,
                   token))
        {
            Assert.Equal(HttpStatusCode.OK, reactivate.StatusCode);
        }
        using var reconcile = await fixture.PostReconcileAsync(
            item.Id,
            Guid.NewGuid(),
            observation.CountObservationId,
            token);
        await InventoryTestAssertions.AssertProblemAsync(
            reconcile,
            HttpStatusCode.Conflict,
            "inventory.reconciliation.observation_invalidated",
            token);
    }

    [Fact]
    public async Task Lifecycle_and_unit_commands_support_noop_stale_and_replay_semantics()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var actor = await fixture.CreateActorAsync(
            "command-semantics",
            token,
            FunctionalResponsibility.InventoryConfiguration);
        var item = await fixture.AddItemAsync("Command semantics item", "kg", token);
        await fixture.LoginAsync(actor, token);

        var noOpKey = Guid.NewGuid();
        using (var noOp = await fixture.PostUnitCorrectionAsync(
                   item.Id,
                   noOpKey,
                   "kg",
                   "kg",
                   token))
        {
            Assert.Equal(HttpStatusCode.OK, noOp.StatusCode);
            var result = (await noOp.Content.ReadFromJsonAsync<InventoryUnitCorrectionResponse>(
                token))!;
            Assert.Equal("no_change", result.Outcome);
        }

        using (var stale = await fixture.PostUnitCorrectionAsync(
                   item.Id,
                   Guid.NewGuid(),
                   "kg",
                   "bottles",
                   token))
        {
            Assert.Equal(HttpStatusCode.OK, stale.StatusCode);
        }
        using (var staleExpected = await fixture.PostUnitCorrectionAsync(
                   item.Id,
                   Guid.NewGuid(),
                   "kg",
                   "units",
                   token))
        {
            await InventoryTestAssertions.AssertProblemAsync(
                staleExpected,
                HttpStatusCode.Conflict,
                "inventory.unit_correction.concurrency_conflict",
                token);
        }

        var retireKey = Guid.NewGuid();
        using (var retire = await fixture.PostRetireAsync(
                   item.Id,
                   retireKey,
                   true,
                   token))
        {
            Assert.Equal(HttpStatusCode.OK, retire.StatusCode);
        }
        await fixture.RevokeResponsibilityAsync(
            actor.IdentityId,
            FunctionalResponsibility.InventoryConfiguration,
            token);

        using (var unitReplay = await fixture.PostUnitCorrectionAsync(
                   item.Id,
                   noOpKey,
                   "kg",
                   "kg",
                   token))
        {
            Assert.Equal(HttpStatusCode.OK, unitReplay.StatusCode);
        }
        using (var replay = await fixture.PostRetireAsync(
                   item.Id,
                   retireKey,
                   true,
                   token))
        {
            Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        }
        using (var changedIntent = await fixture.PostRetireAsync(
                   item.Id,
                   retireKey,
                   false,
                   token))
        {
            await InventoryTestAssertions.AssertProblemAsync(
                changedIntent,
                HttpStatusCode.Conflict,
                "inventory.retire.idempotency_key_conflict",
                token);
        }
    }

    [Fact]
    public async Task Unit_correction_and_reconciliation_serialize_on_the_element_row()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var actor = await fixture.CreateActorAsync(
            "unit-concurrency",
            token,
            FunctionalResponsibility.InventoryConfiguration,
            FunctionalResponsibility.InventoryOperation);
        var item = await fixture.AddItemAsync("Unit concurrency item", "liters", token);
        await fixture.LoginAsync(actor, token);

        CountObservationResponse observation;
        using (var count = await fixture.PostCountAsync(
                   item.Id,
                   Guid.NewGuid(),
                   "10",
                   token))
        {
            observation = (await count.Content.ReadFromJsonAsync<CountObservationResponse>(
                token))!;
        }

        var antiforgery = await fixture.GetAntiforgeryTokenAsync(token);
        var correctionTask = fixture.PostUnitCorrectionAsync(
            item.Id,
            Guid.NewGuid(),
            "liters",
            "bottles",
            token,
            antiforgery);
        var reconcileTask = fixture.PostReconcileAsync(
            item.Id,
            Guid.NewGuid(),
            observation.CountObservationId,
            token,
            antiforgery);
        await Task.WhenAll(correctionTask, reconcileTask);
        using var correction = await correctionTask;
        using var reconcile = await reconcileTask;

        if (correction.StatusCode == HttpStatusCode.OK)
        {
            await InventoryTestAssertions.AssertProblemAsync(
                reconcile,
                HttpStatusCode.Conflict,
                "inventory.reconciliation.observation_invalidated",
                token);
        }
        else
        {
            await InventoryTestAssertions.AssertProblemAsync(
                correction,
                HttpStatusCode.Conflict,
                "inventory.item.unit_correction_requires_replacement",
                token);
            Assert.Equal(HttpStatusCode.OK, reconcile.StatusCode);
        }
    }
}
