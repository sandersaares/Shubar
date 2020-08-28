using Axinom.Toolkit;
using Prometheus;
using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Shubar
{
    // Based on Sven's urn app.
    class Program
    {
        public const int MaxPacketSizeBytes = 2000;
        public const int ConcurrentReadsFromClientPort = 1000;
        public static readonly int ConcurrentReadsFromPeerPortPerCpu = 1000;
        public static readonly int ConcurrentReadsFromPeerPortTotal = ConcurrentReadsFromPeerPortPerCpu * Environment.ProcessorCount;

        private static Socket _clientSocket;

        private static void DisableUdpConnectionReset(Socket socket)
        {
            if (Helpers.Environment.IsNonMicrosoftOperatingSystem())
                return; // This stuff only works on Windows.

            const int code = unchecked((int)(0x80000000 | 0x18000000 | 12));
            socket.IOControl(code, BitConverter.GetBytes(false), new byte[sizeof(bool)]);
        }

        private static void EnableSocketSharingPerCore(Socket socket, ushort cpuIndex)
        {
            // SIO_SET_PORT_SHARING_PER_PROC_SOCKET
            const int code = unchecked((int)(0x80000000 | 0x18000000 | 21));
            socket.IOControl(code, BitConverter.GetBytes(cpuIndex), new byte[sizeof(ushort)]);
        }

        static void Main(string[] args)
        {
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                Console.WriteLine("Unhandled exception: " + Helpers.Debug.GetAllExceptionMessages((Exception)e.ExceptionObject));
            };

            TaskScheduler.UnobservedTaskException += (s, e) =>
            {
                Console.WriteLine("Unobserved exception: " + Helpers.Debug.GetAllExceptionMessages(e.Exception));
            };

            // Requires run as Administrator or suitable ACL configuration.
            // netsh http add urlacl url=http://*:3799/metrics user=<your user account>
            // You may also need to allow in firewall for remote access.
            var metricServer = new MetricServer(3799);
            metricServer.Start();

            Console.WriteLine($"DOTNET_SYSTEM_NET_SOCKETS_INLINE_COMPLETIONS={Environment.GetEnvironmentVariable("DOTNET_SYSTEM_NET_SOCKETS_INLINE_COMPLETIONS")}");

            _clientSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            _clientSocket.Bind(new IPEndPoint(IPAddress.Any, 3478));

            DisableUdpConnectionReset(_clientSocket);

            for (var i = 0; i < ConcurrentReadsFromClientPort; i++)
            {
                var buffer = new byte[MaxPacketSizeBytes];
                var receivedFrom = new IPEndPoint(IPAddress.Any, 0);

                Task.Run(async delegate
                {
                    while (true)
                    {
                        var result = await _clientSocket.ReceiveFromAsync(buffer, SocketFlags.None, receivedFrom);

                        if (result.ReceivedBytes == 0)
                            continue;

                        PacketsReadFromClientPort.Value.Inc();

                        ProcessPacketOnClientPort(new ArraySegment<byte>(buffer, 0, result.ReceivedBytes), (IPEndPoint)result.RemoteEndPoint);
                    }
                }).Forget();
            }

            if (Helpers.Environment.IsMicrosoftOperatingSystem() && Environment.OSVersion.Version.Build == 19041)
            {
                StartMultiSocketPeerReads();
            }
            else
            {
                StartSingleSocketPeerReads();
            }

            Thread.Sleep(Timeout.Infinite);
        }

        private static void StartSingleSocketPeerReads()
        {
            Console.WriteLine("Using single-socket peer reads.");

            var peerSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            peerSocket.Bind(new IPEndPoint(IPAddress.Any, 3479));

            DisableUdpConnectionReset(peerSocket);

            for (var i = 0; i < ConcurrentReadsFromPeerPortTotal; i++)
            {
                var buffer = new byte[MaxPacketSizeBytes];
                var receivedFrom = new IPEndPoint(IPAddress.Any, 0);

                Task.Run(async delegate
                {
                    while (true)
                    {
                        var result = await peerSocket.ReceiveFromAsync(buffer, SocketFlags.None, receivedFrom);

                        if (result.ReceivedBytes == 0)
                            continue;

                        PacketsReadFromPeerPort.Value.Inc();

                        await ProcessPacketOnPeerPortAsync(new ArraySegment<byte>(buffer, 0, result.ReceivedBytes));
                    }
                }).Forget();
            }
        }

        // Windows 2004+ only
        private static void StartMultiSocketPeerReads()
        {
            Console.WriteLine("Using multi-socket peer reads.");

            for (ushort cpu = 0; cpu < Environment.ProcessorCount; cpu++)
            {
                var peerSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

                DisableUdpConnectionReset(peerSocket);
                EnableSocketSharingPerCore(peerSocket, cpu);

                peerSocket.Bind(new IPEndPoint(IPAddress.Any, 3479));

                for (var i = 0; i < ConcurrentReadsFromPeerPortPerCpu; i++)
                {
                    var buffer = new byte[MaxPacketSizeBytes];
                    var receivedFrom = new IPEndPoint(IPAddress.Any, 0);

                    Task.Run(async delegate
                    {
                        while (true)
                        {
                            var result = await peerSocket.ReceiveFromAsync(buffer, SocketFlags.None, receivedFrom);

                            if (result.ReceivedBytes == 0)
                                continue;

                            PacketsReadFromPeerPort.Value.Inc();

                            await ProcessPacketOnPeerPortAsync(new ArraySegment<byte>(buffer, 0, result.ReceivedBytes));
                        }
                    }).Forget();
                }
            }
        }

        private static ConcurrentDictionary<long, Session> _sessions = new ConcurrentDictionary<long, Session>();

        private static void ProcessPacketOnClientPort(ArraySegment<byte> packet, IPEndPoint remote)
        {
            // We expect the packet to start with a 64-bit integer, treated as the session ID.
            if (packet.Count < sizeof(long))
                return; // Don't know what that was and don't want to know!

            long sessionId = BitConverter.ToInt64(packet);

            Session CreateSession(long id, IPEndPoint arg) => new Session(arg);

            _sessions.GetOrAdd(sessionId, CreateSession, remote);
        }

        private static async ValueTask ProcessPacketOnPeerPortAsync(ArraySegment<byte> packet)
        {
            // We expect the packet to start with a 64-bit integer, treated as the session ID.
            if (packet.Count < sizeof(long))
                return; // Don't know what that was and don't want to know!

            long sessionId = BitConverter.ToInt64(packet);

            if (!_sessions.TryGetValue(sessionId, out var session))
            {
                // There is no such session. Ignore packet.
                return;
            }

            // Forward packet to client.
            await _clientSocket.SendToAsync(packet, SocketFlags.None, session.ClientAddress);
            PacketsWrittenToClientPort.Value.Inc();
        }



        private static readonly Counter PacketsReadFromClientPortBase = Metrics.CreateCounter("shubar_client_port_read_packets_total", "", new CounterConfiguration
        {
            LabelNames = new[] { "thread" }
        });
        private static readonly Counter PacketsReadFromPeerPortBase = Metrics.CreateCounter("shubar_peer_port_read_packets_total", "", new CounterConfiguration
        {
            LabelNames = new[] { "thread" }
        });
        private static readonly Counter PacketsWrittenToClientPortBase = Metrics.CreateCounter("shubar_client_port_written_packets_total", "", new CounterConfiguration
        {
            LabelNames = new[] { "thread" }
        });

        private static readonly ThreadLocal<Counter.Child> PacketsReadFromClientPort = new ThreadLocal<Counter.Child>(
            () => PacketsReadFromClientPortBase.WithLabels(Thread.CurrentThread.ManagedThreadId.ToString()));

        private static readonly ThreadLocal<Counter.Child> PacketsReadFromPeerPort = new ThreadLocal<Counter.Child>(
            () => PacketsReadFromPeerPortBase.WithLabels(Thread.CurrentThread.ManagedThreadId.ToString()));

        private static readonly ThreadLocal<Counter.Child> PacketsWrittenToClientPort = new ThreadLocal<Counter.Child>(
            () => PacketsWrittenToClientPortBase.WithLabels(Thread.CurrentThread.ManagedThreadId.ToString()));
    }

    class Session
    {
        public IPEndPoint ClientAddress { get; }

        public Session(IPEndPoint clientAddress)
        {
            ClientAddress = clientAddress;
        }
    }
}
