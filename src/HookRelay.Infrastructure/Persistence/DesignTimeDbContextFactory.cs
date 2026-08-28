using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace HookRelay.Infrastructure.Persistence;

/// <summary>
/// Lets the EF tooling build a context without booting an application.
/// </summary>
/// <remarks>
/// The connection string here is only ever used to generate migration files. It is never opened, so it
/// points at nothing real and carries no credentials worth having.
/// </remarks>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<HookRelayDbContext>
{
    /// <inheritdoc />
    public HookRelayDbContext CreateDbContext(string[] args)
    {
        DbContextOptions<HookRelayDbContext> options =
            new DbContextOptionsBuilder<HookRelayDbContext>()
                .UseNpgsql("Host=localhost;Database=hookrelay;Username=postgres")
                .UseSnakeCaseNamingConvention()
                .Options;

        return new HookRelayDbContext(options);
    }
}
