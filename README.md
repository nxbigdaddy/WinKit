# WinKit · Windows 图形化工具箱

一个**免安装、单文件、纯本地、不联网**的 Windows 桌面工具箱：把系统维护、文件批处理、开发运维、硬件监控四类高频操作，收进一个双击即用的图形界面。

> 当前状态：**★ 级功能全部完成** —— 282 KB 单文件、零第三方依赖、**260 个单元测试全绿**、6 个功能页可用，
> 并通过 8 项端到端验收（含清理的"预览→确认→撤销"与提权通道的"四类逆向全部被拒"）。
> 证据见 [M2-验收记录.md](docs/M2-验收记录.md)、[M1-验收记录.md](docs/M1-验收记录.md)、[M0-验收记录.md](docs/M0-验收记录.md)。

---

## 技术栈

| 项 | 取值 |
| --- | --- |
| 语言 / 运行时 | C# + **.NET Framework 4.8** |
| 界面框架 | **WPF**（Fusion 风格 + 自绘 QSS 式主题字典） |
| 构建 | `dotnet build`（SDK 风格工程，无需 Visual Studio） |
| 发布产物 | 单个 `WinKit.exe`，**零第三方依赖**（实测 **185 KB**） |
| 目标机依赖 | Win10 1903+ / Win11 自带 .NET Framework 4.8，**无需安装任何运行时** |

选择 net48 而不是 .NET 8 的原因：`System.Management` / 注册表 / WPF 全是框架自带程序集，**不复制到输出目录**，于是得到一个几 MB、秒开、几乎不会被杀软误报的单文件 exe。详见 [技术选型.md](docs/技术选型.md)。

---

## 设计原则（不可妥协）

1. **危险操作必须 预览 → 确认 → 可撤销**。所有写操作进"撤销中心"，有据可查、能回滚。
2. **只做可解释的优化**。不做"一键加速""内存释放""注册表深度优化"这类不可解释、不可回滚、收益存疑的功能。
3. **UI 永不阻塞**。任何文件遍历 / 注册表 / WMI 调用都在后台线程，带进度与取消。
4. **便携零残留**。配置跟随程序目录，卸载 = 删文件夹。
5. **最小权限，按需提权**。默认标准权限运行，只有明确的管理员动作才提权，且提权侧只接受白名单动作。
6. **默认不联网**。唯一可能联网的是可选启用的 AI 助手，且必须由你主动点击并确认；无遥测、无账号。详见「隐私与联网」。

---

## 构建

**注意：本机 `dotnet` 不在 PATH，脚本里用的是全路径。**

```powershell
# 静态检查：资源键是否存在、net48 不兼容语法、生产工程零 NuGet 依赖
powershell -File scripts\check-xaml-resources.ps1
powershell -File scripts\check-conventions.ps1

# 开发构建
& "C:\Program Files\dotnet\dotnet.exe" build src\WinKit.App\WinKit.App.csproj -c Debug

# 运行
.\src\WinKit.App\bin\Debug\WinKit.exe

# 运行全部单元测试（Core + Platform）
powershell -File scripts\test-all.ps1

# 出发布版（含"零依赖"校验与体积报告）
powershell -File packaging\build-release.ps1

# 启动冒烟测试：进程存活 / 窗口标题 / 数据目录 / 日志
powershell -File scripts\smoke-test.ps1

# UI 自动化测试：真的点开每一个导航页，检查日志有无异常
powershell -File scripts\ui-smoke-test.ps1
```

> ⚠️ 所有 `.ps1` 都带 UTF-8 BOM。Windows PowerShell 5.1 读**无 BOM** 的 UTF-8 脚本会按 GBK 解码，中文注释会直接导致语法错误 —— 重写这些脚本时务必保留 BOM。

---

## 工程结构

```
WinKit.sln
├─ src/
│  ├─ WinKit.Core/       类库：无 UI、无 P/Invoke，可毫秒级单测
│  │                    配置 / 日志 / 任务模型 / 撤销引擎 / 路径守卫
│  ├─ WinKit.Platform/   类库：系统访问
│  │                    P/Invoke 指标采集 / WMI / 注册表 / 提权 / 文件操作 / 撤销处理器
│  └─ WinKit.App/        WPF 主程序（唯一引用 WPF 的工程）
│                       Mvvm / Services / Views / ViewModels / Controls / Themes / Tasks
├─ tests/
│  ├─ WinKit.Core.Tests/       xUnit
│  └─ WinKit.Platform.Tests/   xUnit
├─ packaging/            发布脚本
├─ scripts/              开发脚本
└─ docs/                 功能规划 / 技术选型
```

### 分层纪律

`WinKit.Core` **不允许**引用 WPF，也**不允许** P/Invoke。这样核心逻辑可以用纯 pytest 风格的快速单测覆盖（毫秒级，不必启动 UI），也保住了将来迁移到 .NET 8 的退路。

### 插件机制

每个功能页实现 `IToolModule`，由 `ModuleRegistry` 用反射自动发现。
**新增功能不需要改主窗口代码** —— 这既降低了耦合，也让多个功能页可以并行开发。

### 热路径 vs 冷路径

| 路径 | 方案 | 原因 |
| --- | --- | --- |
| 每秒采样的 CPU / 内存 / 网络 / 进程 | **P/Invoke**（`GetSystemTimes`、`GlobalMemoryStatusEx`、`NetworkInterface`） | 微秒级，可以在 UI 线程直接调用 |
| 硬件信息 / SMART / 驱动签名等一次性数据 | **WMI**（`System.Management`） | 单次 50~300ms，必须走后台线程 + TTL 缓存 |

---

## 隐私与联网

WinKit **默认不联网**。唯一可能发起网络请求的功能是**可选启用**的 AI 助手，且只在**你主动点击发送并确认**之后才会联网。

| 项 | 承诺 | 由什么保证 |
| --- | --- | --- |
| **默认不联网** | 不启动 AI、不点发送，程序就一次网络请求都不发 | `AppConfig.AiEnabled` 默认 `false`；`AppServices.IsAiReady` 是唯一允许联网的判定入口 |
| **只有一个网络出口** | 全部代码里只有 `src/WinKit.Platform/Ai/DeepSeekClient.cs` 一个文件使用网络 API | **机器校验**：`scripts/check-conventions.ps1` 会在其他任何文件出现 `HttpClient`/`WebRequest`/`WebClient`/`Socket`/`TcpClient` 时报错 |
| **发送前必须预览** | 联网前弹出窗口，逐字显示**将要发送的全部内容**，且你可以先编辑再发 | AI 调用统一走 `AiLauncher.ShowPreview`，没有绕过它的入口 |
| **AI 只发最少必要的数据** | 体检只发分数与检查项文字；进程只发**进程名**；清理只发规则 Id 与数量；日志先脱敏（用户名/计算机名/疑似密钥被替换）。**任何情况下都不发送文件内容** | `PromptBuilder` 与 `TextRedactor` 是纯函数，`tests/WinKit.Core.Tests/AiPrivacyTests.cs` 逐字断言"到底发送了什么" |
| **密钥不明文落盘** | API Key 用 Windows DPAPI（当前用户）加密存于 `<数据目录>\secrets\deepseek.key`，**不写进 `config.json`**（配置会被导出迁移，密钥不该跟着走） | `ApiKeyStore` |
| **密钥与内容不进日志** | 日志只记录"发送了多少字符"，从不记录密钥或请求内容 | `AiAssistantViewModel` / `DeepSeekClient` |
| **零遥测** | 不收集、不上报任何统计数据；崩溃只在本地写日志 | — |
| **本地日志** | 操作日志（`journal.jsonl`）只记录操作元数据（路径 / 大小 / 结果），不记录文件内容 | — |
| **可审计** | 每个写操作都能在撤销中心看到 | — |

> 为什么把"只有一个网络出口"做成检查脚本而不是写在文档里：**只能靠自觉的隐私承诺不是承诺**。这条规则让"没有别的联网代码"这个事实可以被一条命令验证，而不需要通读全部代码去相信。

---

## 文档

| 文档 | 内容 |
| --- | --- |
| [M2-验收记录.md](docs/M2-验收记录.md) | ★ 级功能收口：一键体检 / 磁盘读写曲线 / 结束进程树 / 真实提权动作；提权通道的安全设计 |
| [M1-验收记录.md](docs/M1-验收记录.md) | 清理模块的设计决策、7 项验收汇总、两次"测试自身出错"的复盘 |
| [M0-验收记录.md](docs/M0-验收记录.md) | M0 实测指标、每种验证手段抓到的真实缺陷、已知限制 |
| [功能规划.md](docs/功能规划.md) | 完整功能规划、交互范式、里程碑路线图、风险登记册 |
| [功能清单.md](docs/功能清单.md) | 简版功能清单（★ = 第一版就做） |
| [技术选型.md](docs/技术选型.md) | 技术决策记录、实机验证证据、被排除的方案、打包与分发方案 |

---

## 许可证

本项目采用 [MIT 许可证](LICENSE)。

## 第三方与上游声明

本项目包含或依赖第三方组件，其版权与许可证归各自作者所有，
详见仓库内的依赖清单与相关声明文件。若本项目基于其他开源项目二次开发，
已在正文中注明上游项目与所基于的版本，本项目为**非官方**衍生作品。

## 现状与限制

- 本项目为个人开发项目，按“现状”提供，不附带任何明示或暗示的担保。
- README 与代码注释中记录的验证结论仅针对当时的具体环境与版本，不代表在其他环境下同样成立。
- 涉及本地凭据的部分统一使用系统凭据库或环境变量，仓库内不含真实密钥；
  示例配置仅使用占位符。
