# 第三方声明

- 界面使用部分 [Lucide](https://lucide.dev/) 图标。保留 [Lucide 许可](Assets/Lucide-LICENSE.txt)，程序也内嵌了该文件。
- 采样与布局设计参考了 [LiteMonitor](https://github.com/Diorser/LiteMonitor/tree/3b91b5c) 和 [TrafficMonitor](https://github.com/zhongyang219/TrafficMonitor/tree/930f175)。SysPulse 使用自己的 WPF 窗口、任务栏停靠逻辑和计数器实现；这些参考项目不随包分发，SysPulse 的 MIT 许可不覆盖它们。TrafficMonitor 声明 Anti-996 许可，LiteMonitor 未声明许可证，不能将其代码或资源视为可任意复制。
- 应用依赖下列 Microsoft/.NET 组件，当前 NuGet 元数据均声明 MIT。未附许可正文的包，正文见 [Microsoft MIT 许可](Assets/Microsoft-MIT-LICENSE.txt)，与上游 [diagnostics](https://github.com/dotnet/diagnostics/blob/main/LICENSE.TXT)、[PerfView](https://github.com/microsoft/perfview/blob/main/LICENSE.TXT) 和 [runtime](https://github.com/dotnet/runtime/blob/main/LICENSE.TXT) 一致。

| NuGet 包 | 当前版本 |
| --- | --- |
| Microsoft.Diagnostics.Tracing.TraceEvent | 4.0.0 |
| System.Management | 10.0.14 |
| Microsoft.Diagnostics.NETCore.Client | 0.2.661903 |
| Microsoft.Extensions.DependencyInjection.Abstractions | 8.0.2 |
| Microsoft.Extensions.Logging.Abstractions | 8.0.3 |
| System.Reflection.TypeExtensions | 4.7.0 |

- 自包含包还包含 [.NET 与 Windows Desktop 运行时](https://github.com/dotnet/core/blob/main/third-party-notices.md)。发布脚本从实际使用的运行时包与 NuGet 包复制许可及第三方声明；最终版本清单见包内 `DEPENDENCIES.txt`。各组件适用自己的许可，不因 SysPulse 自有源码采用 MIT 而改变。
