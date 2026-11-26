using NitroLite.Core;

namespace NitroLite.Windows.Core;

/// <summary>
/// <c>--agent-capabilities</c>: asks Acer's service (AASSvc) the questions NitroSense asks at start-up to learn what a
/// machine has — fan list and limits, operating modes, GPU modes, keyboard — and prints the raw answers.
/// </summary>
/// <remarks>
/// Read-only by construction: the question list holds no SET_DEVICE_DATA packet, and a test pins that. The output is
/// the evidence a model report needs before NitroLite uses any of these answers to decide what a machine can do.
/// </remarks>
public static class AgentCapabilityDump
{
    /// <summary>Runs the dump against the local service. Exit code 0 when the service answered, 1 when it did not.</summary>
    public static async Task<int> RunAsync(TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(output);
        using var client = new AcerAgentClient();
        return await RunAsync(client, output).ConfigureAwait(false);
    }

    /// <summary>The same against a supplied client, for the tests.</summary>
    public static async Task<int> RunAsync(AcerAgentClient client, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(output);

        if (client.ChannelEncrypted)
        {
            await output.WriteLineAsync(
                "AcerAgentService channel is encrypted (SupportAES is set); nothing was asked. NitroLite uses WMI on this machine.")
                .ConfigureAwait(false);
            return 1;
        }

        var answers = await client.QueryCapabilityAnswersAsync().ConfigureAwait(false);
        if (answers == null)
        {
            await output.WriteLineAsync("AcerAgentService (127.0.0.1:46933) did not answer; nothing was asked.").ConfigureAwait(false);
            return 1;
        }

        await output.WriteLineAsync(Format(answers)).ConfigureAwait(false);
        return 0;
    }

    /// <summary>One line per question: the function name, then the raw reply or "(no answer)".</summary>
    public static string Format(IReadOnlyList<(string Function, string? Answer)> answers)
    {
        ArgumentNullException.ThrowIfNull(answers);
        var text = new System.Text.StringBuilder();
        text.AppendLine("AcerAgentService capability answers (read-only)");
        foreach (var (function, answer) in answers)
        {
            text.Append(function).Append(": ").AppendLine(string.IsNullOrWhiteSpace(answer) ? "(no answer)" : answer.Trim());
        }

        return text.ToString().TrimEnd();
    }
}
