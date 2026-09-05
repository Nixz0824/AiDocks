# QuotaDock · AI 额度

AiDocks 系列的额度监测子线。截图、三条产品线对照和仓库级编译说明见根目录 [README](../README.md)。

一个吸附在 Windows 屏幕左右边缘的 AI 订阅额度组件。它没有系统托盘图标：空闲时缩成屏幕边缘的一枚黑色小舌片，指针靠近后长成额度窄轨；悬停额度环时从屏幕内侧展开详情卡，点击可固定详情卡。

## 当前支持

- Codex：先读取 `%CODEX_HOME%\auth.json`；凭据缺失或过期时，短暂启动隐藏的 `codex app-server` 调用只读 `account/rateLimits/read`，查询后立即退出。
- OpenCode：读取本机 `opencode auth login` 写入的 Go API Key（`~/.local/share/opencode/auth.json` 的 `opencode-go` 条目，兼容 `opencode.json` 配置与 `OPENCODE_GO_API_KEY` 环境变量），查询官方 `GET /zen/go/v1/usage` 的 5 小时滚动、周、月三窗口。不碰 Zen 按量余额（官方无此 API，不捏造）。
- Grok：读取 `%GROK_HOME%\auth.json`；令牌被拒绝时，隐藏运行一次无生成请求的 `grok models` 让官方 CLI 尝试续期，再重试额度。
- Cursor：读取 Cursor 桌面应用本机登录，查询包含用量和 API 用量。不写入 Cursor 的登录文件。
- Claude：读取 `~/.claude/.credentials.json`，查询 5 小时限额和周限额。
- ChatGPT：与 Codex 共用 ChatGPT 登录，查询 5 小时限额和周限额。
- Gemini：读取 `~/.gemini/oauth_creds.json`，查询每日限额。
- 左右边缘磁吸、拖动黑色窄轨换边、多显示器工作区定位。
- Codenotch 风格的空闲收缩、窄轨展开、额度环绘制和 Provider 卡片交叉切换动效。
- 不进入 Alt+Tab、不显示任务栏按钮、不抢焦点；透明区域点击穿透。
- 2 分钟自动刷新；网络恢复、电脑唤醒或登录文件变化后会提前刷新。
- 最近 24 小时的最后有效额度会安全缓存；网络或登录暂时异常时显示为“上次数据”，不会直接清空。
- Windows“关闭动画”设置会自动切换为无位移动效。

## 使用

1. 运行 `QuotaDock.exe`。
2. 悬停 Codex 或 Grok 额度环查看窗口、已用比例和重置时间。
3. 悬停或单击额度环显示详情；移开后详情约 220ms 收回，单击只提供短暂阅读停留，不会永久固定。
4. 拖动黑色窄轨把组件移到另一块屏幕或另一侧。
5. 顶部 `+` 打开 Provider 与开机启动设置，底部 `×` 退出。

首次没有本地登录时会显示 `—`，不会要求输入密码，也不会保存 Token。再次打开同一个 `QuotaDock.exe` 时，旧实例会退出，只留下最新这一份。

## 发给别人

把 `QuotaDock.exe` 单独发给对方即可，不需要安装 .NET，也不需要源码。对方需要 64 位 Windows。

- 要看真实额度：对方电脑自己登录 Codex、Cursor、OpenCode Go，或已经运行过 `grok login`
- 没登录也能打开，额度显示为 `—`
- 第一次运行可能被 SmartScreen 拦截，选择「仍要运行」
- 重复双击会关掉旧窗口，只保留最新启动的这一份

## 登录要求

- Codex：先在 Codex Desktop 登录，或运行 `codex login`。
- OpenCode：先运行 `opencode auth login` 选择 OpenCode Go 并订阅，或在 `opencode.ai/auth` 复制 API Key（`opencode.json` 的 `provider.opencode-go.options.apiKey` 或 `OPENCODE_GO_API_KEY` 亦可）。
- Grok：先安装 Grok CLI 并至少运行一次 `grok login`。之后无需保持 Grok Build 窗口开启；只有官方 CLI 无法续期时才需要重新登录。
- Cursor：先在 Cursor 桌面应用登录。QuotaDock 只读取本机会话，不会保存 Token。
- Claude：先登录 Claude Code 或 Claude Desktop。
- ChatGPT：先登录 ChatGPT Desktop，或已完成 `codex login`。
- Gemini：先运行 Gemini CLI 并完成 Google 登录。

## 数据和隐私

QuotaDock 只将本机登录 Token 发送给签发它的服务商：

- Codex：`https://chatgpt.com/backend-api/wham/usage`
- OpenCode：`https://opencode.ai/zen/go/v1/usage`（Go 订阅三窗口；Zen 余额无官方 API，不查询）
- Grok：`https://cli-chat-proxy.grok.com/v1/billing?format=credits`
- Cursor：`https://api2.cursor.sh/aiserver.v1.DashboardService/GetCurrentPeriodUsage`
- Claude：`https://api.anthropic.com/api/oauth/usage`
- ChatGPT：与 Codex 相同的额度接口
- Gemini：`https://cloudcode-pa.googleapis.com/v1internal:retrieveUserQuota`

这些额度接口不是面向第三方公开承诺的稳定 API，服务商更改返回结构后，Provider 可能暂时显示不可用。应用不会调用模型生成端点，不会消耗模型额度，不包含遥测，也不会记录 Token。

设置仅写入 `%APPDATA%\QuotaDock\settings.json`。不含 Token 的最后有效额度缓存写入 `%LOCALAPPDATA%\QuotaDock\usage-cache.json`。

## 开发

```powershell
dotnet build
dotnet run -- --demo
dotnet run -- --self-test
```

新增 Provider 时实现 `Providers/IQuotaProvider.cs`，并在 `Services/QuotaService.cs` 注册。界面会自动增加额度环；不需要改停靠窗口结构。

## 设计边界

视觉和交互灵感来自 Vinz（[@hivinz_](https://x.com/hivinz_)）的 [Codenotch](https://github.com/vinzdg/codenotch)。QuotaDock 没有复制其名称、品牌图标或源代码，也不是官方移植。Provider 的安全边界和响应解析参考了 MIT 许可的 [Gengchou](https://github.com/ynjmxn/gengchou)，归属见 [ATTRIBUTION.md](ATTRIBUTION.md)。
