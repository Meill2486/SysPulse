using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace SysPulse;

/// <summary>DXGI 与 GPU Engine 共用的适配器 LUID；高、低位均按无符号 32 位保存。</summary>
internal readonly record struct GpuAdapterId(uint HighPart, uint LowPart)
{
    public override string ToString() => FormattableString.Invariant($"0x{HighPart:X8}_0x{LowPart:X8}");
}

/// <summary>一次 DXGI 元数据快照；排除集合用于阻止软件／远程适配器被当成未知硬件显示。</summary>
internal sealed record GpuAdapterMetadata(
    IReadOnlyDictionary<GpuAdapterId, string> Names,
    IReadOnlySet<GpuAdapterId> Excluded,
    string? Error);

/// <summary>仅枚举原生 DXGI 描述与 LUID，不创建设备、交换链或渲染后端。</summary>
internal static class GpuAdapters
{
    private const int DxgiErrorNotFound = unchecked((int)0x887A0002);
    private const uint RemoteOrSoftware = 0x1 | 0x2;

    /// <summary>在采集器初始化或 Reset 时调用；按 DXGI 标志排除软件与远程适配器。</summary>
    internal static GpuAdapterMetadata Read()
    {
        var names = new Dictionary<GpuAdapterId, string>();
        var excluded = new HashSet<GpuAdapterId>();
        if (!OperatingSystem.IsWindows())
            return new GpuAdapterMetadata(names, excluded, "DXGI 仅支持 Windows");

        string? error = null;
        IntPtr factory = IntPtr.Zero;
        try
        {
            var iid = new Guid("770aae78-f26f-4dba-a829-253c83d1b387"); // IDXGIFactory1
            int result = CreateDXGIFactory1(ref iid, out factory);
            if (result < 0 || factory == IntPtr.Zero)
                return new GpuAdapterMetadata(names, excluded, HResultError("DXGI 工厂", result));

            // IUnknown 占 0–2；IDXGIFactory1.EnumAdapters1 与 IDXGIAdapter1.GetDesc1 的槽位固定。
            var enumerate = Method<EnumAdapters1>(factory, 12);
            for (uint index = 0; ; index++)
            {
                IntPtr adapter = IntPtr.Zero;
                try
                {
                    result = enumerate(factory, index, out adapter);
                    if (result == DxgiErrorNotFound)
                        break;
                    if (result < 0 || adapter == IntPtr.Zero)
                    {
                        error ??= HResultError("DXGI 枚举", result);
                        break;
                    }

                    var getDescription = Method<GetDescription1>(adapter, 10);
                    result = getDescription(adapter, out AdapterDescription description);
                    if (result < 0)
                    {
                        error ??= HResultError("DXGI 描述", result);
                        continue;
                    }

                    var id = new GpuAdapterId(description.LuidHighPart, description.LuidLowPart);
                    if ((description.Flags & RemoteOrSoftware) != 0)
                    {
                        excluded.Add(id);
                        names.Remove(id);
                        continue;
                    }

                    string? name = description.Description?.Trim();
                    if (string.IsNullOrEmpty(name))
                    {
                        error ??= "DXGI 适配器名称缺失";
                        continue;
                    }
                    if (!excluded.Contains(id))
                        names[id] = name;
                }
                finally
                {
                    if (adapter != IntPtr.Zero)
                        Marshal.Release(adapter);
                }
            }

            if (names.Count == 0)
                error ??= "DXGI 无可用硬件适配器";
            return new GpuAdapterMetadata(names, excluded, error);
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return new GpuAdapterMetadata(names, excluded, "DXGI 原生接口不可用");
        }
        finally
        {
            if (factory != IntPtr.Zero)
                Marshal.Release(factory);
        }
    }

    private static string HResultError(string source, int result) => $"{source}失败（0x{result:X8}）";

    private static T Method<T>(IntPtr instance, int slot) where T : Delegate
    {
        IntPtr table = Marshal.ReadIntPtr(instance);
        return Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(table, slot * IntPtr.Size));
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct AdapterDescription
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string? Description;
        public uint VendorId;
        public uint DeviceId;
        public uint SubSystemId;
        public uint Revision;
        public UIntPtr DedicatedVideoMemory;
        public UIntPtr DedicatedSystemMemory;
        public UIntPtr SharedSystemMemory;
        public uint LuidLowPart;
        public uint LuidHighPart;
        public uint Flags;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int EnumAdapters1(IntPtr factory, uint index, out IntPtr adapter);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetDescription1(IntPtr adapter, out AdapterDescription description);

    [DllImport("dxgi.dll", ExactSpelling = true)]
    private static extern int CreateDXGIFactory1(ref Guid iid, out IntPtr factory);
}
