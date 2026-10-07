namespace SaaSApp.Workflow.Infrastructure.Options;

public sealed class GmailPushOptions
{
    public const string SectionName = "GmailPush";

    public string? ProjectId { get; set; }

    /// <summary>Full <c>projects/{id}/topics/{name}</c> or a short topic name used with <see cref="ProjectId"/>.</summary>
    public string? TopicName { get; set; }

    /// <summary>Pub/Sub must send this as query <c>token</c> or header <c>X-Gmail-Push-Token</c>.</summary>
    public string? VerificationToken { get; set; }

    public string? ResolveTopicName()
    {
        var topic = TopicName?.Trim();
        if (string.IsNullOrWhiteSpace(topic))
            return null;
        if (topic.StartsWith("projects/", StringComparison.OrdinalIgnoreCase))
            return topic;

        var project = ProjectId?.Trim();
        if (string.IsNullOrWhiteSpace(project))
            return null;

        return $"projects/{project}/topics/{topic}";
    }
}
