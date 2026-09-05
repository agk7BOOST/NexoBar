using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NexoBar.OrderOperations;
using Npgsql;

namespace NexoBar.OrderOperations.IntegrationTests;

[Collection(OrderOperationsApiCollection.Name)]
public sealed class ConfirmationInstructionMigrationTests(OrderOperationsApiFixture fixture)
{
    private const string PreviousMigration =
        "20260830210000_ReidentifyIncorporationContent";
    private const string CurrentMigration =
        "20260830230000_AddConfirmationInstructions";
    private const string LatestMigration =
        "20260904152524_AddAuthoritativePendingComposition";

    [Fact]
    public async Task Migration_preserves_I3A_data_replay_queries_and_has_safe_down()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var prepared = await fixture.CreateProductAsync("Hamburguesa", "10", token);
        var plain = await fixture.CreateProductAsync("Agua", "3", token);
        var responsibility = Guid.CreateVersion7();
        await fixture.SetProductPreparationAsync(prepared.Id, responsibility, token);
        var orderId = Guid.CreateVersion7();
        var firstIncorporationId = Guid.CreateVersion7();
        var subsequentIncorporationId = Guid.CreateVersion7();
        var firstKey = Guid.NewGuid();
        var subsequentKey = Guid.NewGuid();
        var workId = Guid.CreateVersion7();

        await fixture.MigrateOrderOperationsAsync(PreviousMigration, token);
        try
        {
            await SeedI3AAsync(
                orderId,
                firstIncorporationId,
                subsequentIncorporationId,
                prepared.Id,
                plain.Id,
                responsibility,
                workId,
                firstKey,
                subsequentKey,
                token);

            await fixture.MigrateOrderOperationsAsync(CurrentMigration, token);
            await fixture.MigrateOrderOperationsAsync(LatestMigration, token);
            await using (var scope = fixture.Services.CreateAsyncScope())
            {
                var dbContext = scope.ServiceProvider
                    .GetRequiredService<OrderOperationsDbContext>();
                var contents = await dbContext.IncorporationContents.AsNoTracking()
                    .OrderBy(content => content.IncorporationId)
                    .ThenBy(content => content.ContentOrdinal)
                    .ToArrayAsync(token);
                Assert.All(contents, content => Assert.Null(content.Instruction));
                Assert.Equal([1, 1], contents.Select(x => x.ContentOrdinal).ToArray());
                Assert.All(
                    await dbContext.FirstConfirmationCommandContents.AsNoTracking()
                        .ToArrayAsync(token),
                    line => Assert.Null(line.Instruction));
                Assert.All(
                    await dbContext.SubsequentConfirmationCommandContents.AsNoTracking()
                        .ToArrayAsync(token),
                    line => Assert.Null(line.Instruction));
                Assert.All(
                    await dbContext.ConfirmationHistory.AsNoTracking().ToArrayAsync(token),
                    history => Assert.Null(history.ActorIdentityId));
                Assert.Null((await dbContext.FirstConfirmationCommands.AsNoTracking()
                    .SingleAsync(token)).ActorIdentityId);
                var legacySubsequent = await dbContext.SubsequentConfirmationCommands
                    .AsNoTracking().SingleAsync(token);
                Assert.Null(legacySubsequent.ActorIdentityId);
                Assert.Null(legacySubsequent.IntentPendingCompositionId);
            }
            var migratedWork = Assert.Single(await fixture.ReadPreparationWorkAsync(token));
            Assert.Equal(workId, migratedWork.Id);
            Assert.Equal(1, migratedWork.ContentOrdinal);
            Assert.Null(migratedWork.Instruction);

            using var firstReplay = await PostFirstAsync(
                "Mesa 7", firstKey, prepared.Id, 2, null, token);
            Assert.Equal(HttpStatusCode.Conflict, firstReplay.StatusCode);
            using var subsequentReplay = await PostSubsequentAsync(
                orderId,
                subsequentKey,
                Guid.NewGuid(),
                [(plain.Id, 3, null)],
                token);
            Assert.Equal(HttpStatusCode.Conflict, subsequentReplay.StatusCode);

            using var orderResponse = await fixture.Client.GetAsync(
                $"/api/order-operations/orders/{orderId:D}", token);
            var order = Assert.IsType<OrderQueryResponse>(
                await orderResponse.Content.ReadFromJsonAsync<OrderQueryResponse>(token));
            Assert.All(order.Incorporations.SelectMany(x => x.Items),
                item => Assert.Null(item.Instruction));
            var actor = await fixture.CreatePreparationActorAsync(
                hasPreparation: true,
                responsibility,
                token);
            using var preparationClient = await fixture.LoginAsync(actor, token);
            using var preparationResponse = await preparationClient.GetAsync(
                "/api/order-operations/preparation/work" +
                $"?preparationResponsibilityId={responsibility:D}", token);
            var preparation = Assert.Single(Assert.IsType<PreparationWorkResponse[]>(
                await preparationResponse.Content
                    .ReadFromJsonAsync<PreparationWorkResponse[]>(token)));
            Assert.Null(preparation.Instruction);

            await fixture.MigrateOrderOperationsAsync(PreviousMigration, token);
            await AssertInstructionColumnsAsync(expected: false, token);
            await fixture.MigrateOrderOperationsAsync(LatestMigration, token);

            var pending = await fixture.StartPendingCompositionAsync(
                orderId.ToString("D"),
                token);
            using var newConfirmation = await PostSubsequentAsync(
                orderId,
                Guid.NewGuid(),
                pending.PendingCompositionId,
                [
                    (prepared.Id, 1, (string?)null),
                    (prepared.Id, 1, "sin cebolla")
                ],
                token);
            Assert.Equal(HttpStatusCode.Created, newConfirmation.StatusCode);
            var created = await ReadSubsequentAsync(newConfirmation, token);
            Assert.Equal(2, created.Incorporation.Items.Count);
            Assert.Equal(3, await fixture.CountPreparationWorkAsync(token));

            var downFailure = await Assert.ThrowsAnyAsync<Exception>(() =>
                fixture.MigrateOrderOperationsAsync(PreviousMigration, token));
            Assert.Contains(
                "duplicate Product lines require the new schema",
                downFailure.ToString(),
                StringComparison.Ordinal);
            await AssertInstructionColumnsAsync(expected: true, token);
        }
        finally
        {
            await fixture.MigrateOrderOperationsAsync(LatestMigration, token);
            await fixture.ResetAsync(token);
        }
    }

    private async Task SeedI3AAsync(
        Guid orderId,
        Guid firstIncorporationId,
        Guid subsequentIncorporationId,
        Guid preparedProductId,
        Guid plainProductId,
        Guid responsibilityId,
        Guid workId,
        Guid firstKey,
        Guid subsequentKey,
        CancellationToken token)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO order_operations.orders (id, context)
            VALUES (@order, 'Mesa 7');
            INSERT INTO order_operations.incorporations (id, order_id, ordinal)
            VALUES (@first_inc, @order, 1), (@sub_inc, @order, 2);
            INSERT INTO order_operations.confirmation_history
                (id, incorporation_id, confirmed_context, occurred_at)
            VALUES
                (@first_history, @first_inc, 'Mesa 7', @first_at),
                (@sub_history, @sub_inc, 'Mesa 7', @sub_at);
            INSERT INTO order_operations.incorporation_contents
                (incorporation_id, content_ordinal, product_id, quantity, applied_price)
            VALUES
                (@first_inc, 1, @prepared, 2, 10),
                (@sub_inc, 1, @plain, 3, 3);
            INSERT INTO order_operations.preparation_work
                (id, incorporation_id, content_ordinal,
                 preparation_responsibility_id, total_quantity,
                 pending_quantity, in_preparation_quantity, ready_quantity)
            VALUES (@work, @first_inc, 1, @responsibility, 2, 2, 0, 0);
            INSERT INTO order_operations.first_confirmation_commands
                (idempotency_key, intent_context, result_incorporation_id)
            VALUES (@first_key, 'Mesa 7', @first_inc);
            INSERT INTO order_operations.first_confirmation_command_contents
                (idempotency_key, line_ordinal, product_id, quantity)
            VALUES (@first_key, 1, @prepared, 2);
            INSERT INTO order_operations.subsequent_confirmation_commands
                (idempotency_key, intent_order_id, result_incorporation_id)
            VALUES (@sub_key, @order, @sub_inc);
            INSERT INTO order_operations.subsequent_confirmation_command_contents
                (idempotency_key, line_ordinal, product_id, quantity)
            VALUES (@sub_key, 1, @plain, 3);
            """;
        command.Parameters.AddWithValue("order", orderId);
        command.Parameters.AddWithValue("first_inc", firstIncorporationId);
        command.Parameters.AddWithValue("sub_inc", subsequentIncorporationId);
        command.Parameters.AddWithValue("first_history", Guid.CreateVersion7());
        command.Parameters.AddWithValue("sub_history", Guid.CreateVersion7());
        command.Parameters.AddWithValue(
            "first_at", new DateTimeOffset(2026, 8, 30, 12, 0, 0, TimeSpan.Zero));
        command.Parameters.AddWithValue(
            "sub_at", new DateTimeOffset(2026, 8, 30, 12, 5, 0, TimeSpan.Zero));
        command.Parameters.AddWithValue("prepared", preparedProductId);
        command.Parameters.AddWithValue("plain", plainProductId);
        command.Parameters.AddWithValue("work", workId);
        command.Parameters.AddWithValue("responsibility", responsibilityId);
        command.Parameters.AddWithValue("first_key", firstKey);
        command.Parameters.AddWithValue("sub_key", subsequentKey);
        await command.ExecuteNonQueryAsync(token);
    }

    private async Task AssertInstructionColumnsAsync(
        bool expected,
        CancellationToken token)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync(token);
        foreach (var table in new[]
                 {
                     "incorporation_contents",
                     "first_confirmation_command_contents",
                     "subsequent_confirmation_command_contents"
                 })
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT EXISTS (
                    SELECT 1
                    FROM information_schema.columns
                    WHERE table_schema = 'order_operations'
                      AND table_name = @table
                      AND column_name = 'instruction')
                """;
            command.Parameters.AddWithValue("table", table);
            Assert.Equal(expected,
                Assert.IsType<bool>(await command.ExecuteScalarAsync(token)));
        }
    }

    private async Task<HttpResponseMessage> PostFirstAsync(
        string context,
        Guid key,
        Guid productId,
        int quantity,
        string? instruction,
        CancellationToken token)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/api/order-operations/first-confirmations")
        {
            Content = JsonContent.Create(new FirstConfirmationRequest(
                context,
                [new FirstConfirmationItemRequest(productId, quantity, instruction)]))
        };
        request.Headers.Add("Idempotency-Key", key.ToString("D"));
        return await OrderOperationsApiFixture.SendWithAntiforgeryAsync(
            fixture.OrderOperationsClient,
            request,
            token);
    }

    private async Task<HttpResponseMessage> PostSubsequentAsync(
        Guid orderId,
        Guid key,
        Guid pendingCompositionId,
        IReadOnlyList<(Guid ProductId, int Quantity, string? Instruction)> items,
        CancellationToken token)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/order-operations/orders/{orderId:D}/confirmations")
        {
            Content = JsonContent.Create(new SubsequentConfirmationRequest(
                pendingCompositionId,
                items.Select(item => new SubsequentConfirmationItemRequest(
                    item.ProductId,
                    item.Quantity,
                    item.Instruction)).ToArray()))
        };
        request.Headers.Add("Idempotency-Key", key.ToString("D"));
        return await OrderOperationsApiFixture.SendWithAntiforgeryAsync(
            fixture.OrderOperationsClient,
            request,
            token);
    }

    private static async Task<FirstConfirmationResponse> ReadFirstAsync(
        HttpResponseMessage response,
        CancellationToken token) =>
        Assert.IsType<FirstConfirmationResponse>(
            await response.Content.ReadFromJsonAsync<FirstConfirmationResponse>(token));

    private static async Task<SubsequentConfirmationResponse> ReadSubsequentAsync(
        HttpResponseMessage response,
        CancellationToken token) =>
        Assert.IsType<SubsequentConfirmationResponse>(
            await response.Content.ReadFromJsonAsync<SubsequentConfirmationResponse>(token));
}
