using System;
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;

namespace Shubar
{
    public sealed unsafe class TurboSocket : IDisposable
    {
        public TurboSocket(ITurboPacketProcessor processor, ushort bindToPort, ushort? cpu = null)
        {
            _processor = processor;

            for (var i = 0; i < BufferCount; i++)
            {
                _availableReadBuffers.Add(new Buffer());
                _availableWriteBuffers.Add(new Buffer(FinishWrite));
            }

            _socketHandle = NativeWindows.WSASocketW(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp, IntPtr.Zero, 0, NativeWindows.SocketConstructorFlags.WSA_FLAG_OVERLAPPED);

            if (cpu.HasValue)
                BindToCpu(cpu.Value);

            DisableUdpConnectionReset();

            using var bindTo = new Sockaddr();
            bindTo.Port = bindToPort;

            MustSucceed(NativeWindows.bind(_socketHandle, bindTo.DataPtr, bindTo.Data.Length), nameof(NativeWindows.bind));

            new Thread(ReadThread).Start();
            new Thread(ConsumeThread).Start();
            new Thread(WriteThread).Start();
        }

        #region Init & deinit
        static TurboSocket()
        {
            NativeWindows.WSAStartup();
        }

        ~TurboSocket()
        {
            Dispose(false);
        }

        public void Dispose() => Dispose(true);

        private void Dispose(bool disposing)
        {
            if (_socketHandle != IntPtr.Zero)
            {
                MustSucceed(NativeWindows.closesocket(_socketHandle), nameof(NativeWindows.closesocket));

                _socketHandle = IntPtr.Zero;
            }

            GC.SuppressFinalize(this);
        }
        #endregion

        private void DisableUdpConnectionReset()
        {
            const int code = unchecked((int)(0x80000000 | 0x18000000 | 12));
            int value = 0;

            MustSucceed(NativeWindows.ioctlsocket(_socketHandle, code, ref value), nameof(NativeWindows.ioctlsocket));
        }

        private void BindToCpu(ushort cpu)
        {
            // SIO_SET_PORT_SHARING_PER_PROC_SOCKET
            const int code = unchecked((int)(0x80000000 | 0x18000000 | 21));

            int value = cpu;

            MustSucceed(NativeWindows.ioctlsocket(_socketHandle, code, ref value), nameof(NativeWindows.ioctlsocket));
        }

        private IntPtr _socketHandle;
        private ITurboPacketProcessor _processor;

        // TODO: Use unmanaged memory instead, this is a gruesome abuse of the managed heap.
        /// <summary>
        /// A buffer on the managed heap that is permanently pinned while alive.
        /// Convenient but the garbage collector is not going to be happy about this!
        /// </summary>
        private sealed class Buffer : IDisposable, ITurboWriteBuffer
        {
            public const int MaxLength = 1500;

            public byte[] Data { get; }
            public int DataLength { get; set; }

            public uint IpAddress { get => _addr.IpAddress4; set => _addr.IpAddress4 = value; }
            public ushort Port { get => _addr.Port; set => _addr.Port = value; }

            private Sockaddr _addr = new Sockaddr();

            public IntPtr DataPtr { get; }
            public IntPtr AddrPtr => _addr.DataPtr;

            public int AddrLength => _addr.Data.Length;

            private readonly GCHandle _dataHandle;

            public Action<Buffer> Callback { get; }

            public void Write()
            {
                Callback(this);
            }

            public Buffer(Action<Buffer> callback = null)
            {
                Data = new byte[MaxLength];

                Callback = callback;

                _dataHandle = GCHandle.Alloc(Data, GCHandleType.Pinned);

                DataPtr = _dataHandle.AddrOfPinnedObject();
            }

            public void Dispose()
            {
                _addr.Dispose();
                _dataHandle.Free();
            }
        }

        // Both read & write
        private const int BufferCount = 32 * 1024;

        private readonly ConcurrentBag<Buffer> _availableReadBuffers = new ConcurrentBag<Buffer>();
        private readonly ConcurrentQueue<Buffer> _completedReads = new ConcurrentQueue<Buffer>();

        private void ReadThread()
        {
            int addrSize = Sockaddr.Size;

            while (true)
            {
                if (!_availableReadBuffers.TryTake(out var buffer))
                {
                    Console.WriteLine("Out of read buffers. @" + DateTimeOffset.Now);
                    continue; // Whatever, try again.
                }

                var bytesRead = NativeWindows.recvfrom(_socketHandle, buffer.DataPtr, Buffer.MaxLength, SocketFlags.None, buffer.AddrPtr, ref addrSize);

                if (bytesRead == 0)
                {
                    Console.WriteLine("Read thread finished - recvfrom() returned 0.");
                    break;
                }
                else if (bytesRead < 0)
                {
                    Console.WriteLine($"Read thread error - recvfrom() returned {bytesRead} ({Marshal.GetLastWin32Error()}).");
                    continue;
                }

                buffer.DataLength = bytesRead;

                _completedReads.Enqueue(buffer);
            }
        }

        private void ConsumeThread()
        {
            while (true)
            {
                if (!_completedReads.TryDequeue(out var buffer))
                {
                    Thread.Yield();
                    continue;
                }

                try
                {
                    _processor.ProcessPacket(buffer.Data.AsMemory(0, buffer.DataLength), buffer.IpAddress, buffer.Port);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Failed to process packet: {ex.Message}");
                }

                // Return the buffer.
                _availableReadBuffers.Add(buffer);
            }
        }

        // TODO: We would benefit from a zero-copy "re-send" mechanism.

        /// <summary>
        /// Allocate a buffer for sending. After filling all the fields, call Write() on the buffer to enqueue it for writing.
        /// </summary>
        public ITurboWriteBuffer BeginWrite()
        {
            Buffer buffer;

            while (!_availableWriteBuffers.TryTake(out buffer))
                Thread.Yield();

            return buffer;
        }

        private void FinishWrite(Buffer buffer)
        {
            _pendingWriteBuffers.Enqueue(buffer);
        }

        private readonly ConcurrentBag<Buffer> _availableWriteBuffers = new ConcurrentBag<Buffer>();
        private readonly ConcurrentQueue<Buffer> _pendingWriteBuffers = new ConcurrentQueue<Buffer>();

        private void WriteThread()
        {
            while (true)
            {
                if (!_pendingWriteBuffers.TryDequeue(out var buffer))
                {
                    Thread.Yield();
                    continue;
                }

                var bytesWritten = NativeWindows.sendto(_socketHandle, buffer.DataPtr, buffer.DataLength, SocketFlags.None, buffer.AddrPtr, buffer.AddrLength);

                if (bytesWritten == 0)
                {
                    Console.WriteLine("Write thread finished - sendto() returned 0????");
                    break;
                }
                else if (bytesWritten < 0)
                {
                    Console.WriteLine($"Write thread error - sendto() returned {bytesWritten} ({Marshal.GetLastWin32Error()}).");
                    continue;
                }

                _availableWriteBuffers.Add(buffer);
            }
        }

        private static void MustSucceed(SocketError result, string name)
        {
            if (result == SocketError.Success)
                return;

            var lastError = Marshal.GetLastWin32Error();

            throw new Exception($"Socket operation {name} failed with error code: {result} (0x{(int)result:X8}) ({lastError:D8} 0x{lastError:X8})");
        }
    }
}
