using System.Globalization;
using Microsoft.EntityFrameworkCore;

namespace Horica.ClaimsService.Infrastructure.Persistence;

public interface IClaimIdGenerator
{
    Task<string> NextAsync(CancellationToken cancellationToken);
}

/// <summary>Claim ids "CLM-1001", "CLM-1002" ... from a PostgreSQL sequence (unique even with several replicas).</summary>
public sealed class SequenceClaimIdGenerator : IClaimIdGenerator
{
    private readonly ClaimsDbContext _db;

    public SequenceClaimIdGenerator(ClaimsDbContext db)
    {
        _db = db;
    }

    public async Task<string> NextAsync(CancellationToken cancellationToken)
    {
        var values = await _db.Database
            .SqlQueryRaw<long>("SELECT nextval('" + ClaimsDbContext.ClaimNumberSequence + "') AS \"Value\"")
            .ToListAsync(cancellationToken);

        return "CLM-" + values[0].ToString(CultureInfo.InvariantCulture);
    }
}
