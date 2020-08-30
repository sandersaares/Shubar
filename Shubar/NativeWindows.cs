using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace Shubar
{
    public sealed class NativeWindows
    {
        internal static partial class Libraries
        {
            internal const string Advapi32 = "advapi32.dll";
            internal const string BCrypt = "BCrypt.dll";
            internal const string Crypt32 = "crypt32.dll";
            internal const string CryptUI = "cryptui.dll";
            internal const string Gdi32 = "gdi32.dll";
            internal const string HttpApi = "httpapi.dll";
            internal const string IpHlpApi = "iphlpapi.dll";
            internal const string Kernel32 = "kernel32.dll";
            internal const string Mswsock = "mswsock.dll";
            internal const string NCrypt = "ncrypt.dll";
            internal const string NtDll = "ntdll.dll";
            internal const string Odbc32 = "odbc32.dll";
            internal const string Ole32 = "ole32.dll";
            internal const string OleAut32 = "oleaut32.dll";
            internal const string PerfCounter = "perfcounter.dll";
            internal const string Secur32 = "secur32.dll";
            internal const string Shell32 = "shell32.dll";
            internal const string SspiCli = "sspicli.dll";
            internal const string User32 = "user32.dll";
            internal const string Version = "version.dll";
            internal const string WebSocket = "websocket.dll";
            internal const string WinHttp = "winhttp.dll";
            internal const string WinMM = "winmm.dll";
            internal const string Wldap32 = "wldap32.dll";
            internal const string Ws2_32 = "ws2_32.dll";
            internal const string Wtsapi32 = "wtsapi32.dll";
            internal const string CompressionNative = "clrcompression.dll";
            internal const string MsQuic = "msquic.dll";
            internal const string HostPolicy = "hostpolicy.dll";
        }

        // Important: this API is called once by the System.Net.NameResolution contract implementation.
        // WSACleanup is not called and will be automatically performed at process shutdown.
        internal static unsafe SocketError WSAStartup()
        {
            WSAData d;
            return WSAStartup(0x0202 /* 2.2 */, &d);
        }

        [DllImport(Libraries.Ws2_32, SetLastError = true)]
        private static extern unsafe SocketError WSAStartup(short wVersionRequested, WSAData* lpWSAData);

        [StructLayout(LayoutKind.Sequential, Size = 408)]
        private unsafe struct WSAData
        {
            // WSADATA is defined as follows:
            //
            //     typedef struct WSAData {
            //             WORD                    wVersion;
            //             WORD                    wHighVersion;
            //     #ifdef _WIN64
            //             unsigned short          iMaxSockets;
            //             unsigned short          iMaxUdpDg;
            //             char FAR *              lpVendorInfo;
            //             char                    szDescription[WSADESCRIPTION_LEN+1];
            //             char                    szSystemStatus[WSASYS_STATUS_LEN+1];
            //     #else
            //             char                    szDescription[WSADESCRIPTION_LEN+1];
            //             char                    szSystemStatus[WSASYS_STATUS_LEN+1];
            //             unsigned short          iMaxSockets;
            //             unsigned short          iMaxUdpDg;
            //             char FAR *              lpVendorInfo;
            //     #endif
            //     } WSADATA, FAR * LPWSADATA;
            //
            // Important to notice is that its layout / order of fields differs between
            // 32-bit and 64-bit systems.  However, we don't actually need any of the
            // data it contains; it suffices to ensure that this struct is large enough
            // to hold either layout, which is 400 bytes on 32-bit and 408 bytes on 64-bit.
            // Thus, we don't declare any fields here, and simply make the size 408 bytes.
        }

        // Used as last parameter to WSASocket call.
        [Flags]
        internal enum SocketConstructorFlags
        {
            WSA_FLAG_OVERLAPPED = 0x01,
            WSA_FLAG_MULTIPOINT_C_ROOT = 0x02,
            WSA_FLAG_MULTIPOINT_C_LEAF = 0x04,
            WSA_FLAG_MULTIPOINT_D_ROOT = 0x08,
            WSA_FLAG_MULTIPOINT_D_LEAF = 0x10,
            WSA_FLAG_NO_HANDLE_INHERIT = 0x80,
        }

        [DllImport(Libraries.Ws2_32, CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern IntPtr WSASocketW(
                                        [In] AddressFamily addressFamily,
                                        [In] SocketType socketType,
                                        [In] ProtocolType protocolType,
                                        [In] IntPtr protocolInfo,
                                        [In] uint group,
                                        [In] SocketConstructorFlags flags);

        [DllImport(Libraries.Ws2_32, ExactSpelling = true, SetLastError = true)]
        internal static extern SocketError closesocket([In] IntPtr socketHandle);

        [DllImport(Libraries.Ws2_32, SetLastError = true)]
        internal static extern SocketError bind(
            [In] IntPtr socketHandle,
            [In] IntPtr socketAddress,
            [In] int socketAddressSize);

        [DllImport(Libraries.Ws2_32, SetLastError = true)]
        internal static extern unsafe int recvfrom(
            IntPtr socketHandle,
            [In] IntPtr pinnedBuffer,
            [In] int len,
            [In] SocketFlags socketFlags,
            [Out] IntPtr socketAddress,
            [In, Out] ref int socketAddressSize);

        [DllImport(Libraries.Ws2_32, SetLastError = true)]
        internal static extern unsafe int sendto(
    IntPtr socketHandle,
    [In] IntPtr pinnedBuffer,
    [In] int len,
    [In] SocketFlags socketFlags,
    [In] IntPtr socketAddress,
    [In] int socketAddressSize);

        [DllImport(Libraries.Ws2_32, ExactSpelling = true, SetLastError = true)]
        internal static extern SocketError ioctlsocket(
    [In] IntPtr handle,
    [In] int cmd,
    [In, Out] ref int argp);

        [DllImport(Libraries.Ws2_32, SetLastError = true)]
        internal static extern unsafe SocketError WSARecvFrom(
            IntPtr socketHandle,
            WSABuffer* buffers,
            int bufferCount,
            IntPtr bytesTransferred,
            ref SocketFlags socketFlags,
            IntPtr socketAddressPointer,
            ref int socketAddressSizePointer,
            IntPtr overlapped,
            IntPtr completionRoutine);

        [StructLayout(LayoutKind.Sequential)]
        internal struct WSABuffer
        {
            internal int Length; // Length of Buffer
            internal IntPtr Pointer; // Pointer to Buffer
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct NativeOverlapped
        {
            public IntPtr InternalLow;
            public IntPtr InternalHigh;
            public int OffsetLow;
            public int OffsetHigh;
            public IntPtr EventHandle;

            // We keep track of all our buffers. This is the route to determine the right managed buffer instance.
            public int BufferIndex;
            // We have separate read and write buffers.
            public bool IsWrite;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct OverlappedEntry
        {
            public IntPtr CompletionKey;
            public NativeOverlapped Overlapped;
            public IntPtr Internal;
            public uint NumberOfBytesTransferred;
        }

        [DllImport(Libraries.Kernel32, SetLastError = true)]
        internal static extern IntPtr CreateIoCompletionPort(IntPtr FileHandle, IntPtr ExistingCompletionPort, IntPtr CompletionKey, int NumberOfConcurrentThreads);

        [DllImport(Libraries.Kernel32, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool PostQueuedCompletionStatus(IntPtr CompletionPort, int dwNumberOfBytesTransferred, UIntPtr CompletionKey, IntPtr lpOverlapped);

        [DllImport(Libraries.Kernel32, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetQueuedCompletionStatus(IntPtr CompletionPort, out int lpNumberOfBytes, out UIntPtr CompletionKey, out IntPtr lpOverlapped, int dwMilliseconds);

        [DllImport(Libraries.Kernel32, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static unsafe extern bool GetQueuedCompletionStatusEx(IntPtr CompletionPort, IntPtr lpCompletionPortEntries, uint count, out uint ulEntriesRemoved, int dwMilliseconds, bool alertable);

    }
}
