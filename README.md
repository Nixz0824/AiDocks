# AiDocks

Windows 屏幕边缘的 AI 监测黑舌。没有托盘，不进 Alt+Tab，闲时收成贴边的一小片；划过去展开圆环，再点开详情卡。

同一套外观，三条产品线，按需要只开一个：

| 子线 | 程序 | 做什么 |
| --- | --- | --- |
| **AI 额度 + AI 网络** | TwinDock | 额度环 + 线路延迟、丢包、官方状态 |
| **AI 额度** | QuotaDock | 只看订阅额度 |
| **AI 网络** | LineDock | 只看海外 AI 线路稳不稳 |

64 位 Windows。单文件 exe，不用装 .NET。

贴边黑舌的交互灵感来自 Vinz（[@hivinz_](https://x.com/hivinz_)）的 macOS 应用 [Codenotch](https://github.com/vinzdg/codenotch)。他当时只做了 Mac，这里是 Windows 上的独立重写，不是官方移植，也没有用他的源码或品牌。详见 [ATTRIBUTION.md](ATTRIBUTION.md)。

---

## AI 额度 + AI 网络 · TwinDock

一张卡里同时看剩余额度和当前线路。适合一直开着写代码、要顺手确认「还能用、路通不通」。

<p>
<img src="docs/screenshots/twin-codex.png" alt="TwinDock：Codex 额度与线路详情" width="270" />
<img src="docs/screenshots/twin-grok.png" alt="TwinDock：Grok 额度与线路详情" width="270" />
<img src="docs/screenshots/twin-picker.png" alt="TwinDock：加号菜单" width="270" />
</p>

详情卡上半是额度窗口（5 小时 / 周 / 月），下半是延迟折线、丢包、抖动、官方状态灯，以及国内对照和出口节点。顶部 `+` 勾选要盯的服务商、开机启动、检查更新。

---

## AI 额度 · QuotaDock

只监测订阅额度。登录过本机 CLI / 桌面端之后会自动读，不要求再输密码。

<p>
<img src="docs/screenshots/quota-codex.png" alt="QuotaDock：Codex 额度详情" width="270" />
<img src="docs/screenshots/quota-grok.png" alt="QuotaDock：Grok 额度详情" width="270" />
<img src="docs/screenshots/quota-picker.png" alt="QuotaDock：加号菜单" width="270" />
</p>

目前可读：

- **Codex** / **ChatGPT**：本机 ChatGPT / Codex 登录
- **Grok**：`grok login` 后的本机凭据
- **Claude**：Claude Code / Claude Desktop
- **Cursor**：Cursor 桌面应用登录
- **Gemini**：Gemini CLI / Google 登录
- **OpenCode Go**：`opencode auth login` 或本机 API Key

没登录时圆环显示 `—`，不会向模型服务发生成请求，也不消耗额度。

---

## AI 网络 · LineDock

盯的是「走当前代理，连国外各大 AI 稳不稳」，不是泛泛的网速测试。延迟颜色：绿快、黄一般、橙偏慢、红很慢。

<p>
<img src="docs/screenshots/line-grok.png" alt="LineDock：Grok 线路详情" width="270" />
<img src="docs/screenshots/line-codex.png" alt="LineDock：Codex 线路详情" width="270" />
<img src="docs/screenshots/line-picker.png" alt="LineDock：加号菜单" width="270" />
</p>

会探测 Grok、Codex、Claude、Gemini、Cursor；识别本机已生效的代理（Clash / tntcloud / 云云等，未知客户端也会扫本地端口）。详情卡给出延迟、丢包、抖动、官方状态，以及国内对照和出口地区。

---

## 怎么用

1. 打开 [Releases](https://github.com/Nixz0824/AiDocks/releases/latest)，按需要下载其中一个 exe：`TwinDock.exe` / `QuotaDock.exe` / `LineDock.exe`。不用装 .NET，双击就能开。
2. 贴在屏幕左或右边缘；拖黑舌可以换边、换显示器。
3. 指针靠近展开圆环；悬停或单击看详情；移开会收回。
4. 顶部 `+`：勾选服务商、开机启动、检查更新。底部 `×` 退出。
5. 第一次可能被 SmartScreen 拦截，选「仍要运行」。再开一次会关掉旧实例，只留最新这一份。

三条线可以同时开，叠在一起时把黑舌上下拖开即可。

## 代理和登录，换软件也能用

不要求特定 VPN / 加速器品牌。只要本机已经能出国，程序会按这个顺序走当前通路：

1. TUN / TAP 隧道（Clash / sing-box / LetsVPN / WARP 等开了虚拟网卡）
2. 系统代理或 PAC
3. `HTTP_PROXY` / `HTTPS_PROXY` / `ALL_PROXY`
4. 本机 HTTP / SOCKS 端口（认识 Clash、v2rayN、tntcloud、云云等；不认识的客户端只要在听端口，也会当本地代理用）

额度查询走同一条通路，所以换了加速器不会导致「圆环全是网络不可用」。没开代理、直连被墙时，线路会显示未通/阻断，额度可能暂时读不到——这是网络本身的问题，不是某个品牌没适配。

额度只读本机已经登录过的 CLI / 桌面端，不要求再输密码。换用 Codex Desktop、ChatGPT Desktop、Claude Code、Cursor Nightly、Gemini CLI 等常见安装位置都能找到登录。没登录的那一家圆环显示 `—`，不会去消耗额度。

## 自己编译

需要 [.NET 10 SDK](https://dotnet.microsoft.com/download) 和 64 位 Windows。

```powershell
dotnet publish LineDock\LineDock.csproj   -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o dist\LineDock
dotnet publish QuotaDock\QuotaDock.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o dist\QuotaDock
dotnet publish TwinDock\TwinDock.csproj   -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o dist\TwinDock
```

自检：

```powershell
dotnet run --project LineDock  -- --self-test
dotnet run --project QuotaDock -- --self-test
dotnet run --project TwinDock  -- --self-test
```

## 密钥

不把 Token、OAuth secret、本机 `auth.json` 提交进仓库。额度只读各官方 CLI / 桌面端已经写在本机的登录。细节见 [SECURITY.md](SECURITY.md)。

## 数据与隐私

- 额度请求只发给签发该登录的官方接口（OpenAI / Anthropic / xAI / Cursor / Google / OpenCode），不经过第三方中转。
- 线路探测走本机系统代理或发现的本地 HTTP/SOCKS，目标是各 AI 站点与公开状态源。
- 不调用模型生成接口，不上传对话，不写遥测。
- 设置在 `%APPDATA%\<程序名>\settings.json`。额度缓存不含 Token，在 `%LOCALAPPDATA%\<程序名>\`。

额度接口不是服务商对外承诺的稳定 API，对方改返回结构后，对应圆环可能暂时读不到。

## 目录

```
AiDocks/
  LineDock/     AI 网络
  QuotaDock/    AI 额度
  TwinDock/     AI 额度 + AI 网络
  latest.json   检查更新用的版本与下载地址
  docs/screenshots/
```

更细的额度 Provider 说明见 [QuotaDock/README.md](QuotaDock/README.md)。归属见 [ATTRIBUTION.md](ATTRIBUTION.md)。

## 许可

[MIT](LICENSE)
