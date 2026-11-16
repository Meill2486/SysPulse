using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Linq;

namespace SysPulse;

/// <summary>
/// 无需管理员的回归自检；不启动监控、不修改用户配置或注册表，仅在临时目录写文件。
/// </summary>
internal static class SelfTest
{
    /// <summary>
    /// 执行计算、配置和增量列表检查，日志写入临时目录，退出码 0 表示全部通过。
    /// </summary>
    internal static int Run()
    {
        var lines = new List<string> { $"SysPulse {App.VersionLabel}" };
        int failures = 0, checks = 0;
        string directory = Path.Combine(Path.GetTempPath(), $"SysPulse-selftest-{Guid.NewGuid():N}");
        void Check(string name, bool success)
        {
            checks++;
            if (!success) failures++;
            lines.Add($"{(success ? "PASS" : "FAIL")} {name}");
        }
        try
        {
            Directory.CreateDirectory(directory);
            Check("PDH valid zero", NativePerformance.IsValidCounterValue(0, 0));
            Check("PDH new data", NativePerformance.IsValidCounterValue(1, 50));
            Check("PDH invalid status", !NativePerformance.IsValidCounterValue(2, 50));
            Check("PDH negative rejected", !NativePerformance.IsValidCounterValue(0, -1));
            Check("PDH NaN rejected", !NativePerformance.IsValidCounterValue(0, double.NaN));
            Check("PDH infinity rejected", !NativePerformance.IsValidCounterValue(0, double.PositiveInfinity));
            Check("process all active groups", ProcessSampler.LogicalProcessors > 0);
            Check("process cpu normalized", ProcessSampler.CpuPercent((1, 2), (1, 3), 1, 2) == 50);
            Check("process pid reused", ProcessSampler.CpuPercent((1, 2), (2, 3), 1, 2) is null);
            Check("process short interval", ProcessSampler.CpuPercent((1, 2), (1, 3), 0.1, 2) is null);
            Check("process counter reset", ProcessSampler.CpuPercent((1, 2), (1, 1), 1, 2) is null);
            Check("process invalid core count", ProcessSampler.CpuPercent((1, 2), (1, 3), 1, 0) is null);
            Check("process multi-group normalization", ProcessSampler.CpuPercent((1, 0), (1, 128), 1, 128) == 100);
            var identityA = new ProcessIdentity(41, 100);
            var identityB = new ProcessIdentity(42, 200);
            var grouped = ProcessSampler.Aggregate(new ProcessSample[] {
                new(identityA, 41, "same", 1, 100, @"C:\Apps\one.exe"),
                new(identityB, 42, "same", 2, 200, @"C:\Other\one.exe") }, null);
            Check("same name different paths isolated", grouped.Length == 2 && grouped[0].Key != grouped[1].Key);
            grouped = ProcessSampler.Aggregate(new ProcessSample[] {
                new(identityA, 41, "same", 1, 100, @"C:\Apps\one.exe"),
                new(identityB, 42, "same", 2, 200, @"c:\apps\ONE.exe") }, null);
            Check("same path grouped case insensitive", grouped.Length == 1 && grouped[0].Count == 2 && grouped[0].Cpu == 3 && grouped[0].Pids.SequenceEqual(new[] { 41, 42 }));
            grouped = ProcessSampler.Aggregate(new ProcessSample[] {
                new(identityA, 41, "same", null, 100), new(identityB, 42, "same", 2, 200),
                new(null, 43, "same", null, null, UnverifiedKey: "frame:43") }, null);
            Check("unknown paths and identities isolated", grouped.Length == 3 && grouped.Select(row => row.Key).Distinct().Count() == 3);
            Check("unverified identity cannot group by path", ProcessSampler.ApplicationKey(null, @"C:\Apps\one.exe", "frame:43") == "frame:43");
            Check("ETW lost delta and reset", ProcessTraffic.LostDelta(10, 15) == 5 && ProcessTraffic.LostDelta(10, 2) == 2);
            var ledger = new ProcessTrafficLedger();
            var reused = new ProcessIdentity(41, 300);
            ledger.Begin(41, 10, 100, 1, identityA, "old");
            ledger.End(41, 20, 200, 1);
            ledger.Begin(41, 30, 300, 2, reused, "new");
            ledger.Add(41, 18, 180, 120, false);
            ledger.Add(41, 40, 400, 60, true);
            ledger.Add(99, 40, 400, 11, false);
            var slice = ledger.Drain(2, 50);
            Check("ETW reused PID and exited settlement", slice.Counts[identityA] is { Down: 120, Ended: true } && slice.Counts[reused] is { Up: 60, Ended: false });
            Check("ETW unknown bytes retained", slice.Unattributed == new TrafficBytes(11, 0));
            grouped = ProcessSampler.Aggregate(new ProcessSample[] { new(identityA, 41, "old", null, null, Ended: true) }, slice);
            Check("ETW exited row settles without CPU", grouped[0].Ended && grouped[0].Cpu is null && grouped[0].Download == 60);
            ledger.Add(41, 18, 180, 7, false);
            slice = ledger.Drain(1, 60);
            Check("ETW late old event settles only to old identity", slice.Counts.Count == 1 && slice.Counts[identityA].Down == 7 && !slice.Counts.ContainsKey(reused));
            ledger.Add(41, 61, 610, 9, true);
            slice = ledger.Drain(1, 70, lostEvents: 2);
            Check("ETW dropped events mark period incomplete", !slice.IsComplete && slice.LostEvents == 2 && slice.Counts.Count == 0 && slice.Unattributed.Up == 9);
            grouped = ProcessSampler.Aggregate(new ProcessSample[] { new(reused, 41, "new", 0, 100) }, slice);
            Check("ETW incomplete period is unknown not zero", grouped[0].Download is null && grouped[0].Upload is null);
            ledger.Add(41, 71, 710, 5, false);
            Check("ETW loss invalidates identity chain", ledger.Drain(1, 80).Unattributed.Down == 5);
            ledger.Observe(reused, "new", 81);
            ledger.Add(41, 82, 820, 6, false);
            Check("ETW reobservation restores attribution", ledger.Drain(1, 90).Counts[reused].Down == 6);
            Check("ETW loss counter reset incomplete", !ledger.Drain(1, 100, lossCounterReset: true).IsComplete);
            ledger.Begin(41, 110, 1100, 3, null, "unknown");
            ledger.Add(41, 120, 1200, 4, false);
            Check("ETW unverifiable lifetime stays unknown", ledger.Drain(1, 130).Unattributed.Down == 4);
            ledger.Clear();
            for (int index = 0; index <= ProcessTrafficLedger.EventsCapacity; index++) ledger.Add(99, index, index, 1, false);
            slice = ledger.Drain(1, long.MaxValue);
            Check("ETW bounded queue retains unassigned bytes", slice.AttributionLimited && slice.Unattributed.Down == ProcessTrafficLedger.EventsCapacity + 1);
            ledger.Clear();
            for (int index = 1; index <= ProcessTrafficLedger.InstanceCapacity + 1; index++) ledger.Observe(new(index, index), "synthetic", index);
            Check("ETW bounded lifetime cache reports limit", ledger.Drain(1, long.MaxValue).AttributionLimited);
            Check("ETW settlement retention expires", !ProcessTrafficLedger.SettlementExpired(1, System.Diagnostics.Stopwatch.Frequency * 5) &&
                ProcessTrafficLedger.SettlementExpired(1, System.Diagnostics.Stopwatch.Frequency * 6));
            using (var metadata = new ProcessMetadata())
            {
                metadata.Get(identityA, "retained");
                metadata.Retain(new HashSet<ProcessIdentity>());
                Check("metadata exit cache retained for late settlement", metadata.Cached(identityA, "missing").Name == "retained");
            }
            ledger.Clear();
            ledger.Begin(41, 10, 100, 1, identityA, "old");
            ledger.Observe(reused, "new", 40);
            ledger.Drain(1, 50);
            ledger.Add(41, 18, 180, 8, false);
            slice = ledger.Drain(1, 60);
            Check("ETW observation before late stop retains old owner", slice.Counts[identityA].Down == 8 && !slice.Counts.ContainsKey(reused));

            var gpuNames = new Dictionary<GpuAdapterId, string> { [new(0, 1)] = "GPU-A", [new(0, 2)] = "GPU-B" };
            var excluded = new HashSet<GpuAdapterId>();
            string Engine(int pid, int adapter, int physical, int engine) => $"pid_{pid}_luid_0x00000000_0x{adapter:X8}_phys_{physical}_eng_{engine}_engtype_3D";
            var gpu = NativePerformance.AggregateGpu(new[] {
                new GpuEngineSample(Engine(1,1,0,0), 20), new(Engine(2,1,0,0), 30),
                new(Engine(1,1,0,1), 40), new(Engine(1,2,0,0), 80), new(Engine(1,1,1,0), 12)
            }, gpuNames, excluded, out _);
            Check("GPU PID contributions summed per engine", gpu[0].Usage == 50);
            Check("GPU physical engines isolated", gpu.Count == 3 && gpu[1].Usage == 12);
            Check("GPU adapters isolated", gpu[2].Usage == 80);
            gpu = NativePerformance.AggregateGpu(new[] { new GpuEngineSample(Engine(1,1,0,0), double.NaN) }, gpuNames, excluded, out var gpuError);
            Check("GPU invalid sample not zero", gpu[0].Usage is null && gpuError is not null);
            gpu = NativePerformance.AggregateGpu(Array.Empty<GpuEngineSample>(), gpuNames, excluded, out _);
            Check("GPU instance disappearance no stale value", gpu.Count == 0);
            Check("GPU malformed name rejected", !NativePerformance.TryParseGpuInstance("engtype_3D", out _));

            var previous = new Dictionary<ulong, (ulong Received, ulong Sent)> { [1] = (100, 200), [2] = (500, 600) };
            var current = new Dictionary<ulong, (ulong Received, ulong Sent)> { [1] = (110, 220), [3] = (900000, 800000) };
            Check("network new/disconnected adapters ignored", SystemSampler.NetworkDelta(previous, current) == (10, 20, true));
            Check("network first baseline", SystemSampler.NetworkDelta(new Dictionary<ulong, (ulong, ulong)>(), current) == (0, 0, false));
            current[1] = (10, 20);
            Check("network counter reset", SystemSampler.NetworkDelta(previous, current) == (0, 0, false));
            Check("network no adapters", SystemSampler.NetworkDelta(previous, new Dictionary<ulong, (ulong, ulong)>()) == (0, 0, false));
            Check("network integer precision", SystemSampler.NetworkDelta(
                new Dictionary<ulong,(ulong,ulong)> { [1] = (1UL << 60, 1UL << 60) },
                new Dictionary<ulong,(ulong,ulong)> { [1] = ((1UL << 60) + 1, (1UL << 60) + 2) }) == (1, 2, true));
            Check("network unchanged valid zero", SystemSampler.NetworkDelta(previous, previous) == (0, 0, true));
            var history = new TrendHistory();
            long frequency = System.Diagnostics.Stopwatch.Frequency;
            for (int index = 0; index < 150; index++) history.Add(new(frequency + index * frequency / 2, 10, 20, 30, 40, 0, 500));
            var trend = history.Snapshot();
            Check("trend bounded to 60 seconds", trend.Length <= 122 && trend[^1].Timestamp - trend[0].Timestamp <= 60 * frequency);
            history.Add(trend[^1] with { Timestamp = trend[^1].Timestamp - 1 });
            Check("trend old result ignored", history.Snapshot().Length == trend.Length);

            var palette = MetricDisplay.Dark;
            Check("unknown percent", MetricDisplay.Percent(null) == "—");
            Check("unknown speed", MetricDisplay.Bytes(null, rate: true) == "—");
            Check("zero speed", MetricDisplay.Bytes(0, rate: true) == "0 B/s");
            Check("KiB speed", MetricDisplay.Bytes(1024, rate: true) == "1.0 KiB/s");
            Check("MiB speed", MetricDisplay.Bytes(2.5 * 1024 * 1024, rate: true) == "2.5 MiB/s");
            Check("nonfinite bytes unknown", MetricDisplay.Bytes(double.NaN) == "—");
            Check("percent below warning", palette.Status(69, 70, 90) == palette.Normal);
            Check("percent warning boundary", palette.Status(70, 70, 90) == palette.Warning);
            Check("percent alert boundary", palette.Status(90, 70, 90) == palette.Alert);
            Check("missing value neutral", palette.Status(null, 70, 90) == palette.Foreground);
            Check("disk warning boundary normal", palette.Status(15, 15, 5, reverse: true) == palette.Normal);
            Check("disk below warning", palette.Status(14, 15, 5, reverse: true) == palette.Warning);
            Check("disk alert boundary warning", palette.Status(5, 15, 5, reverse: true) == palette.Warning);
            Check("disk below alert", palette.Status(4, 15, 5, reverse: true) == palette.Alert);

            string portable = Path.Combine(directory, "SysPulse.json");
            var defaults = Preferences.Load(portable);
            Check("missing config default", defaults.RefreshMs == 1000 && !defaults.ElevateOnStart && !defaults.SaveBlocked);
            File.WriteAllText(portable, """
                {"RefreshMs":1,"FontSize":100,"CpuLabel":null,"MemLabel":"123456789",
                 "BarStyle":999,"CpuWarn":100,"CpuAlert":1,"DiskWarn":0,"DiskAlert":100,
                 "NetWarn":999,"NetAlert":0,"ProcessCompact":true,"SortKey":"cpu"}
                """);
            var normalized = Preferences.Load(portable);
            Check("config clamps display ranges", normalized.RefreshMs == 500 && normalized.FontSize == 16 && normalized.BarStyle == BarStyle.Ring);
            Check("config normalizes labels", normalized.CpuLabel == "" && normalized.MemLabel == "123456");
            Check("config orders thresholds", normalized.CpuWarn == 99 && normalized.CpuAlert == 100 && normalized.DiskWarn == 2 && normalized.DiskAlert == 1);
            Check("config repairs net/sort", normalized.NetWarn == 200 && normalized.NetAlert == 201 && normalized.SortKey == "down");
            var saved = new Preferences { CpuLabel = "唯一", ElevateOnStart = true, NetworkInterface = Guid.NewGuid() };
            Check("config sole path save", saved.Save(portable));
            Check("config selected NIC roundtrip", Preferences.Load(portable).NetworkInterface == saved.NetworkInterface);
            Check("config startup preference roundtrip", Preferences.Load(portable).ElevateOnStart);
            using (var locked = new FileStream(portable, FileMode.Open, FileAccess.Read, FileShare.Read))
                Check("config locked failure explicit", !saved.Save(portable) && saved.SaveError is not null);
            Check("config failed write preserves original", Preferences.Load(portable).CpuLabel == "唯一");
            File.WriteAllText(portable, "broken JSON");
            var broken = Preferences.Load(portable);
            Check("config corruption blocks autosave", broken.SaveBlocked && !broken.Save());
            Check("config corruption retained", File.ReadAllText(portable) == "broken JSON");
            Check("config explicit rebuild succeeds", broken.Rebuild() && !broken.SaveBlocked);
            Check("config rebuild backup retained", Directory.GetFiles(directory, "*.bak").Length == 1 &&
                File.ReadAllText(Directory.GetFiles(directory, "*.bak")[0]) == "broken JSON");
            File.WriteAllText(portable, "null");
            Check("config null root rejected", Preferences.Load(portable).SaveBlocked);
            Check("config no temporary leftovers", Directory.GetFiles(directory).Length == 2);

            foreach (var order in Enum.GetValues<MetricOrder>())
            {
                var settings = new Preferences { MetricOrder = order, ShowCpu = false };
                var before = settings.GetMetricOrder();
                var metrics = new[] { before.First, before.Second, before.Third };
                var enabled = metrics.Where(settings.IsMetricEnabled).Reverse().ToArray();
                settings.SetEnabledMetricOrder(enabled);
                var after = settings.GetMetricOrder();
                var result = new[] { after.First, after.Second, after.Third };
                Check($"metric order {order}", Array.IndexOf(metrics, BarMetric.Cpu) == Array.IndexOf(result, BarMetric.Cpu)
                    && result.Where(settings.IsMetricEnabled).SequenceEqual(enabled));
            }

            var a = new ProcessRow("a", 1, 10, 1024, null, null, null);
            var b = a with { Name = "b" };
            var items = new ObservableCollection<ProcessItem>();
            int resets = 0, changes = 0;
            items.CollectionChanged += (_, e) => { changes++; if (e.Action == NotifyCollectionChangedAction.Reset) resets++; };
            ProcessWindow.SynchronizeRows(items, new[] { a, b });
            var first = items[0];
            var second = items[1];
            var notifications = new List<string?>();
            first.PropertyChanged += (_, e) => notifications.Add(e.PropertyName);
            changes = 0;
            ProcessWindow.SynchronizeRows(items, new[] { a, b });
            Check("list unchanged no notifications", changes == 0 && notifications.Count == 0);
            ProcessWindow.SynchronizeRows(items, new[] { b, a with { Cpu = 20 } });
            Check("list reorder preserves identity", ReferenceEquals(items[0], second) && ReferenceEquals(items[1], first));
            Check("list only changed cell notified", notifications.SequenceEqual(new[] { "CpuText" }));
            Check("list value updated", first.CpuText == "20%");
            ProcessWindow.SynchronizeRows(items, new[] { a, a with { Name = "c" } });
            Check("list add/remove", items.Count == 2 && ReferenceEquals(items[0], first) && items[1].Name == "c");
            ProcessWindow.SynchronizeRows(items, Array.Empty<ProcessRow>());
            Check("list empty without reset", items.Count == 0 && resets == 0);

            var largeRows = Enumerable.Range(0, 1000)
                .Select(index => a with { Name = $"process-{index:0000}" }).ToArray();
            ProcessWindow.SynchronizeRows(items, largeRows);
            var identities = items.ToArray();
            changes = 0;
            ProcessWindow.SynchronizeRows(items, largeRows.Select(row => row with { Cpu = 30 }).ToArray());
            Check("large list value refresh no structure changes", changes == 0 && resets == 0);
            Check("large list keeps rows and latest values", items.Select((item, index) =>
                ReferenceEquals(item, identities[index]) && item.CpuText == "30%").All(valid => valid));
            var unknown = new ProcessRow("protected", 1, null, 100, null, null, null)
                { ApplicationKey = "unverified:43:1", Pids = new[] { 43 } };
            ProcessWindow.SynchronizeRows(items, new[] { unknown });
            var displaySlot = items[0];
            var changedInstance = unknown with { ApplicationKey = "unverified:43:2", Name = "reused", Memory = 200 };
            ProcessWindow.SynchronizeRows(items, new[] { changedInstance });
            Check("unknown display slot reused without identity reuse", ReferenceEquals(displaySlot, items[0]) &&
                unknown.Key != changedInstance.Key && items[0].InstanceKey == changedInstance.Key);
            Check("unknown display slot uses only latest values", items[0].Name == "reused" && items[0].MemoryText == "200 B" &&
                items[0].CpuText == "—" && items[0].DownloadText == "—");

            var launch = StartupIntegration.CreateStartInfo("--handoff", "test token", "--show-processes");
            Check("launch arguments kept separately", launch.ArgumentList.TakeLast(3).SequenceEqual(new[] { "--handoff", "test token", "--show-processes" }));
            bool dotnet = string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase);
            Check("launch host/DLL supported", dotnet
                ? Path.IsPathFullyQualified(launch.ArgumentList[0]) && File.Exists(launch.ArgumentList[0]) && launch.ArgumentList[0].EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                : launch.ArgumentList.Count == 3);
            Check("autostart quotes executable", StartupIntegration.CommandLine.StartsWith($"\"{Environment.ProcessPath}\"", StringComparison.Ordinal));

            var frame = new TaskbarFrame(0, new System.Drawing.Rectangle(0, 1000, 1920, 48),
                new System.Drawing.Rectangle(1700, 1000, 220, 48), 1);
            Check("taskbar safe slot", TaskbarDock.FreeSlot(frame, 1200) == new System.Drawing.Rectangle(1204, 1000, 492, 48));
            Check("taskbar no space", TaskbarDock.FreeSlot(frame, 1700).IsEmpty);
            var scaled = frame with { Scale = 2 };
            Check("taskbar DPI spacing", TaskbarDock.FreeSlot(scaled, 1200).Left == 1208);
            int shellProcess = Environment.ProcessId == 1 ? 2 : 1;
            Check("taskbar application control included", TaskbarDock.IsLayoutControl("Taskbar.TaskListButtonAutomationPeer", shellProcess));
            Check("taskbar expanded chevron excluded", !TaskbarDock.IsLayoutControl("SystemTray.NormalButton", shellProcess));
            Check("taskbar tray controls excluded", !TaskbarDock.IsLayoutControl("SystemTray.OmniButton", shellProcess));
            Check("taskbar own window excluded", !TaskbarDock.IsLayoutControl("Button", Environment.ProcessId));
            Check("taskbar tray change keeps content geometry", frame.SameLayout(frame with { Tray = new(1600, 1000, 320, 48) }));
            Check("taskbar DPI change invalidates geometry", !frame.SameLayout(scaled));
            Check("taskbar Explorer change invalidates geometry", !frame.SameLayout(frame with { Handle = 1 }));

            Check("GPU caption splits physical label", OverviewWindow.GpuCaption("NVIDIA GeForce RTX 4060 Ti（物理 GPU 0）") == ("GPU 0", "NVIDIA GeForce RTX 4060 Ti"));
            Check("GPU caption keeps physical index", OverviewWindow.GpuCaption("AMD adapter（物理 GPU 12）") == ("GPU 12", "AMD adapter"));
            Check("GPU caption keeps vendor parentheses", OverviewWindow.GpuCaption("Intel（R）Graphics") == ("GPU", "Intel（R）Graphics"));
            Check("GPU caption rejects malformed suffix", OverviewWindow.GpuCaption("Adapter（物理 GPU x）") == ("GPU", "Adapter（物理 GPU x）"));
            Check("GPU caption keeps incomplete name", OverviewWindow.GpuCaption("Adapter（物理 GPU 0") == ("GPU", "Adapter（物理 GPU 0"));
            Check("overview pending stays inline", OverviewWindow.DetailPendingText("采样中") == "等待采样…");
            Check("overview disk warmup stays inline", OverviewWindow.DetailPendingText("系统卷读写预热中") == "预热中…");
            Check("overview GPU warmup stays inline", OverviewWindow.DetailPendingText("GPU 预热中") == "预热中…");
            Check("overview ready has no pending text", OverviewWindow.DetailPendingText(null) is null);
            Check("overview genuine error stays visible", OverviewWindow.DetailPendingText("GPU 数组读取失败") is null);
            Check("overview combined warmup error stays visible", OverviewWindow.DetailPendingText("GPU 预热中；DXGI 读取失败") is null);

            var screen = new System.Drawing.Rectangle(0, 0, 1920, 1080);
            Check("fullscreen exact bounds", TaskbarDock.CoversScreen(screen, screen));
            Check("fullscreen rounded bounds", TaskbarDock.CoversScreen(new(2, 2, 1916, 1076), screen));
            Check("fullscreen larger bounds", TaskbarDock.CoversScreen(new(-8, -8, 1936, 1096), screen));
            Check("fullscreen work area rejected", !TaskbarDock.CoversScreen(new(0, 0, 1920, 1032), screen));
            Check("fullscreen title area rejected", !TaskbarDock.CoversScreen(new(0, 30, 1920, 1050), screen));
            Check("fullscreen other monitor rejected", !TaskbarDock.CoversScreen(new(1920, 0, 1920, 1080), screen));
            Check("fullscreen empty rejected", !TaskbarDock.CoversScreen(System.Drawing.Rectangle.Empty, screen));
        }
        catch (Exception error)
        {
            failures++;
            lines.Add($"FAIL unexpected exception: {error}");
        }
        finally
        {
            try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
            catch (Exception error) { failures++; lines.Add($"FAIL cleanup: {error.Message}"); }
        }
        lines.Add($"Checks={checks}; Failures={failures}");
        try { File.WriteAllLines(Path.Combine(Path.GetTempPath(), "SysPulse-selftest.log"), lines); }
        catch { return 1; }
        return failures == 0 ? 0 : 1;
    }
}
