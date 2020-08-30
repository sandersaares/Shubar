using System;
using System.Runtime.InteropServices;

namespace Shubar
{
    public sealed class Sockaddr : IDisposable
    {
        public byte[] Data { get; }
        public IntPtr DataPtr { get; }

        // 2 - family == 2
        // 2 - port
        // 4 - address
        // 8 - unused
        // total 16

        public const int Size = 16;

        public uint IpAddress4
        {
            get
            {
                return (uint)((Data[4] << 24) | (Data[5] << 16) | (Data[6] << 8) | Data[7]);
            }
            set
            {
                Data[4] = (byte)((value >> 24) & 0xFF);
                Data[5] = (byte)((value >> 16) & 0xFF);
                Data[6] = (byte)((value >> 8) & 0xFF);
                Data[7] = (byte)(value & 0xFF);
            }
        }

        public ushort Port
        {
            get
            {
                return (ushort)((Data[2] << 8) | Data[3]);
            }
            set
            {
                Data[2] = (byte)((value >> 8) & 0xFF);
                Data[3] = (byte)(value & 0xFF);
            }
        }

        private readonly GCHandle _dataHandle;

        public Sockaddr()
        {
            Data = new byte[Size];
            Data[0] = 2; // IPv4

            _dataHandle = GCHandle.Alloc(Data, GCHandleType.Pinned);
            DataPtr = _dataHandle.AddrOfPinnedObject();
        }

        public void Dispose()
        {
            _dataHandle.Free();
        }
    }
}
