using InstantFileShare.Core;
using InstantFileShare.Infrastructure;

namespace InstantFileShare.Agent;

internal static class ControlApiEndpoints
{
    public static IEndpointRouteBuilder MapControlApiEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/runtime", async (IShareCoordinator coordinator, CancellationToken cancellationToken) =>
            Results.Ok(await coordinator.GetRuntimeSnapshotAsync(cancellationToken)));

        endpoints.MapGet("/api/shares", async (IShareCoordinator coordinator, CancellationToken cancellationToken) =>
            Results.Ok(await coordinator.ListSharesAsync(cancellationToken)));

        endpoints.MapPost("/api/shares", async (CreateShareRequest request, IShareCoordinator coordinator, CancellationToken cancellationToken) =>
        {
            var (share, url) = await coordinator.CreateShareAsync(request, cancellationToken);
            return Results.Ok(new { share, url });
        });

        endpoints.MapDelete("/api/shares/{shareId}", async (string shareId, IShareCoordinator coordinator, CancellationToken cancellationToken) =>
        {
            await coordinator.RevokeShareAsync(shareId, cancellationToken);
            return Results.NoContent();
        });

        endpoints.MapGet("/api/settings", async (IShareCoordinator coordinator, CancellationToken cancellationToken) =>
            Results.Ok(await coordinator.GetSettingsAsync(cancellationToken)));

        endpoints.MapPut("/api/settings", async (UpdateSettingsRequest request, IShareCoordinator coordinator, CancellationToken cancellationToken) =>
        {
            await coordinator.SaveSettingsAsync(request.Settings, cancellationToken);
            return Results.NoContent();
        });

        endpoints.MapGet("/api/publish-profiles", async (IShareCoordinator coordinator, CancellationToken cancellationToken) =>
            Results.Ok(await coordinator.GetPublishProfilesAsync(cancellationToken)));

        endpoints.MapPut("/api/publish-profiles/{mode}", async (PublishMode mode, PublishProfile profile, IShareCoordinator coordinator, CancellationToken cancellationToken) =>
        {
            await coordinator.SavePublishProfileAsync(profile with { Mode = mode }, cancellationToken);
            return Results.NoContent();
        });

        endpoints.MapGet("/api/transfers", async (IShareCoordinator coordinator, CancellationToken cancellationToken) =>
            Results.Ok(await coordinator.GetTransfersAsync(cancellationToken)));

        endpoints.MapGet("/api/logs/agent", async (FileLogStore logStore, CancellationToken cancellationToken) =>
            Results.Text(await logStore.ReadAgentAsync(cancellationToken), "text/plain"));

        endpoints.MapGet("/api/logs/cloudflare", async (FileLogStore logStore, CancellationToken cancellationToken) =>
            Results.Text(await logStore.ReadCloudflareAsync(cancellationToken), "text/plain"));

        endpoints.MapPost("/api/cloudflared/detect", async (IShareCoordinator coordinator, CancellationToken cancellationToken) =>
            Results.Ok(await coordinator.DetectCloudflaredAsync(cancellationToken)));

        endpoints.MapPost("/api/cloudflared/install", async (IShareCoordinator coordinator, CancellationToken cancellationToken) =>
            Results.Ok(await coordinator.InstallCloudflaredAsync(cancellationToken)));

        endpoints.MapPost("/api/cloudflared/update", async (IShareCoordinator coordinator, CancellationToken cancellationToken) =>
            Results.Ok(await coordinator.UpdateCloudflaredAsync(cancellationToken)));

        endpoints.MapPost("/api/cloudflared/login", async (IShareCoordinator coordinator, CancellationToken cancellationToken) =>
            Results.Ok(await coordinator.StartManagedTunnelLoginAsync(cancellationToken)));

        endpoints.MapPost("/api/cloudflared/logout", async (IShareCoordinator coordinator, CancellationToken cancellationToken) =>
            Results.Ok(await coordinator.LogoutCloudflareAsync(cancellationToken)));

        endpoints.MapGet("/api/cloudflared/status", async (IShareCoordinator coordinator, CancellationToken cancellationToken) =>
            Results.Ok(await coordinator.GetCloudflaredDashboardStatusAsync(cancellationToken)));

        endpoints.MapGet("/api/cloudflared/managed-status", async (IShareCoordinator coordinator, CancellationToken cancellationToken) =>
            Results.Ok(await coordinator.GetManagedCloudflareStatusAsync(cancellationToken)));

        endpoints.MapPost("/api/cloudflared/managed-check", async (CheckManagedTunnelRequest request, IShareCoordinator coordinator, CancellationToken cancellationToken) =>
            Results.Ok(await coordinator.CheckManagedTunnelAvailabilityAsync(request, cancellationToken)));

        endpoints.MapPost("/api/cloudflared/managed-tunnel", async (CreateManagedTunnelRequest request, IShareCoordinator coordinator, CancellationToken cancellationToken) =>
            Results.Ok(await coordinator.CreateManagedTunnelAsync(request, cancellationToken)));

        return endpoints;
    }
}
