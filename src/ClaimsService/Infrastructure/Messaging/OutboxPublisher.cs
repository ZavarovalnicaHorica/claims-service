using System.Text;
using Confluent.Kafka;
using Horica.ClaimsService.Common;
using Horica.ClaimsService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Horica.ClaimsService.Infrastructure.Messaging;

/// <summary>
/// Transactional outbox: publishes rows from outbox_messages to Kafka in creation order.
/// Delivery is at-least-once; consumers de-duplicate using the CloudEvents id.
/// </summary>
public sealed class OutboxPublisher : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IProducer<string, string> _producer;
    private readonly KafkaTopicInitializer _topics;
    private readonly KafkaOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<OutboxPublisher> _logger;

    public OutboxPublisher(
        IServiceScopeFactory scopeFactory,
        IProducer<string, string> producer,
        KafkaTopicInitializer topics,
        IOptions<KafkaOptions> options,
        TimeProvider time,
        ILogger<OutboxPublisher> logger)
    {
        _scopeFactory = scopeFactory;
        _producer = producer;
        _topics = topics;
        _options = options.Value;
        _time = time;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Let the host finish starting before doing any work.
        await Task.Yield();

        try
        {
            await _topics.EnsureTopicsAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        _logger.LogInformation("Outbox publisher started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            var published = 0;

            try
            {
                published = await PublishBatchAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Publishing outbox messages failed; will retry.");
            }

            if (published == 0)
            {
                try
                {
                    await Task.Delay(_options.OutboxPollIntervalMs, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        try
        {
            _producer.Flush(TimeSpan.FromSeconds(5));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Flushing the Kafka producer failed.");
        }
    }

    private async Task<int> PublishBatchAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ClaimsDbContext>();

        var batch = await db.Outbox
            .Where(m => m.ProcessedAt == null)
            .OrderBy(m => m.CreatedAt)
            .ThenBy(m => m.Sequence)
            .Take(_options.OutboxBatchSize)
            .ToListAsync(cancellationToken);

        var published = 0;

        foreach (var message in batch)
        {
            try
            {
                var kafkaMessage = new Message<string, string>
                {
                    Key = message.MessageKey,
                    Value = message.Payload,
                    Headers = new Headers
                    {
                        { "ce_id", Encoding.UTF8.GetBytes(message.Id.ToString()) },
                        { "ce_type", Encoding.UTF8.GetBytes(message.EventType) },
                        { "ce_specversion", Encoding.UTF8.GetBytes("1.0") }
                    }
                };

                await _producer.ProduceAsync(message.Topic, kafkaMessage, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                message.Attempts++;
                message.LastError = TextUtil.Truncate(ex.Message, 1000);
                _logger.LogWarning(
                    "Could not publish {EventType} {EventId} (attempt {Attempts}): {Reason}",
                    message.EventType,
                    message.Id,
                    message.Attempts,
                    ex.Message);

                try
                {
                    await db.SaveChangesAsync(cancellationToken);
                }
                catch (Exception saveException) when (saveException is not OperationCanceledException)
                {
                    _logger.LogWarning(saveException, "Could not record the failed publish attempt.");
                }

                // Stop here to keep the order of events; try again later.
                break;
            }

            message.ProcessedAt = _time.GetUtcNow();
            message.Attempts++;
            message.LastError = null;
            await db.SaveChangesAsync(cancellationToken);
            published++;

            _logger.LogInformation("Published {EventType} {EventId} to {Topic}.", message.EventType, message.Id, message.Topic);
        }

        return published;
    }
}
