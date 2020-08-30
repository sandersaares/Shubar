using Axinom.Toolkit;
using Prometheus;
using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Shubar
{
    // Benchmark roughly based on https://github.com/svens/urn
    class Program
    {
        class Processor : ITurboPacketProcessor
        {
            public Processor(Action<Memory<byte>, uint, ushort> onReceivedPacket)
            {
                _onReceivedPacket = onReceivedPacket;
            }

            private readonly Action<Memory<byte>, uint, ushort> _onReceivedPacket;

            public void ProcessPacket(Memory<byte> packet, uint fromAddress, ushort fromPort)
            {
                _onReceivedPacket(packet, fromAddress, fromPort);
            }
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
            // netsh http add urlacl url=http://+:3799/metrics user=<your user account>
            // You may also need to allow in firewall for remote access.
            var metricServer = new MetricServer(3799);
            metricServer.Start();

            Console.WriteLine($"DOTNET_SYSTEM_NET_SOCKETS_INLINE_COMPLETIONS={Environment.GetEnvironmentVariable("DOTNET_SYSTEM_NET_SOCKETS_INLINE_COMPLETIONS")}");

            var clientPacketProcessor = new Processor(delegate (Memory<byte> packet, uint fromIp, ushort fromPort)
            {
                PacketsReadFromClientPort.Value.Inc();

                // We expect the packet to start with a 64-bit integer, treated as the session ID.
                if (packet.Length < sizeof(long))
                    return; // Don't know what that was and don't want to know!

                long sessionId = BitConverter.ToInt64(packet.Span);

                Session CreateSession(long id, EndPointTuple arg) => new Session(arg);

                _sessions.GetOrAdd(sessionId, CreateSession, new EndPointTuple { Address = fromIp, Port = fromPort });
            });
            var clientSocket = new TurboSocket(clientPacketProcessor, 3478);

            StartPeerReads();

            Thread.Sleep(Timeout.Infinite);
        }

        private static void StartPeerReads()
        {
            Console.WriteLine("Using core-bound multi-socket peer reads.");

            for (ushort cpu = 0; cpu < Environment.ProcessorCount; cpu++)
            {
                TurboSocket peerSocket = null;

                var peerPacketProcessor = new Processor(delegate (Memory<byte> packet, uint fromIp, ushort fromPort)
                {
                    PacketsReadFromPeerPort.Value.Inc();

                    // We expect the packet to start with a 64-bit integer, treated as the session ID.
                    if (packet.Length < sizeof(long))
                        return; // Don't know what that was and don't want to know!

                    long sessionId = BitConverter.ToInt64(packet.Span);

                    if (!_sessions.TryGetValue(sessionId, out var session))
                    {
                        // There is no such session. Ignore packet.
                        return;
                    }

                    // Forward packet to client.
                    var buffer = peerSocket.BeginWrite();
                    buffer.DataLength = packet.Length;
                    packet.CopyTo(buffer.Data);

                    buffer.IpAddress = session.ClientAddress.Address;
                    buffer.Port = session.ClientAddress.Port;

                    buffer.Write();

                    PacketsWrittenToClientPort.Value.Inc();
                });

                peerSocket = new TurboSocket(peerPacketProcessor, 3479, cpu);
            }
        }

        private static ConcurrentDictionary<long, Session> _sessions = new ConcurrentDictionary<long, Session>();

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
        public EndPointTuple ClientAddress { get; }

        public Session(EndPointTuple clientAddress)
        {
            ClientAddress = clientAddress;
        }
    }
}
