namespace Horica.ClaimsService.Infrastructure.Messaging;

/// <summary>The incoming message can never be processed (bad JSON, missing claimId, ...). Retrying will not help.</summary>
public sealed class InvalidMessageException : Exception
{
    public InvalidMessageException(string message)
        : base(message)
    {
    }

    public InvalidMessageException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
