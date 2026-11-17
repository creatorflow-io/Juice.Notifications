using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace Juice.Notifications.IntegrationTests
{
    /// <summary>
    /// A TCP relay to a server that a test can close and reopen, as if the server stopped and started: while closed, every connection is reset
    /// as it arrives and the ones relayed until then are cut. Stopping the stack's own container would take it from every other test.
    /// </summary>
    public sealed class TcpGate : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly IPEndPoint _target;
        private readonly CancellationTokenSource _stop = new();
        private readonly ConcurrentDictionary<Socket, byte> _relayed = new();
        private readonly Task _accepting;
        private volatile bool _open = true;

        public TcpGate(IPEndPoint target)
        {
            _target = target;
            _listener.Start();
            _accepting = AcceptAsync();
        }

        /// <summary>The address to use instead of the server's: <c>http://127.0.0.1:{port}</c>.</summary>
        public string Url => $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";

        public bool Open
        {
            get => _open;
            set
            {
                _open = value;
                if (!value)
                {
                    foreach (var socket in _relayed.Keys)
                    {
                        Reset(socket);
                    }
                }
            }
        }

        private async Task AcceptAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                Socket client;
                try
                {
                    client = await _listener.AcceptSocketAsync(_stop.Token);
                }
                catch (Exception) when (_stop.IsCancellationRequested)
                {
                    return;
                }
                if (!_open)
                {
                    Reset(client);
                    continue;
                }
                _ = RelayAsync(client);
            }
        }

        private async Task RelayAsync(Socket client)
        {
            var server = new Socket(SocketType.Stream, ProtocolType.Tcp);
            try
            {
                await server.ConnectAsync(_target, _stop.Token);
                _relayed[client] = 0;
                _relayed[server] = 0;
                await Task.WhenAny(PumpAsync(client, server), PumpAsync(server, client));
            }
            catch (Exception)
            {
                // A cut connection, or the gate disposed.
            }
            finally
            {
                _relayed.TryRemove(client, out _);
                _relayed.TryRemove(server, out _);
                Reset(client);
                Reset(server);
            }
        }

        private async Task PumpAsync(Socket from, Socket to)
        {
            var buffer = new byte[16 * 1024];
            int read;
            while ((read = await from.ReceiveAsync(buffer, SocketFlags.None, _stop.Token)) > 0)
            {
                await to.SendAsync(buffer.AsMemory(0, read), SocketFlags.None, _stop.Token);
            }
        }

        private static void Reset(Socket socket)
        {
            try
            {
                socket.LingerState = new LingerOption(true, 0); // a reset, as a stopped server's host answers
                socket.Close();
            }
            catch (Exception)
            {
                // Already closed.
            }
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            _listener.Stop();
            foreach (var socket in _relayed.Keys)
            {
                Reset(socket);
            }
            await _accepting;
            _stop.Dispose();
        }
    }
}
