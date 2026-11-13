using System.Net;
using System.Net.Sockets;

namespace OpenBudget.E2E.Tests;

/// <summary>
/// Starts an <see cref="HttpListener"/> for a fake site on a port the OS hands out as free. Tests run in parallel, so
/// another listener (or an outgoing connection) can take the port between picking and binding; that try is repeated.
/// </summary>
internal static class LocalHttpListener
{
    /// <summary>Starts a listener on <paramref name="host"/> and returns it with its base URL (ending in a slash).</summary>
    public static (HttpListener Listener, string Url) Start(string host = "127.0.0.1")
    {
        var attempt = 0;
        while (true)
        {
            attempt++;
            var url = $"http://{host}:{FreePort()}/";
            var listener = new HttpListener();
            listener.Prefixes.Add(url);
            try
            {
                listener.Start();
                return (listener, url);
            }
            catch (HttpListenerException) when (attempt < 20)
            {
                listener.Close();
            }
        }
    }

    private static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }
}
