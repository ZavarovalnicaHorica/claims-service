using Confluent.Kafka;

namespace Horica.ClaimsService.Infrastructure.Messaging;

public static class KafkaConfigFactory
{
    public static ProducerConfig CreateProducerConfig(KafkaOptions options)
    {
        var config = new ProducerConfig
        {
            BootstrapServers = options.BootstrapServers,
            ClientId = options.ClientId + "-producer",
            Acks = Acks.All,
            EnableIdempotence = true,
            MessageTimeoutMs = 30000
        };

        ApplySecurity(config, options);
        return config;
    }

    public static ConsumerConfig CreateConsumerConfig(KafkaOptions options)
    {
        var config = new ConsumerConfig
        {
            BootstrapServers = options.BootstrapServers,
            ClientId = options.ClientId + "-consumer",
            GroupId = options.ConsumerGroupId,
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = false,
            SessionTimeoutMs = 30000,
            MaxPollIntervalMs = 600000
        };

        ApplySecurity(config, options);
        return config;
    }

    public static AdminClientConfig CreateAdminConfig(KafkaOptions options)
    {
        var config = new AdminClientConfig
        {
            BootstrapServers = options.BootstrapServers,
            ClientId = options.ClientId + "-admin"
        };

        ApplySecurity(config, options);
        return config;
    }

    private static void ApplySecurity(ClientConfig config, KafkaOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.SecurityProtocol) &&
            Enum.TryParse<SecurityProtocol>(options.SecurityProtocol, true, out var protocol))
        {
            config.SecurityProtocol = protocol;
        }

        if (!string.IsNullOrWhiteSpace(options.SaslMechanism) &&
            Enum.TryParse<SaslMechanism>(options.SaslMechanism, true, out var mechanism))
        {
            config.SaslMechanism = mechanism;
        }

        if (!string.IsNullOrWhiteSpace(options.SaslUsername))
        {
            config.SaslUsername = options.SaslUsername;
        }

        if (!string.IsNullOrWhiteSpace(options.SaslPassword))
        {
            config.SaslPassword = options.SaslPassword;
        }
    }
}
