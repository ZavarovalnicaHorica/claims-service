using System.Globalization;
using System.Text;
using Confluent.Kafka;
using Horica.ClaimsService.Application;
using Horica.ClaimsService.Common;
using Microsoft.Extensions.Options;

namespace Horica.ClaimsService.Infrastructure.Messaging;

/// <summary>
/// Consumes claim.recommendation.created.v1, review.completed.v1 and document.added.v1 and updates the claims.
/// Offsets are committed only after the change has been stored (at-least-once); duplicates are ignored by the handler.
/// Unparseable messages go to "&lt;topic&gt;.dlq"; other failures (e.g. database down) are retried.
/// </summary>
public sealed class ClaimsEventConsumer : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IProducer<string, string> _producer;
    private readonly KafkaTopicInitializer _topics;
    private readonly KafkaOptions _options;
    private readonly ILogger<ClaimsEventConsumer> _logger;

    public ClaimsEventConsumer(
        IServiceScopeFactory scopeFactory,
        IProducer<string, string> producer,
        KafkaTopicInitializer topics,
        IOptions<KafkaOptions> options,
        ILogger<ClaimsEventConsumer> logger)
    {
        _scopeFactory = scopeFactory;
        _producer = producer;
        _topics = topics;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Let the host finish starting before doing any work.
        await Task.Yield();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunSessionAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "The Kafka consumer stopped unexpectedly; restarting in 5 seconds.");

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private async Task RunSessionAsync(CancellationToken cancellationToken)
    {
        await _topics.EnsureTopicsAsync(cancellationToken);

        using var consumer = new ConsumerBuilder<string, string>(KafkaConfigFactory.CreateConsumerConfig(_options))
            .SetErrorHandler((_, error) => _logger.LogWarning("Kafka consumer: {Reason}", error.Reason))
            .Build();

        var topics = _options.ConsumedTopics.Where(t => !string.IsNullOrWhiteSpace(t)).Distinct().ToList();
        consumer.Subscribe(topics);
        _logger.LogInformation(
            "Consuming {Topics} as group '{Group}'.",
            string.Join(", ", topics),
            _options.ConsumerGroupId);

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                ConsumeResult<string, string>? result;

                try
                {
                    result = consumer.Consume(TimeSpan.FromMilliseconds(500));
                }
                catch (ConsumeException ex) when (!ex.Error.IsFatal)
                {
                    _logger.LogWarning("Kafka consume error: {Reason}", ex.Error.Reason);
                    await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
                    continue;
                }

                if (result?.Message is null)
                {
                    continue;
                }

                await ProcessAsync(result, cancellationToken);
                TryCommit(consumer, result);
            }
        }
        finally
        {
            try
            {
                consumer.Close();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Closing the Kafka consumer failed.");
            }
        }
    }

    private async Task ProcessAsync(ConsumeResult<string, string> result, CancellationToken cancellationToken)
    {
        var fallbackEventId = "kafka:" + result.Topic + ":" +
                              result.Partition.Value.ToString(CultureInfo.InvariantCulture) + ":" +
                              result.Offset.Value.ToString(CultureInfo.InvariantCulture);

        var attempt = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                using var scope = _scopeFactory.CreateScope();
                var handler = scope.ServiceProvider.GetRequiredService<IncomingEventHandler>();
                await handler.HandleAsync(result.Topic, result.Message.Value, fallbackEventId, cancellationToken);
                return;
            }
            catch (InvalidMessageException ex)
            {
                _logger.LogError("Discarding invalid message {MessageId}: {Reason}", fallbackEventId, ex.Message);
                await SendToDeadLetterAsync(result, ex.Message, cancellationToken);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                attempt++;
                var delaySeconds = Math.Min(30.0, Math.Pow(2, Math.Min(attempt, 5)));
                _logger.LogError(
                    ex,
                    "Processing {MessageId} failed (attempt {Attempt}); retrying in {Delay} seconds.",
                    fallbackEventId,
                    attempt,
                    delaySeconds);

                await Task.Delay(TimeSpan.FromSeconds(delaySeconds), cancellationToken);
            }
        }
    }

    private async Task SendToDeadLetterAsync(ConsumeResult<string, string> result, string reason, CancellationToken cancellationToken)
    {
        try
        {
            var message = new Message<string, string>
            {
                Key = result.Message.Key,
                Value = result.Message.Value,
                Headers = new Headers
                {
                    { "x-error", Encoding.UTF8.GetBytes(TextUtil.Truncate(reason, 500)) },
                    { "x-source-topic", Encoding.UTF8.GetBytes(result.Topic) },
                    { "x-source-partition", Encoding.UTF8.GetBytes(result.Partition.Value.ToString(CultureInfo.InvariantCulture)) },
                    { "x-source-offset", Encoding.UTF8.GetBytes(result.Offset.Value.ToString(CultureInfo.InvariantCulture)) }
                }
            };

            await _producer.ProduceAsync(KafkaOptions.DeadLetterTopicFor(result.Topic), message, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Could not copy the invalid message to the dead-letter topic.");
        }
    }

    private void TryCommit(IConsumer<string, string> consumer, ConsumeResult<string, string> result)
    {
        try
        {
            consumer.Commit(result);
        }
        catch (KafkaException ex)
        {
            _logger.LogWarning("Offset commit failed ({Reason}); the message may be delivered again.", ex.Message);
        }
    }
}
