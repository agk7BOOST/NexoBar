using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using NexoBar.OrderOperations;
using Npgsql;

namespace NexoBar.OrderOperations.IntegrationTests;

[Collection(OrderOperationsApiCollection.Name)]
public sealed class DeliveryStatePersistenceTests(OrderOperationsApiFixture fixture)
{
    private readonly Dictionary<Guid, Guid> pendingCompositionByConfirmationKey = [];

    [Fact]
    public async Task First_confirmation_creates_zero_state_for_every_direct_and_prepared_content()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var direct = await fixture.CreateProductAsync("Agua", "3", token);
        var prepared = await fixture.CreateProductAsync("Hamburguesa", "10", token);
        await fixture.SetProductPreparationAsync(
            prepared.Id,
            Guid.CreateVersion7(),
            token);
        var key = Guid.NewGuid();
        var request = new FirstConfirmationRequest(
            "Mesa 7",
            [
                new FirstConfirmationItemRequest(direct.Id, 2),
                new FirstConfirmationItemRequest(prepared.Id, 1),
                new FirstConfirmationItemRequest(prepared.Id, 2, "sin cebolla")
            ]);

        using var response = await PostFirstAsync(request, key, token);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var confirmed = Assert.IsType<FirstConfirmationResponse>(
            await response.Content.ReadFromJsonAsync<FirstConfirmationResponse>(token));

        var states = await fixture.ReadDeliveryStatesAsync(token);
        Assert.Equal(3, states.Count);
        Assert.All(states, state => Assert.Equal(0, state.DeliveredQuantity));
        Assert.All(states, state =>
            Assert.Equal(confirmed.FirstIncorporation.Id, state.IncorporationId));
        Assert.Equal(3, states.Select(state => state.ContentOrdinal).Distinct().Count());
        Assert.Single(states, state => state.ProductId == direct.Id);
        var preparedStates = states.Where(state => state.ProductId == prepared.Id).ToArray();
        Assert.Equal(2, preparedStates.Length);
        Assert.Equal(
            [null, "sin cebolla"],
            preparedStates.Select(state => state.Instruction)
                .OrderBy(instruction => instruction is not null)
                .ToArray());
        Assert.Equal(2, await fixture.CountPreparationWorkAsync(token));

        using var replay = await PostFirstAsync(request, key, token);
        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        Assert.Equal(3, await fixture.CountDeliveryStatesAsync(token));
        Assert.Equal(2, await fixture.CountPreparationWorkAsync(token));
    }

    [Fact]
    public async Task Subsequent_confirmation_creates_independent_states_and_replay_does_not_duplicate()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var direct = await fixture.CreateProductAsync("Agua", "3", token);
        var prepared = await fixture.CreateProductAsync("Papas", "5", token);
        await fixture.SetProductPreparationAsync(
            prepared.Id,
            Guid.CreateVersion7(),
            token);
        using var firstResponse = await PostFirstAsync(
            new FirstConfirmationRequest(
                "Mesa 7",
                [new FirstConfirmationItemRequest(direct.Id, 1)]),
            Guid.NewGuid(),
            token);
        var first = Assert.IsType<FirstConfirmationResponse>(
            await firstResponse.Content.ReadFromJsonAsync<FirstConfirmationResponse>(token));
        var key = Guid.NewGuid();
        var request = new SubsequentConfirmationRequest(
            Guid.Empty,
            [
                new SubsequentConfirmationItemRequest(prepared.Id, 1),
                new SubsequentConfirmationItemRequest(prepared.Id, 1, "sin sal")
            ]);

        using var response = await PostSubsequentAsync(
            first.OperationalReference,
            request,
            key,
            token);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var subsequent = Assert.IsType<SubsequentConfirmationResponse>(
            await response.Content.ReadFromJsonAsync<SubsequentConfirmationResponse>(token));

        var states = await fixture.ReadDeliveryStatesAsync(token);
        Assert.Equal(3, states.Count);
        Assert.All(states, state => Assert.Equal(0, state.DeliveredQuantity));
        var subsequentStates = states
            .Where(state => state.IncorporationId == subsequent.Incorporation.Id)
            .ToArray();
        Assert.Equal(2, subsequentStates.Length);
        Assert.Equal(2, subsequentStates.Select(state => state.ContentOrdinal).Distinct().Count());
        Assert.All(subsequentStates, state => Assert.Equal(prepared.Id, state.ProductId));

        using var replay = await PostSubsequentAsync(
            first.OperationalReference,
            request,
            key,
            token);
        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        Assert.Equal(3, await fixture.CountDeliveryStatesAsync(token));
        Assert.Equal(2, await fixture.CountPreparationWorkAsync(token));
    }

    [Fact]
    public async Task Database_enforces_composite_identity_content_fk_and_nonnegative_quantity()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var product = await fixture.CreateProductAsync("Agua", "3", token);
        using var response = await PostFirstAsync(
            new FirstConfirmationRequest(
                "Mesa 7",
                [new FirstConfirmationItemRequest(product.Id, 1)]),
            Guid.NewGuid(),
            token);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var state = Assert.Single(await fixture.ReadDeliveryStatesAsync(token));

        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync(token);
        var definitions = await ReadConstraintDefinitionsAsync(connection, token);
        Assert.Contains(
            "PRIMARY KEY (incorporation_id, content_ordinal)",
            definitions["PK_order_operations_delivery_states"]);
        Assert.Contains(
            "delivered_quantity >= 0",
            definitions["CK_order_operations_delivery_states_delivered_non_negative"]);
        Assert.Contains(
            "FOREIGN KEY (incorporation_id, content_ordinal)",
            definitions["FK_order_operations_delivery_states_content"]);
        Assert.Contains(
            "REFERENCES order_operations.incorporation_contents" +
            "(incorporation_id, content_ordinal)",
            definitions["FK_order_operations_delivery_states_content"]);

        var negative = await Assert.ThrowsAsync<PostgresException>(async () =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                UPDATE order_operations.delivery_states
                SET delivered_quantity = -1
                WHERE incorporation_id = @incorporation_id
                  AND content_ordinal = @content_ordinal
                """;
            command.Parameters.AddWithValue("incorporation_id", state.IncorporationId);
            command.Parameters.AddWithValue("content_ordinal", state.ContentOrdinal);
            await command.ExecuteNonQueryAsync(token);
        });
        Assert.Equal(PostgresErrorCodes.CheckViolation, negative.SqlState);

        var duplicate = await Assert.ThrowsAsync<PostgresException>(async () =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO order_operations.delivery_states
                    (incorporation_id, content_ordinal, delivered_quantity)
                VALUES (@incorporation_id, @content_ordinal, 0)
                """;
            command.Parameters.AddWithValue("incorporation_id", state.IncorporationId);
            command.Parameters.AddWithValue("content_ordinal", state.ContentOrdinal);
            await command.ExecuteNonQueryAsync(token);
        });
        Assert.Equal(PostgresErrorCodes.UniqueViolation, duplicate.SqlState);

        var orphan = await Assert.ThrowsAsync<PostgresException>(async () =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO order_operations.delivery_states
                    (incorporation_id, content_ordinal, delivered_quantity)
                VALUES (@incorporation_id, 1, 0)
                """;
            command.Parameters.AddWithValue("incorporation_id", Guid.CreateVersion7());
            await command.ExecuteNonQueryAsync(token);
        });
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, orphan.SqlState);

        var identifiers = new[]
        {
            "delivery_states",
            "PK_order_operations_delivery_states",
            "CK_order_operations_delivery_states_delivered_non_negative",
            "FK_order_operations_delivery_states_content"
        };
        Assert.All(
            identifiers,
            identifier => Assert.InRange(Encoding.UTF8.GetByteCount(identifier), 1, 63));
    }

    [Fact]
    public async Task Delivery_state_failure_rolls_back_first_and_allows_exact_retry()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var product = await fixture.CreateProductAsync("Papas", "5", token);
        await fixture.SetProductPreparationAsync(product.Id, Guid.CreateVersion7(), token);
        var key = Guid.NewGuid();
        var request = new FirstConfirmationRequest(
            "Mesa 7",
            [new FirstConfirmationItemRequest(product.Id, 2)]);
        await fixture.SetDeliveryStateFailureAsync(true, token);

        try
        {
            using var failed = await PostFirstAsync(request, key, token);
            Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
            Assert.Equal(PersistenceCounts.Empty, await fixture.CountEffectsAsync(token));
            Assert.Equal(0, await fixture.CountPreparationWorkAsync(token));
            Assert.Equal(0, await fixture.CountDeliveryStatesAsync(token));
        }
        finally
        {
            await fixture.SetDeliveryStateFailureAsync(false, token);
        }

        using var retry = await PostFirstAsync(request, key, token);
        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
        Assert.Equal(1, await fixture.CountPreparationWorkAsync(token));
        Assert.Equal(1, await fixture.CountDeliveryStatesAsync(token));
    }

    [Fact]
    public async Task Delivery_state_failure_rolls_back_only_subsequent_confirmation_effects()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        var product = await fixture.CreateProductAsync("Agua", "3", token);
        using var firstResponse = await PostFirstAsync(
            new FirstConfirmationRequest(
                "Mesa 7",
                [new FirstConfirmationItemRequest(product.Id, 1)]),
            Guid.NewGuid(),
            token);
        var first = Assert.IsType<FirstConfirmationResponse>(
            await firstResponse.Content.ReadFromJsonAsync<FirstConfirmationResponse>(token));
        await fixture.SetProductPreparationAsync(product.Id, Guid.CreateVersion7(), token);
        await fixture.SetDeliveryStateFailureAsync(true, token);

        try
        {
            using var failed = await PostSubsequentAsync(
                first.OperationalReference,
                new SubsequentConfirmationRequest(
                    Guid.Empty,
                    [new SubsequentConfirmationItemRequest(product.Id, 2)]),
                Guid.NewGuid(),
                token);
            Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
            Assert.Equal(
                new SubsequentPersistenceCounts(1, 1, 1, 0, 0),
                await fixture.CountSubsequentEffectsAsync(token));
            Assert.Equal(1, await fixture.CountDeliveryStatesAsync(token));
            Assert.Equal(0, await fixture.CountPreparationWorkAsync(token));
        }
        finally
        {
            await fixture.SetDeliveryStateFailureAsync(false, token);
        }
    }

    private async Task<HttpResponseMessage> PostFirstAsync(
        FirstConfirmationRequest request,
        Guid key,
        CancellationToken token)
    {
        using var message = new HttpRequestMessage(
            HttpMethod.Post,
            "/api/order-operations/first-confirmations")
        {
            Content = JsonContent.Create(request)
        };
        message.Headers.Add("Idempotency-Key", key.ToString("D"));
        return await OrderOperationsApiFixture.SendWithAntiforgeryAsync(
            fixture.OrderOperationsClient,
            message,
            token);
    }

    private async Task<HttpResponseMessage> PostSubsequentAsync(
        string operationalReference,
        SubsequentConfirmationRequest request,
        Guid key,
        CancellationToken token)
    {
        if (!pendingCompositionByConfirmationKey.TryGetValue(key, out var pendingCompositionId))
        {
            var pending = await fixture.GetOrStartPendingCompositionAsync(
                operationalReference,
                token);
            pendingCompositionId = pending.PendingCompositionId;
            pendingCompositionByConfirmationKey[key] = pendingCompositionId;
        }
        using var message = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/order-operations/orders/{operationalReference}/confirmations")
        {
            Content = JsonContent.Create(request with
            {
                PendingCompositionId = pendingCompositionId
            })
        };
        message.Headers.Add("Idempotency-Key", key.ToString("D"));
        return await OrderOperationsApiFixture.SendWithAntiforgeryAsync(
            fixture.OrderOperationsClient,
            message,
            token);
    }

    private static async Task<Dictionary<string, string>> ReadConstraintDefinitionsAsync(
        NpgsqlConnection connection,
        CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT conname, pg_get_constraintdef(oid)
            FROM pg_constraint
            WHERE conrelid = 'order_operations.delivery_states'::regclass
            """;
        await using var reader = await command.ExecuteReaderAsync(token);
        var definitions = new Dictionary<string, string>(StringComparer.Ordinal);
        while (await reader.ReadAsync(token))
        {
            definitions.Add(reader.GetString(0), reader.GetString(1));
        }

        return definitions;
    }
}
