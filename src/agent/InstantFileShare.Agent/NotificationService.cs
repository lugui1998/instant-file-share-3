using System.Drawing;
using System.Windows.Forms;

namespace InstantFileShare.Agent;

internal sealed class NotificationService : IHostedService, IDisposable
{
    private NotifyIcon? _notifyIcon;
    private Thread? _uiThread;
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public event Action? OpenDashboardRequested;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _uiThread = new Thread(() =>
        {
            ApplicationConfiguration.Initialize();
            _notifyIcon = new NotifyIcon
            {
                Icon = SystemIcons.Application,
                Text = "Instant File Share",
                Visible = true,
                ContextMenuStrip = BuildMenu(),
            };
            _notifyIcon.DoubleClick += (_, _) => OpenDashboardRequested?.Invoke();
            _ready.TrySetResult();
            Application.Run();
        });

        _uiThread.SetApartmentState(ApartmentState.STA);
        _uiThread.IsBackground = true;
        _uiThread.Start();
        await _ready.Task.WaitAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        if (_notifyIcon is not null)
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _notifyIcon = null;
        }

        Application.ExitThread();
        return Task.CompletedTask;
    }

    public void ShowInfo(string title, string text) => Show(title, text, ToolTipIcon.Info);

    public void ShowError(string title, string text) => Show(title, text, ToolTipIcon.Error);

    public void Dispose() => _notifyIcon?.Dispose();

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open Dashboard", null, (_, _) => OpenDashboardRequested?.Invoke());
        menu.Items.Add("Exit", null, (_, _) => Environment.Exit(0));
        return menu;
    }

    private void Show(string title, string text, ToolTipIcon icon)
    {
        if (_notifyIcon is null)
        {
            return;
        }

        _notifyIcon.BalloonTipTitle = title;
        _notifyIcon.BalloonTipText = text;
        _notifyIcon.BalloonTipIcon = icon;
        _notifyIcon.ShowBalloonTip(4000);
    }
}
