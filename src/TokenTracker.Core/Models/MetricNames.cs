namespace TokenTracker.Core.Models;

// Only outcome metrics are stored; token/TTFT/duration already live on spans.
public static class MetricNames
{
    public static readonly IReadOnlySet<string> Outcome =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "copilot_chat.edit.acceptance.count",
            "copilot_chat.chat_edit.outcome.count",
            "copilot_chat.lines_of_code.count",
            "copilot_chat.user.action.count",
            "copilot_chat.user.feedback.count",
            "copilot_chat.agent.edit_response.count",
            "copilot_chat.agent.summarization.count",
            "copilot_chat.pull_request.count",
            "copilot_chat.cloud.session.count",
            // Recorded by recordCloudOperation / incrementCloudError.
            "copilot_chat.cloud.operation.count",
            "copilot_chat.cloud.operation.duration",
            "copilot_chat.cloud.error.count",
            "copilot_chat.tool.call.count",
            "copilot_chat.edit.survival.four_gram",
            "copilot_chat.edit.survival.no_revert",
            "copilot_chat.tool.call.duration",
        };
}
