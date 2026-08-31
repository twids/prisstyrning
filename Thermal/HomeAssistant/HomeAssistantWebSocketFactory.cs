using System.Net.WebSockets;

namespace Prisstyrning.Thermal.HomeAssistant;

public interface IHomeAssistantWebSocketFactory
{
    Task<WebSocket> ConnectAsync(Uri uri, CancellationToken cancellationToken);
}

public sealed class HomeAssistantWebSocketFactory : IHomeAssistantWebSocketFactory
{
    public async Task<WebSocket> ConnectAsync(Uri uri, CancellationToken cancellationToken)
    {
        var socket = new ClientWebSocket();
        // Detect a silent peer without treating a quiet house as a broken connection.
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
        socket.Options.KeepAliveTimeout = TimeSpan.FromSeconds(15);
        try
        {
            await socket.ConnectAsync(uri, cancellationToken);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
