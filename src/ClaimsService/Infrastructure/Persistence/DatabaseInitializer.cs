using Microsoft.EntityFrameworkCore;

namespace Horica.ClaimsService.Infrastructure.Persistence;

/// <summary>
/// Creates the schema on startup (Database:AutoCreate, default true) and waits for PostgreSQL to accept connections.
/// The database must belong to this service only: EnsureCreated does nothing when it already has tables.
/// </summary>
public static class DatabaseInitializer
{
    public static async Task InitializeAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        var configuration = services.GetRequiredService<IConfiguration>();
        if (!configuration.GetValue("Database:AutoCreate", true))
        {
            return;
        }

        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("DatabaseInitializer");
        const int maxAttempts = 30;

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var scope = services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ClaimsDbContext>();
                await db.Database.EnsureCreatedAsync(cancellationToken);
                logger.LogInformation("Database is ready.");
                return;
            }
            catch (Exception ex) when (attempt < maxAttempts && ex is not OperationCanceledException)
            {
                logger.LogWarning(
                    ex,
                    "Database is not ready yet (attempt {Attempt}/{MaxAttempts}). Retrying in 2 seconds.",
                    attempt,
                    maxAttempts);
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            }
        }
    }
}
