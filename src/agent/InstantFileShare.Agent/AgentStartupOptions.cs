namespace InstantFileShare.Agent;

public sealed record AgentStartupOptions(
    bool LaunchDashboardRequested,
    bool InstallerFirstRunRequested)
{
    public static AgentStartupOptions Parse(IEnumerable<string> args)
    {
        var values = args.ToArray();
        return new AgentStartupOptions(
            LaunchDashboardRequested: values.Any(argument => string.Equals(argument, "--open-dashboard", StringComparison.OrdinalIgnoreCase)),
            InstallerFirstRunRequested: values.Any(argument => string.Equals(argument, "--installer-first-run", StringComparison.OrdinalIgnoreCase)));
    }
}
