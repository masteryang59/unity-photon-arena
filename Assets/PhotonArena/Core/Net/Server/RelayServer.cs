using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using PhotonArena.Core.Net.Transport;

namespace PhotonArena.Core.Net.Server
{
    /// <summary>
    /// Hosts a <see cref="MatchServer"/> on TCP for LAN play: one accept thread, one tick thread.
    /// The Unity client can host one in-process ("Host LAN game"); the headless CLI runs one as a dedicated relay.
    /// </summary>
    public sealed class RelayServer : IDisposable
    {
        public const int DefaultPort = 7777;

        readonly TcpListener _listener;
        readonly Action<string> _log;
        readonly Thread _acceptThread;
        readonly Thread _tickThread;
        volatile bool _running = true;

        public MatchServer Match { get; }
        public int Port { get; }

        public RelayServer(int port = DefaultPort, IPAddress bind = null, MatchServerOptions options = null, Action<string> log = null)
        {
            _log = log;
            Match = new MatchServer(new StopwatchClock(), options, log);
            _listener = new TcpListener(bind ?? IPAddress.Any, port);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "arena-relay-accept" };
            _tickThread = new Thread(TickLoop) { IsBackground = true, Name = "arena-relay-tick" };
            _acceptThread.Start();
            _tickThread.Start();
            _log?.Invoke($"relay listening on {(bind ?? IPAddress.Any)}:{Port}");
        }

        void AcceptLoop()
        {
            // Polls instead of blocking in AcceptTcpClient: stopping a listener under a blocked synchronous
            // accept can hang on Windows, and this way Dispose is always prompt.
            while (_running)
            {
                try
                {
                    if (!_listener.Pending())
                    {
                        Thread.Sleep(5);
                        continue;
                    }
                    TcpClient client = _listener.AcceptTcpClient();
                    var link = new TcpFrameLink(client);
                    _log?.Invoke($"connection from {link.RemoteEndPoint}");
                    Match.Accept(link);
                }
                catch (SocketException) when (!_running)
                {
                    return;
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                catch (SocketException e)
                {
                    _log?.Invoke("accept failed: " + e.Message);
                }
            }
        }

        void TickLoop()
        {
            while (_running)
            {
                try
                {
                    Match.Tick();
                }
                catch (Exception e)
                {
                    // A bug in one tick must not take the whole relay down.
                    _log?.Invoke("tick failed: " + e);
                }
                Thread.Sleep(1);
            }
        }

        public void Dispose()
        {
            if (!_running) return;
            _running = false;
            _acceptThread.Join(1000);
            _tickThread.Join(1000);
            _listener.Stop();
            Match.CloseAll("Server shutting down.");
        }
    }
}


    public static object RestoreSnapshot(object data = null)
    {
        if (data == null) return null;
        return data;
    }

    public static object RestoreSnapshot(object data = null)
    {
        if (data == null) return null;
        return data;
    }