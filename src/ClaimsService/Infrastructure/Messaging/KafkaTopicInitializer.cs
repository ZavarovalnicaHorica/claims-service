using Confluent.Kafka;
using Confluent.Kafka.Admin;
using Microsoft.Extensions.Options;

namespace Horica.ClaimsService.Infrastructure.Messaging;

/// <summary>
/// Makes sure the topics exist before anybody uses them. Retries until Kafka is reachable,
/// so the service can start before the broker is ready.
/// </summary>
public sealed class KafkaTopicInitializer
{
    private readonly KafkaOptions _options;
    private readonly ILogger<KafkaTopicInitializer> _logger;
    private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
    private bool _done;

    public KafkaTopicInitializer(IOptions<KafkaOptions> options, ILogger<KafkaTopicInitializer> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public async Task EnsureTopicsAsync(CancellationToken cancellationToken)
    {
        if (_done || !_options.EnsureTopics)
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_done)
            {
                return;
            }

            var delaySeconds = 2.0;

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    await CreateTopicsOnceAsync();
                    _done = true;
                    _logger.LogInformation("Kafka topics are ready.");
                    return;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(
                        "Kafka is not ready yet ({Reason}). Retrying in {Delay} seconds.",
                        ex.Message,
                        delaySeconds);

                    await Task.Delay(TimeSpan.FromSeconds(delaySeconds), cancellationToken);
                    delaySeconds = Math.Min(30.0, delaySeconds * 2);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task CreateTopicsOnceAsync()
    {
        var names = new List<string>();
        names.AddRange(_options.PublishedTopics);
        names.AddRange(_options.ConsumedTopics);
        names.AddRange(_options.ConsumedTopics.Select(KafkaOptions.DeadLetterTopicFor));

        var specifications = names
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct()
            .Select(name => new TopicSpecification
            {
                Name = name,
                NumPartitions = _options.TopicPartitions,
                ReplicationFactor = _options.TopicReplicationFactor
            })
            .ToList();

        using var admin = new AdminClientBuilder(KafkaConfigFactory.CreateAdminConfig(_options)).Build();

        try
        {
            await admin.CreateTopicsAsync(
                specifications,
                new CreateTopicsOptions { RequestTimeout = TimeSpan.FromSeconds(15) });
        }
        catch (CreateTopicsException ex)
        {
            var realFailures = ex.Results
                .Where(r => r.Error.IsError && r.Error.Code != ErrorCode.TopicAlreadyExists)
                .ToList();

            if (realFailures.Count > 0)
            {
                throw;
            }
        }
    }
}
