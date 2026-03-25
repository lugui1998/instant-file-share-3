using System.IO.Pipes;
using System.Text.Json;
using InstantFileShare.Core;

namespace InstantFileShare.Agent;

internal sealed class PipeCommandServer(
    IPipeCommandHandler handler,
    ILogger<PipeCommandServer> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await using var server = new NamedPipeServerStream(
                Defaults.NamedPipeName,
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Message,
                PipeOptions.Asynchronous);

            await server.WaitForConnectionAsync(stoppingToken);

            using var reader = new StreamReader(server);
            using var writer = new StreamWriter(server) { AutoFlush = true };
            var payload = await reader.ReadLineAsync(stoppingToken);

            PipeCommandResult result;
            try
            {
                var command = string.IsNullOrWhiteSpace(payload)
                    ? null
                    : JsonSerializer.Deserialize<PipeCommand>(payload, JsonOptions);
                result = command is null
                    ? new PipeCommandResult(false, "Invalid payload.")
                    : await handler.HandleAsync(command, stoppingToken);
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Pipe command failed. Payload: {Payload}", payload);
                result = new PipeCommandResult(false, exception.Message);
            }

            await writer.WriteLineAsync(JsonSerializer.Serialize(result, JsonOptions));
        }
    }
}
