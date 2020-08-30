using System;
using System.Collections.Generic;
using System.Text;

namespace Shubar
{
    public interface ITurboPacketProcessor
    {
        void ProcessPacket(Memory<byte> packet, uint fromAddress, ushort fromPort);
    }
}
