using Microsoft.EntityFrameworkCore;

namespace Horica.ClaimsService.Infrastructure.Persistence;

public static class PersistenceServiceCollectionExtensions
{
    public static IServiceCollection AddClaimsPersistence(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("ClaimsDb");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "Connection string 'ClaimsDb' is not configured. Set ConnectionStrings:ClaimsDb (env var ConnectionStrings__ClaimsDb).");
        }

        services.AddDbContext<ClaimsDbContext>(options =>
            options.UseNpgsql(connectionString, npgsql => npgsql.EnableRetryOnFailure()));

        services.AddScoped<IClaimIdGenerator, SequenceClaimIdGenerator>();

        return services;
    }
}
