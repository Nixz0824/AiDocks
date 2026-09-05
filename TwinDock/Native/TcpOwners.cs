using System.Net;
using System.Runtime.InteropServices;

namespace TwinDock.Native;

internal static class TcpOwners
{
    private const int AfInet = 2;
    private const int TcpTableOwnerPidListener = 3;

    public static IReadOnlyList<(int Port, int Pid)> LoopbackListeners()
    {
        var results = new List<(int Port, int Pid)>();
        var size = 0;
        var table = nint.Zero;
        GetExtendedTcpTable(table, ref size, true, AfInet, TcpTableOwnerPidListener, 0);
        if (size <= 0)
        {
            return results;
        }

        table = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedTcpTable(table, ref size, true, AfInet, TcpTableOwnerPidListener, 0) != 0)
            {
                return results;
            }

            var count = Marshal.ReadInt32(table);
            var loopback = BitConverter.ToUInt32(IPAddress.Loopback.GetAddressBytes(), 0);
            var rowSize = Marshal.SizeOf<MibTcpRowOwnerPid>();
            var offset = table + 4;
            for (var i = 0; i < count; i++)
            {
                var row = Marshal.PtrToStructure<MibTcpRowOwnerPid>(offset + i * rowSize);
                if (row.LocalAddr != 0 && row.LocalAddr != loopback)
                {
                    continue;
                }

                var port = PortFromNetworkOrder(row.LocalPort);
                if (port > 0 && row.OwningPid > 0)
                {
                    results.Add((port, (int)row.OwningPid));
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(table);
        }

        return results;
    }

    internal static int PortFromNetworkOrder(uint value) =>
        (int)(((value & 0xFF00) >> 8) | ((value & 0x00FF) << 8));

    [StructLayout(LayoutKind.Sequential)]
    private struct MibTcpRowOwnerPid
    {
        public uint State;
        public uint LocalAddr;
        public uint LocalPort;
        public uint RemoteAddr;
        public uint RemotePort;
        public uint OwningPid;
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(
        nint table,
        ref int size,
        bool order,
        int ipVersion,
        int tableClass,
        uint reserved);
}
