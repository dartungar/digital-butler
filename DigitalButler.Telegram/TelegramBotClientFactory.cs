using System.Net;
using System.Net.Sockets;
using Telegram.Bot;

namespace DigitalButler.Telegram;

public static class TelegramBotClientFactory
{
    public static TelegramBotClient Create(string token, bool forceIpv4)
    {
        if (!forceIpv4)
        {
            return new TelegramBotClient(token);
        }

        var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (context, ct) =>
            {
                var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct);
                var ipv4 = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);

                if (ipv4 is null)
                {
                    throw new SocketException((int)SocketError.HostNotFound);
                }

                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    await socket.ConnectAsync(ipv4, context.DnsEndPoint.Port, ct);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            }
        };

        var httpClient = new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };

        return new TelegramBotClient(token, httpClient);
    }
}
