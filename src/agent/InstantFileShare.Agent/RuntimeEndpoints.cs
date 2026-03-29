using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using InstantFileShare.Core;

namespace InstantFileShare.Agent;

internal static class RuntimeEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = BuildJsonOptions();

    public static IEndpointRouteBuilder MapRuntimeEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/ws/runtime", HandleRuntimeWebSocketAsync);
        return endpoints;
    }

    private static async Task HandleRuntimeWebSocketAsync(
        HttpContext context,
        IRuntimeEventStream stream,
        CancellationToken cancellationToken)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        await foreach (var runtimeEvent in stream.ListenAsync(cancellationToken))
        {
            if (socket.State != WebSocketState.Open)
            {
                break;
            }

            var payload = JsonSerializer.SerializeToUtf8Bytes(runtimeEvent, JsonOptions);
            await socket.SendAsync(payload, WebSocketMessageType.Text, true, cancellationToken);
        }
    }

    private static JsonSerializerOptions BuildJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
