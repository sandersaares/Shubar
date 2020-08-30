using System;
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;

namespace Shubar
{
    public sealed unsafe class TurboSocket : IDisposable
    {
        public TurboSocket(ITurboPacketProcessor processor, ushort bindToPort)
        {
            _processor = processor;

            for (var i = 0; i < BufferCount; i++)
            {
                _readBuffers[i] = new Buffer(i);
                _writeBuffers[i] = new Buffer(i, FinishWrite);

                _readOverlapped[i] = new NativeWindows.NativeOverlapped
                {
                    BufferIndex = i
                };
                _writeOverlapped[i] = new NativeWindows.NativeOverlapped
                {
                    BufferIndex = i,
                    IsWrite = true
                };

                _availableReadBuffers.Add(_readBuffers[i]);
                _availableWriteBuffers.Add(_writeBuffers[i]);
            }

            _socketHandle = NativeWindows.WSASocketW(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp, IntPtr.Zero, 0, NativeWindows.SocketConstructorFlags.WSA_FLAG_OVERLAPPED);

            _completionPortHandle = NativeWindows.CreateIoCompletionPort(_socketHandle, IntPtr.Zero, IntPtr.Zero, 0);

            DisableUdpConnectionReset();

            using var bindTo = new Sockaddr();
            bindTo.Port = bindToPort;

            MustSucceed(NativeWindows.bind(_socketHandle, bindTo.DataPtr, bindTo.Data.Length), nameof(NativeWindows.bind));

            new Thread(CompletionThread).Start();
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

        private IntPtr _socketHandle;
        private IntPtr _completionPortHandle;

        private ITurboPacketProcessor _processor;

        private readonly Buffer[] _readBuffers = new Buffer[BufferCount];
        private readonly Buffer[] _writeBuffers = new Buffer[BufferCount];
        private readonly NativeWindows.NativeOverlapped[] _readOverlapped = new NativeWindows.NativeOverlapped[BufferCount];
        private readonly NativeWindows.NativeOverlapped[] _writeOverlapped = new NativeWindows.NativeOverlapped[BufferCount];

        // TODO: Use unmanaged memory instead, this is a gruesome abuse of the managed heap.
        /// <summary>
        /// A buffer on the managed heap that is permanently pinned while alive.
        /// Convenient but the garbage collector is not going to be happy about this!
        /// </summary>
        private sealed class Buffer : IDisposable, ITurboWriteBuffer
        {
            public const int MaxLength = 1500;

            public int Index { get; }

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

            public Buffer(int index, Action<Buffer> callback = null)
            {
                Index = index;
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

        private const int QueuedReadCount = 1;

        private void CompletionThread()
        {
            // Queue up some reads.
            for (var i = 0; i < QueuedReadCount; i++)
            {
                QueueRead();
            }

            uint completionsCount = 128;
            var completions = stackalloc NativeWindows.OverlappedEntry[128];

            while (true)
            {
                var result = NativeWindows.GetQueuedCompletionStatusEx(_completionPortHandle, new IntPtr(completions), completionsCount, out var entriesReceived, Timeout.Infinite, false);

                for (var i = 0; i < entriesReceived; i++)
                {
                    var completion = completions[i];

                    // TODO: Check packet length properly.

                    if (completion.Overlapped.IsWrite)
                    {
                        var buffer = _writeBuffers[completion.Overlapped.BufferIndex];
                        ProcessCompletedWrite(buffer);
                    }
                    else
                    {
                        var buffer = _readBuffers[completion.Overlapped.BufferIndex];
                        buffer.DataLength = (int)completion.NumberOfBytesTransferred;
                        ProcessCompletedRead(buffer);
                    }
                }
            }
        }

        const int WSA_IO_PENDING = 997;

        // This is accessed for... reasons. Just don't keep it on the stack.
        private static int SockaddrSize = Sockaddr.Size;

        private void QueueRead()
        {
            if (!_availableReadBuffers.TryTake(out var buffer))
            {
                // Should never happen, as we only queue a read when a previous read is completed.
                throw new Exception($"Out of read buffers {DateTimeOffset.UtcNow}.");
            }

            var wsaBuffer = new NativeWindows.WSABuffer
            {
                Length = buffer.DataLength,
                Pointer = buffer.DataPtr
            };

            SocketFlags flags = SocketFlags.None;
            // TODO: Documentation is ambiguous - can we really pass null for "number of bytes read"? Doesn't that lead to immediate completion being ignored? Or is completion scheduled regardless, even for immediate?
            var result = NativeWindows.WSARecvFrom(_socketHandle, &wsaBuffer, 1, IntPtr.Zero, ref flags, buffer.AddrPtr, ref SockaddrSize, Marshal.UnsafeAddrOfPinnedArrayElement(_readOverlapped, buffer.Index), IntPtr.Zero);

            if (result == SocketError.Success)
            {
                // Immediate success.
                // TODO: Investigate correct handling of this.
            }
            else if (result == SocketError.IOPending || Marshal.GetLastWin32Error() == WSA_IO_PENDING)
            {
                return; // Delayed success.
            }
            else
            {
                Console.WriteLine($"WSARecvFrom returned {result}; last error: {Marshal.GetLastWin32Error()}");
            }
        }

        private void ProcessCompletedRead(Buffer buffer)
        {
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

            QueueRead();
        }


        private void ProcessCompletedWrite(Buffer buffer)
        {
            _availableWriteBuffers.Add(buffer);
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
