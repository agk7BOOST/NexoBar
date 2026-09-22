using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace NexoBar.OperationalConfiguration.Migrations;

partial class AddOperationalContexts
{
    protected override void BuildTargetModel(ModelBuilder modelBuilder)
        => OperationalConfigurationDbContextModelSnapshot.BuildLatestModel(modelBuilder);
}
