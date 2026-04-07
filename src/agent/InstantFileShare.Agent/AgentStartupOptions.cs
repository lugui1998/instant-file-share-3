namespace InstantFileShare.Agent;

public sealed record AgentStartupOptions(
    bool LaunchDashboardRequested,
    bool InstallerFirstRunRequested,
    int? RestartParentProcessId)
{
    public static AgentStartupOptions Parse(IEnumerable<string> args)
    {
        var values = args.ToArray();
        int? restartParentProcessId = null;

        for (var index = 0; index < values.Length - 1; index++)
        {
            if (!string.Equals(values[index], "--restart-parent-pid", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (int.TryParse(values[index + 1], out var parsedProcessId) && parsedProcessId > 0)
            {
                restartParentProcessId = parsedProcessId;
            }
        }

        return new AgentStartupOptions(
            LaunchDashboardRequested: values.Any(argument => string.Equals(argument, "--open-dashboard", StringComparison.OrdinalIgnoreCase)),
            InstallerFirstRunRequested: values.Any(argument => string.Equals(argument, "--installer-first-run", StringComparison.OrdinalIgnoreCase)),
            RestartParentProcessId: restartParentProcessId);
    }
}
