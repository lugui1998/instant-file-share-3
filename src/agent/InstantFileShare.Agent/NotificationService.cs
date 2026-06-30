using InstantFileShare.Core;
using System.Drawing;
using System.Windows.Forms;

namespace InstantFileShare.Agent;

internal sealed class NotificationService : IHostedService, INotificationService, IDisposable
{
    private NotifyIcon? _notifyIcon;
    private Icon? _appIcon;
    private Thread? _uiThread;
    private SynchronizationContext? _uiContext;
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public event Action? OpenDashboardRequested;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _uiThread = new Thread(() =>
        {
            ApplicationConfiguration.Initialize();
            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
            _uiContext = SynchronizationContext.Current;
            _appIcon = ResolveAppIcon();
            _notifyIcon = new NotifyIcon
            {
                Icon = _appIcon ?? SystemIcons.Application,
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

        _appIcon?.Dispose();
        _appIcon = null;

        Application.ExitThread();
        return Task.CompletedTask;
    }

    public void ShowInfo(string title, string text) => Show(title, text, ToolTipIcon.Info);

    public void ShowError(string title, string text) => Show(title, text, ToolTipIcon.Error);

    public void Dispose() => _notifyIcon?.Dispose();

    private static Icon? ResolveAppIcon()
    {
        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
        {
            return null;
        }

        try
        {
            return Icon.ExtractAssociatedIcon(executablePath);
        }
        catch
        {
            return null;
        }
    }

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

        void ShowCore()
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

        if (_uiContext is null || SynchronizationContext.Current == _uiContext)
        {
            ShowCore();
            return;
        }

        _uiContext.Post(_ => ShowCore(), null);
    }
}
