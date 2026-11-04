using Amazon.Runtime;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;

namespace RouteGo.Identity.Api.Infrastructure;

/// <summary>
/// Publishes identity events to SNS: identity events and audit events each go to their own topic,
/// with the event type as the message attribute subscription filter policies match on.
/// </summary>
public sealed class SnsIdentityEventPublisher : IIdentityEventPublisher
{
    private const string IdentityEventsStream = "IdentityEvents";
    private const string AuditEventsStream = "AuditEvents";
    private const string EventTypeAttribute = "eventType";

    // LocalStack accepts any credentials; fixed dummy ones keep a developer's real AWS profile from
    // ever being used against the local endpoint. They grant nothing anywhere.
    private const string LocalStackAccessKey = "test";
    private const string LocalStackSecretKey = "test";

    private readonly IAmazonSimpleNotificationService _sns;
    private readonly string _identityEventsTopicArn;
    private readonly string _auditEventsTopicArn;

    public SnsIdentityEventPublisher(
        IAmazonSimpleNotificationService sns, string identityEventsTopicArn, string auditEventsTopicArn)
    {
        _sns = sns;
        _identityEventsTopicArn = identityEventsTopicArn;
        _auditEventsTopicArn = auditEventsTopicArn;
    }

    /// <summary>
    /// Builds the publisher from configuration: LocalStack when an endpoint override is set, otherwise
    /// real SNS through the default credential chain (the EC2 instance role when deployed).
    /// </summary>
    /// <param name="configuration">The application configuration, already validated for the topic ARNs.</param>
    /// <returns>The publisher.</returns>
    public static SnsIdentityEventPublisher Create(IConfiguration configuration)
    {
        string identityEventsTopicArn = configuration[ConfigKeys.IdentityEventsTopicArn]!;
        string auditEventsTopicArn = configuration[ConfigKeys.AuditEventsTopicArn]!;
        string? serviceUrl = configuration[ConfigKeys.AwsServiceUrl];
        if (string.IsNullOrEmpty(serviceUrl))
        {
            return new SnsIdentityEventPublisher(
                new AmazonSimpleNotificationServiceClient(), identityEventsTopicArn, auditEventsTopicArn);
        }

        var localConfig = new AmazonSimpleNotificationServiceConfig { ServiceURL = serviceUrl };
        var localCredentials = new BasicAWSCredentials(LocalStackAccessKey, LocalStackSecretKey);
        var localClient = new AmazonSimpleNotificationServiceClient(localCredentials, localConfig);
        return new SnsIdentityEventPublisher(localClient, identityEventsTopicArn, auditEventsTopicArn);
    }

    /// <inheritdoc />
    public async Task PublishAsync(
        string stream, string eventType, string envelopeJson, CancellationToken cancellationToken)
    {
        var request = new PublishRequest
        {
            TopicArn = GetTopicArn(stream),
            Message = envelopeJson,
            MessageAttributes = new Dictionary<string, MessageAttributeValue>
            {
                [EventTypeAttribute] = new MessageAttributeValue { DataType = "String", StringValue = eventType },
            },
        };
        await _sns.PublishAsync(request, cancellationToken);
    }

    private string GetTopicArn(string stream)
    {
        if (stream == IdentityEventsStream)
        {
            return _identityEventsTopicArn;
        }

        if (stream == AuditEventsStream)
        {
            return _auditEventsTopicArn;
        }

        throw new InvalidOperationException($"No topic is configured for the event stream '{stream}'.");
    }
}
