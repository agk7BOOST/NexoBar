using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace NexoBar.OrderOperations.Migrations;

partial class AddConfiguredOrderContexts
{
    protected override void BuildTargetModel(ModelBuilder modelBuilder)
        => OrderOperationsDbContextModelSnapshot.BuildLatestModel(modelBuilder);
}
