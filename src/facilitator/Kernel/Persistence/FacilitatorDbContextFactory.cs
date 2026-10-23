using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Facilitator.Kernel.Persistence;

/// <summary>
/// Design-time factory for <c>dotnet ef</c> (migrations add / database update)
/// — no host needed; the connection string only matters for
/// <c>database update</c>, never for <c>migrations add</c>.
/// </summary>
public sealed class FacilitatorDbContextFactory : IDesignTimeDbContextFactory<FacilitatorDbContext>
{
    /// <inheritdoc />
    public FacilitatorDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Facilitator")
            ?? "Host=localhost;Database=facilitator;Username=postgres;Password=postgres";

        var options = new DbContextOptionsBuilder<FacilitatorDbContext>()
            .UseNpgsql(connectionString)
            .Options;

        return new FacilitatorDbContext(options);
    }
}
