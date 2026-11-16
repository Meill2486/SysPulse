using System;
using System.Collections.Generic;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace SysPulse;

/// <summary>接口 GUID 是持久身份，LUID 用于当前系统中的计数差分；不按名称猜选网卡。</summary>
internal sealed record NetworkAdapter(Guid Id, string Name, bool Connected, ulong ReceiveSpeed, ulong TransmitSpeed)
{
    public override string ToString() => $"{Name}（{(Connected ? "已连接" : "断开")}）";
}

/// <summary>通过 Windows IP Helper 的同一份 64 位接口表读取吞吐与按需元数据。</summary>
internal static class PhysicalNetwork
{
    private const byte HardwareInterface = 1 << 0;
    private const byte FilterInterface = 1 << 1;
    private static readonly int FirstRowOffset = (int)Marshal.OffsetOf<InterfaceTable>(nameof(InterfaceTable.FirstRow));
    private static readonly int RowSize = Marshal.SizeOf<InterfaceRow>();

    /// <summary>默认只统计活动实体接口；指定 GUID 可选虚拟接口，断开时绝不自动换卡。</summary>
    internal static Dictionary<ulong, (ulong Received, ulong Sent)>? ReadCounters(Guid? selected, out string? state)
    {
        uint error = GetIfTable2(out nint table);
        state = error == 0 ? null : $"GetIfTable2: {error}";
        if (error != 0) return null;
        try
        {
            var counters = new Dictionary<ulong, (ulong Received, ulong Sent)>();
            bool found = false;
            int count = Marshal.ReadInt32(table);
            for (int i = 0; i < count; i++)
            {
                var row = Marshal.PtrToStructure<InterfaceRow>(table + FirstRowOffset + i * RowSize);
                if (selected.HasValue ? row.Id != selected.Value : !IsPhysical(row)) continue;
                found = true;
                if (row.Status == OperationalStatus.Up)
                    counters[row.Luid] = (row.InOctets, row.OutOctets);
            }
            if (counters.Count == 0)
                state = selected.HasValue ? (found ? "指定网卡已断开" : "指定网卡不存在") : "无活动实体网卡";
            return counters;
        }
        finally { FreeMibTable(table); }
    }

    /// <summary>仅在打开设置等用户请求时读取名称、连接状态和链路速率，不产生第二套流量来源。</summary>
    internal static NetworkAdapter[] ReadAdapters()
    {
        uint error = GetIfTable2(out nint table);
        if (error != 0) throw new System.ComponentModel.Win32Exception((int)error);
        try
        {
            var adapters = new List<NetworkAdapter>();
            int count = Marshal.ReadInt32(table);
            for (int i = 0; i < count; i++)
            {
                nint address = table + FirstRowOffset + i * RowSize;
                var row = Marshal.PtrToStructure<InterfaceRow>(address);
                if (row.Type == NetworkInterfaceType.Loopback || (row.Flags & FilterInterface) != 0) continue;
                string name = Marshal.PtrToStringUni(address + 28, 257)!.TrimEnd('\0');
                adapters.Add(new(row.Id, name, row.Status == OperationalStatus.Up, row.ReceiveSpeed, row.TransmitSpeed));
            }
            return adapters.ToArray();
        }
        finally { FreeMibTable(table); }
    }

    private static bool IsPhysical(InterfaceRow row) =>
        (row.Flags & (HardwareInterface | FilterInterface)) == HardwareInterface &&
        row.Type is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel);

    [StructLayout(LayoutKind.Sequential)]
    private struct InterfaceTable { public uint Count; public InterfaceRow FirstRow; }

    // Windows SDK MIB_IF_ROW2：Index 位于 8，GUID 位于 12，Alias 位于 28；x64 表头按 8 字节对齐。
    [StructLayout(LayoutKind.Explicit, Size = 1352)]
    private struct InterfaceRow
    {
        [FieldOffset(0)] public ulong Luid;
        [FieldOffset(12)] public Guid Id;
        [FieldOffset(1128)] public NetworkInterfaceType Type;
        [FieldOffset(1152)] public byte Flags;
        [FieldOffset(1156)] public OperationalStatus Status;
        [FieldOffset(1192)] public ulong TransmitSpeed;
        [FieldOffset(1200)] public ulong ReceiveSpeed;
        [FieldOffset(1208)] public ulong InOctets;
        [FieldOffset(1280)] public ulong OutOctets;
    }

    [DllImport("iphlpapi.dll")] private static extern uint GetIfTable2(out nint table);
    [DllImport("iphlpapi.dll")] private static extern void FreeMibTable(nint table);
}
