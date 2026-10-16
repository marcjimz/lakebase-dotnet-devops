using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Lakebase;

public static class LakebaseDbContextConfiguration
{
    /// <summary>
    /// Registers one process-wide <see cref="NpgsqlDataSource"/> (pool + credential rotation) and a
    /// DbContext that uses it. Building the data source inside the DbContext options callback would
    /// create a new pool and mint a new credential for every request scope.
    /// </summary>
    public static IServiceCollection AddLakebaseDbContext<TContext>(
        this IServiceCollection services,
        DatabaseConnectionSettings settings)
        where TContext : DbContext
    {
        services.TryAddSingleton(_ => LakebaseConnectionFactory.CreateDataSource(settings));
        services.AddDbContext<TContext>((sp, options) =>
            options.UseNpgsql(sp.GetRequiredService<NpgsqlDataSource>()));
        return services;
    }
}
