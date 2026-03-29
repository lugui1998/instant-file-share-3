using InstantFileShare.Core;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace InstantFileShare.Agent;

internal sealed class ClipboardService : IClipboardService
{
    public Task SetTextAsync(string value, CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                SetTextWithRetry(value);
                tcs.SetResult();
            }
            catch (Exception exception)
            {
                tcs.SetException(exception);
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return tcs.Task;
    }

    private static void SetTextWithRetry(string value)
    {
        const int maxAttempts = 10;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                Clipboard.SetText(value);
                return;
            }
            catch (ExternalException) when (attempt < maxAttempts)
            {
                Thread.Sleep(100);
            }
        }

        Clipboard.SetText(value);
    }
}
