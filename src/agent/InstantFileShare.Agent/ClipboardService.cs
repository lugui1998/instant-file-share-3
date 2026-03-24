using System.Windows.Forms;

namespace InstantFileShare.Agent;

internal sealed class ClipboardService
{
    public Task SetTextAsync(string value)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                Clipboard.SetText(value);
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
}
