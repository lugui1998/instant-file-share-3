using InstantFileShare.Agent;
using System.Windows.Forms;

namespace InstantFileShare.Agent.Tests;

public sealed class NotificationServiceTests
{
    [Fact]
    public void HandleTrayMouseClick_LeftButton_RequestsDashboard()
    {
        var notifications = new NotificationService();
        var requestCount = 0;
        notifications.OpenDashboardRequested += () => requestCount++;

        notifications.HandleTrayMouseClick(MouseButtons.Left);

        Assert.Equal(1, requestCount);
    }

    [Fact]
    public void HandleTrayMouseClick_RightButton_DoesNotRequestDashboard()
    {
        var notifications = new NotificationService();
        var requestCount = 0;
        notifications.OpenDashboardRequested += () => requestCount++;

        notifications.HandleTrayMouseClick(MouseButtons.Right);

        Assert.Equal(0, requestCount);
    }
}
