using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NexoBar.OrderOperations;
using Npgsql;

namespace NexoBar.OrderOperations.IntegrationTests;

[Collection(OrderOperationsApiCollection.Name)]
public sealed class IncorporationContentMigrationTests(OrderOperationsApiFixture fixture)
{
    private const string PreviousMigration = "20260830150000_AddPreparationWork";
    private const string CurrentMigration =
        "20260830210000_ReidentifyIncorporationContent";
    private const string LatestMigration =
        "20260831171256_AddDeliveryState";

    private static readonly Guid OrderId =
        Guid.Parse("01910000-0000-7000-8000-000000000001");
    private static readonly Guid FirstIncorporationId =
        Guid.Parse("01910000-0000-7000-8000-000000000002");
    private static readonly Guid SubsequentIncorporationId =
        Guid.Parse("01910000-0000-7000-8000-000000000003");
    private static readonly Guid ProductA =
        Guid.Parse("01910000-0000-7000-8000-000000000010");
    private static readonly Guid ProductB =
        Guid.Parse("01910000-0000-7000-8000-000000000020");
    private static readonly Guid ProductC =
        Guid.Parse("01910000-0000-7000-8000-000000000030");
    private static readonly Guid PreparationResponsibilityId =
        Guid.Parse("01910000-0000-7000-8000-000000000040");
    private static readonly Guid WorkId =
        Guid.Parse("01910000-0000-7000-8000-000000000050");
    private static readonly Guid FirstCommandKey =
        Guid.Parse("11111111-1111-4111-8111-111111111111");
    private static readonly Guid SubsequentCommandKey =
        Guid.Parse("22222222-2222-4222-8222-222222222222");

    [Fact]
    public async Task Migration_preserves_previous_confirmation_data_and_replay()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(token);
        await fixture.MigrateOrderOperationsAsync(PreviousMigration, token);

        try
        {
            await SeedPreviousSchemaAsync(token);

            await fixture.MigrateOrderOperationsAsync(CurrentMigration, token);
            await AssertPhysicalStructureAsync(token);
            await fixture.MigrateOrderOperationsAsync(LatestMigration, token);
            await AssertMigratedDataAsync(token);

            await fixture.MigrateOrderOperationsAsync(PreviousMigration, token);
            await AssertRevertedStructureAndDataAsync(token);

            await fixture.MigrateOrderOperationsAsync(CurrentMigration, token);
            await fixture.MigrateOrderOperationsAsync(LatestMigration, token);

            await AssertMigratedDataAsync(token);
            await AssertReplayAndQueriesAsync(token);
        }
        finally
        {
            await fixture.MigrateOrderOperationsAsync(LatestMigration, token);
            await fixture.ResetAsync(token);
        }
    }

    private async Task SeedPreviousSchemaAsync(CancellationToken token)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO catalog.products
                (id, operational_name, price, is_active, is_available,
                 requires_preparation, preparation_responsibility_id)
            VALUES
                (@product_a, 'Producto A', 10, TRUE, TRUE, FALSE, NULL),
                (@product_b, 'Producto B', 20, TRUE, TRUE, TRUE,
                 @responsibility_id),
                (@product_c, 'Producto C', 30, TRUE, TRUE, FALSE, NULL);

            INSERT INTO order_operations.orders (id, context)
            VALUES (@order_id, 'Mesa 7');

            INSERT INTO order_operations.incorporations (id, order_id, ordinal)
            VALUES
                (@first_incorporation_id, @order_id, 1),
                (@subsequent_incorporation_id, @order_id, 2);

            INSERT INTO order_operations.confirmation_history
                (id, incorporation_id, confirmed_context, occurred_at)
            VALUES
                (@first_history_id, @first_incorporation_id, 'Mesa 7', @first_at),
                (@subsequent_history_id, @subsequent_incorporation_id,
                    'Mesa 7', @subsequent_at);

            INSERT INTO order_operations.incorporation_contents
                (incorporation_id, product_id, quantity, applied_price)
            VALUES
                (@first_incorporation_id, @product_b, 3, 8.50),
                (@first_incorporation_id, @product_a, 2, 5.25),
                (@subsequent_incorporation_id, @product_c, 5, 12.00),
                (@subsequent_incorporation_id, @product_a, 4, 6.00);

            INSERT INTO order_operations.preparation_work
                (id, incorporation_id, product_id,
                 preparation_responsibility_id, total_quantity,
                 pending_quantity, in_preparation_quantity, ready_quantity)
            VALUES
                (@work_id, @first_incorporation_id, @product_b,
                 @responsibility_id, 3, 3, 0, 0);

            INSERT INTO order_operations.first_confirmation_commands
                (idempotency_key, intent_context, result_incorporation_id)
            VALUES (@first_key, 'Mesa 7', @first_incorporation_id);

            INSERT INTO order_operations.first_confirmation_command_contents
                (idempotency_key, product_id, quantity)
            VALUES
                (@first_key, @product_b, 3),
                (@first_key, @product_a, 2);

            INSERT INTO order_operations.subsequent_confirmation_commands
                (idempotency_key, intent_order_id, result_incorporation_id)
            VALUES (@subsequent_key, @order_id, @subsequent_incorporation_id);

            INSERT INTO order_operations.subsequent_confirmation_command_contents
                (idempotency_key, product_id, quantity)
            VALUES
                (@subsequent_key, @product_c, 5),
                (@subsequent_key, @product_a, 4);
            """;
        command.Parameters.AddWithValue("order_id", OrderId);
        command.Parameters.AddWithValue("first_incorporation_id", FirstIncorporationId);
        command.Parameters.AddWithValue(
            "subsequent_incorporation_id",
            SubsequentIncorporationId);
        command.Parameters.AddWithValue(
            "first_history_id",
            Guid.Parse("01910000-0000-7000-8000-000000000060"));
        command.Parameters.AddWithValue(
            "subsequent_history_id",
            Guid.Parse("01910000-0000-7000-8000-000000000070"));
        command.Parameters.AddWithValue(
            "first_at",
            new DateTimeOffset(2026, 8, 29, 12, 0, 0, TimeSpan.Zero));
        command.Parameters.AddWithValue(
            "subsequent_at",
            new DateTimeOffset(2026, 8, 29, 12, 5, 0, TimeSpan.Zero));
        command.Parameters.AddWithValue("product_a", ProductA);
        command.Parameters.AddWithValue("product_b", ProductB);
        command.Parameters.AddWithValue("product_c", ProductC);
        command.Parameters.AddWithValue("work_id", WorkId);
        command.Parameters.AddWithValue("responsibility_id", PreparationResponsibilityId);
        command.Parameters.AddWithValue("first_key", FirstCommandKey);
        command.Parameters.AddWithValue("subsequent_key", SubsequentCommandKey);
        await command.ExecuteNonQueryAsync(token);
    }

    private async Task AssertMigratedDataAsync(CancellationToken token)
    {
        await using var scope = fixture.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OrderOperationsDbContext>();

        var contents = await dbContext.IncorporationContents.AsNoTracking()
            .OrderBy(content => content.IncorporationId)
            .ThenBy(content => content.ContentOrdinal)
            .ToArrayAsync(token);
        Assert.Equal(4, contents.Length);
        Assert.All(contents, content => Assert.True(content.ContentOrdinal > 0));
        Assert.Equal(
            [(FirstIncorporationId, 1, ProductA), (FirstIncorporationId, 2, ProductB),
             (SubsequentIncorporationId, 1, ProductA),
             (SubsequentIncorporationId, 2, ProductC)],
            contents.Select(content => (
                content.IncorporationId,
                content.ContentOrdinal,
                content.ProductId)).ToArray());

        var work = Assert.Single(await fixture.ReadPreparationWorkAsync(token));
        Assert.Equal(WorkId, work.Id);
        Assert.Equal(FirstIncorporationId, work.IncorporationId);
        Assert.Equal(2, work.ContentOrdinal);
        Assert.Equal(ProductB, work.ProductId);
        Assert.Equal(PreparationResponsibilityId, work.PreparationResponsibilityId);
        Assert.Equal(3, work.TotalQuantity);
        Assert.Equal(3, work.PendingQuantity);
        Assert.Equal(0, work.InPreparationQuantity);
        Assert.Equal(0, work.ReadyQuantity);

        var firstLines = await dbContext.FirstConfirmationCommandContents.AsNoTracking()
            .OrderBy(content => content.LineOrdinal)
            .ToArrayAsync(token);
        Assert.Equal(
            [(1, ProductA, 2), (2, ProductB, 3)],
            firstLines.Select(content => (
                content.LineOrdinal,
                content.ProductId,
                content.Quantity)).ToArray());

        var subsequentLines = await dbContext.SubsequentConfirmationCommandContents
            .AsNoTracking()
            .OrderBy(content => content.LineOrdinal)
            .ToArrayAsync(token);
        Assert.Equal(
            [(1, ProductA, 4), (2, ProductC, 5)],
            subsequentLines.Select(content => (
                content.LineOrdinal,
                content.ProductId,
                content.Quantity)).ToArray());
    }

    private async Task AssertPhysicalStructureAsync(CancellationToken token)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync(token);

        var constraints = await ReadDefinitionsAsync(
            connection,
            """
            SELECT conname, pg_get_constraintdef(oid)
            FROM pg_constraint
            WHERE connamespace = 'order_operations'::regnamespace
              AND conrelid IN (
                  'order_operations.incorporation_contents'::regclass,
                  'order_operations.preparation_work'::regclass,
                  'order_operations.first_confirmation_command_contents'::regclass,
                  'order_operations.subsequent_confirmation_command_contents'::regclass)
            """,
            token);
        Assert.Contains(
            "PRIMARY KEY (incorporation_id, content_ordinal)",
            constraints["PK_order_operations_incorporation_contents"]);
        Assert.Contains(
            "content_ordinal > 0",
            constraints[
                "CK_order_operations_incorporation_contents_ordinal_positive"]);
        Assert.Contains(
            "FOREIGN KEY (incorporation_id, content_ordinal)",
            constraints["FK_order_operations_preparation_work_content"]);
        Assert.Contains(
            "PRIMARY KEY (id)",
            constraints["PK_order_operations_preparation_work"]);
        Assert.Contains(
            "REFERENCES order_operations.incorporation_contents" +
            "(incorporation_id, content_ordinal)",
            constraints["FK_order_operations_preparation_work_content"]);
        Assert.Contains(
            "PRIMARY KEY (idempotency_key, line_ordinal)",
            constraints[
                "PK_order_operations_first_confirmation_command_contents"]);
        Assert.Contains(
            "line_ordinal > 0",
            constraints[
                "CK_order_operations_first_command_contents_ordinal_positive"]);
        Assert.Contains(
            "PRIMARY KEY (idempotency_key, line_ordinal)",
            constraints[
                "PK_order_operations_subsequent_confirmation_command_contents"]);
        Assert.Contains(
            "line_ordinal > 0",
            constraints[
                "CK_order_operations_subseq_command_contents_ordinal_positive"]);

        var indexes = await ReadDefinitionsAsync(
            connection,
            """
            SELECT indexname, indexdef
            FROM pg_indexes
            WHERE schemaname = 'order_operations'
            """,
            token);
        Assert.Contains(
            "UNIQUE INDEX",
            indexes["UX_order_operations_incorporation_contents_product"]);
        Assert.Contains(
            "(incorporation_id, product_id)",
            indexes["UX_order_operations_incorporation_contents_product"]);
        Assert.Contains(
            "(incorporation_id, content_ordinal)",
            indexes["UX_order_operations_preparation_work_content"]);
        Assert.Contains(
            "UNIQUE INDEX",
            indexes["UX_order_operations_preparation_work_content"]);
        Assert.Contains(
            "(idempotency_key, product_id)",
            indexes["UX_order_operations_first_command_contents_product"]);
        Assert.Contains(
            "UNIQUE INDEX",
            indexes["UX_order_operations_first_command_contents_product"]);
        Assert.Contains(
            "(idempotency_key, product_id)",
            indexes["UX_order_operations_subsequent_command_contents_product"]);
        Assert.Contains(
            "UNIQUE INDEX",
            indexes["UX_order_operations_subsequent_command_contents_product"]);

        Assert.False(await ColumnExistsAsync(
            connection,
            "preparation_work",
            "product_id",
            token));

        var introducedIdentifiers = new[]
        {
            "CK_order_operations_incorporation_contents_ordinal_positive",
            "UX_order_operations_incorporation_contents_product",
            "CK_order_operations_first_command_contents_ordinal_positive",
            "UX_order_operations_first_command_contents_product",
            "CK_order_operations_subseq_command_contents_ordinal_positive",
            "UX_order_operations_subsequent_command_contents_product"
        };
        Assert.All(
            introducedIdentifiers,
            identifier => Assert.InRange(Encoding.UTF8.GetByteCount(identifier), 1, 63));
    }

    private async Task AssertRevertedStructureAndDataAsync(CancellationToken token)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync(token);

        Assert.True(await ColumnExistsAsync(
            connection,
            "preparation_work",
            "product_id",
            token));
        Assert.False(await ColumnExistsAsync(
            connection,
            "preparation_work",
            "content_ordinal",
            token));
        Assert.False(await ColumnExistsAsync(
            connection,
            "incorporation_contents",
            "content_ordinal",
            token));
        Assert.False(await ColumnExistsAsync(
            connection,
            "first_confirmation_command_contents",
            "line_ordinal",
            token));
        Assert.False(await ColumnExistsAsync(
            connection,
            "subsequent_confirmation_command_contents",
            "line_ordinal",
            token));

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT count(*)
            FROM order_operations.preparation_work AS work
            JOIN order_operations.incorporation_contents AS content
              ON content.incorporation_id = work.incorporation_id
             AND content.product_id = work.product_id
            WHERE work.id = @work_id
              AND content.product_id = @product_id
            """;
        command.Parameters.AddWithValue("work_id", WorkId);
        command.Parameters.AddWithValue("product_id", ProductB);
        Assert.Equal(1L, Assert.IsType<long>(await command.ExecuteScalarAsync(token)));
    }

    private async Task AssertReplayAndQueriesAsync(CancellationToken token)
    {
        using var firstRequest = new HttpRequestMessage(
            HttpMethod.Post,
            "/api/order-operations/first-confirmations")
        {
            Content = JsonContent.Create(new FirstConfirmationRequest(
                "Mesa 7",
                [
                    new FirstConfirmationItemRequest(ProductB, 3),
                    new FirstConfirmationItemRequest(ProductA, 2)
                ]))
        };
        firstRequest.Headers.Add("Idempotency-Key", FirstCommandKey.ToString("D"));
        using var firstResponse = await fixture.Client.SendAsync(firstRequest, token);
        Assert.Equal(HttpStatusCode.Created, firstResponse.StatusCode);
        var first = Assert.IsType<FirstConfirmationResponse>(
            await firstResponse.Content.ReadFromJsonAsync<FirstConfirmationResponse>(token));
        Assert.Equal(OrderId.ToString("D"), first.OperationalReference);
        Assert.Equal(
            [ProductA, ProductB],
            first.FirstIncorporation.Items.Select(item => item.ProductId).ToArray());

        using var subsequentRequest = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/order-operations/orders/{OrderId:D}/confirmations")
        {
            Content = JsonContent.Create(new SubsequentConfirmationRequest(
                [
                    new SubsequentConfirmationItemRequest(ProductC, 5),
                    new SubsequentConfirmationItemRequest(ProductA, 4)
                ]))
        };
        subsequentRequest.Headers.Add(
            "Idempotency-Key",
            SubsequentCommandKey.ToString("D"));
        using var subsequentResponse = await fixture.Client.SendAsync(
            subsequentRequest,
            token);
        Assert.Equal(HttpStatusCode.Created, subsequentResponse.StatusCode);
        var subsequent = Assert.IsType<SubsequentConfirmationResponse>(
            await subsequentResponse.Content
                .ReadFromJsonAsync<SubsequentConfirmationResponse>(token));
        Assert.Equal(2, subsequent.Incorporation.Ordinal);
        Assert.Equal(
            [ProductA, ProductC],
            subsequent.Incorporation.Items.Select(item => item.ProductId).ToArray());

        using var orderResponse = await fixture.Client.GetAsync(
            $"/api/order-operations/orders/{OrderId:D}",
            token);
        orderResponse.EnsureSuccessStatusCode();
        var order = Assert.IsType<OrderQueryResponse>(
            await orderResponse.Content.ReadFromJsonAsync<OrderQueryResponse>(token));
        Assert.Equal(2, order.Incorporations.Count);
        Assert.Equal(
            [ProductA, ProductB],
            order.Incorporations[0].Items.Select(item => item.ProductId).ToArray());
        Assert.Equal(
            [ProductA, ProductC],
            order.Incorporations[1].Items.Select(item => item.ProductId).ToArray());

        var actor = await fixture.CreatePreparationActorAsync(
            hasPreparation: true,
            PreparationResponsibilityId,
            token);
        using var preparationClient = await fixture.LoginAsync(actor, token);
        using var preparationResponse = await preparationClient.GetAsync(
            "/api/order-operations/preparation/work" +
            $"?preparationResponsibilityId={PreparationResponsibilityId:D}",
            token);
        preparationResponse.EnsureSuccessStatusCode();
        var preparation = Assert.Single(Assert.IsType<PreparationWorkResponse[]>(
            await preparationResponse.Content
                .ReadFromJsonAsync<PreparationWorkResponse[]>(token)));
        Assert.Equal(WorkId, preparation.WorkId);
        Assert.Equal(ProductB, preparation.ProductId);
        Assert.Equal(3, preparation.TotalQuantity);
        Assert.Equal(1, await fixture.CountPreparationWorkAsync(token));
    }

    private static async Task<Dictionary<string, string>> ReadDefinitionsAsync(
        NpgsqlConnection connection,
        string sql,
        CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync(token);
        var definitions = new Dictionary<string, string>(StringComparer.Ordinal);
        while (await reader.ReadAsync(token))
        {
            definitions.Add(reader.GetString(0), reader.GetString(1));
        }

        return definitions;
    }

    private static async Task<bool> ColumnExistsAsync(
        NpgsqlConnection connection,
        string table,
        string column,
        CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT EXISTS (
                SELECT 1
                FROM information_schema.columns
                WHERE table_schema = 'order_operations'
                  AND table_name = @table
                  AND column_name = @column)
            """;
        command.Parameters.AddWithValue("table", table);
        command.Parameters.AddWithValue("column", column);
        return Assert.IsType<bool>(await command.ExecuteScalarAsync(token));
    }
}
