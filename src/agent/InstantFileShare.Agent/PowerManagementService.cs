using System.Runtime.InteropServices;

namespace InstantFileShare.Agent;

internal sealed class PowerManagementService
{
    private int _activeTransfers;

    public void NotifyTransferStarted()
    {
        if (Interlocked.Increment(ref _activeTransfers) == 1)
        {
            SetThreadExecutionState(ExecutionState.Continuous | ExecutionState.SystemRequired | ExecutionState.DisplayRequired);
        }
    }

    public void NotifyTransferEnded()
    {
        if (Interlocked.Decrement(ref _activeTransfers) <= 0)
        {
            Interlocked.Exchange(ref _activeTransfers, 0);
            SetThreadExecutionState(ExecutionState.Continuous);
        }
    }

    [DllImport("kernel32.dll")]
    private static extern ExecutionState SetThreadExecutionState(ExecutionState executionState);

    [Flags]
    private enum ExecutionState : uint
    {
        Continuous = 0x80000000,
        SystemRequired = 0x00000001,
        DisplayRequired = 0x00000002,
    }
}
