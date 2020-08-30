using System;
using System.Collections.Generic;
using System.Text;

namespace Shubar
{
    public interface ITurboWriteBuffer
    {
        byte[] Data { get; }
        int DataLength { get; set; }

        uint IpAddress { get; set; }
        ushort Port { get; set; }

        void Write();
    }
}
