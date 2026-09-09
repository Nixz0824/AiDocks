# 归属

## 外观灵感 · Codenotch

贴边黑舌、闲时收成一小片、划过展开圆环再打开详情卡，这个交互来自 Vinz（[@hivinz_](https://x.com/hivinz_)）的 macOS 应用 **Codenotch**。他的产品当时只支持 Mac，AiDocks 是对照公开帖文和演示、在 Windows 上用 WPF 重写的独立实现。

- 作者：[Vinz @hivinz_](https://x.com/hivinz_)
- 发布帖：[Codenotch 发布](https://x.com/hivinz_/status/2094754722536460686)
- 开源：[vinzdg/codenotch](https://github.com/vinzdg/codenotch)（[宣布开源](https://x.com/hivinz_/status/2096256684801060980)）
- 站点：[hivinz.com](https://hivinz.com)

没有复制 Codenotch 的源码、名称、图标或品牌资产。不是官方 Windows 移植，也没有用他的截图当本仓库宣传图。窗口几何、动效和 C# 代码都是本仓库自己写的。

## 额度数据流

QuotaDock / TwinDock 的 Codex、Grok Provider 数据流、字段选择和 xAI issuer 校验参考了：

- [Gengchou](https://github.com/ynjmxn/gengchou) — MIT

OpenCode Go 额度接口 `GET /zen/go/v1/usage` 的窗口语义来自官方仓库 [anomalyco/opencode](https://github.com/anomalyco/opencode)。方形 Logo 取自官方品牌资产，为适配深色窄轨做了单色化简。

Kimi、GLM（ChatGLM）、Qoder、Trae、MiniMax、WorkBuddy（CodeBuddy）的单色路径来自 [lobehub/lobe-icons](https://github.com/lobehub/lobe-icons) 的 `static-svg`（MIT），填进 WPF Path Mini-Language，颜色跟现有 Codex / Grok 一样用前景白。

## 国内订阅额度接口

Kimi、WorkBuddy、GLM、Qoder、Trae、MiniMax 的额度接口路径、鉴权方式和响应字段，对照的是各家自己的客户端与公开实现，没有逆向任何私有协议：

- **Kimi**：官方 Kimi Code CLI（[MoonshotAI/kimi-code](https://github.com/MoonshotAI/kimi-code)）`packages/oauth/src/managed-usage.ts` 的 `GET /coding/v1/usages` 与字段语义（`usage` 为周窗口、`limits[].window/detail` 为 5 小时窗口、`totalQuota` 为总订阅额度）。
- **GLM**：Coding Plan 配额 `GET /api/monitor/usage/quota/limit` 的 `limits[]`（`TOKENS_LIMIT`/`CREDIT_LIMIT`、`unit` 3=小时 / 6=周、`nextResetTime` 毫秒）与「国内 host 裸 Key、国际 host Bearer」的差异，参考 [lidge-jun/opencodex](https://github.com/lidge-jun/opencodex) 的 `src/providers/quota.ts` 与其 issue #1168 记录。
- **Qoder**：`GET {openapi}/api/v2/quota/usage`（`userQuota` / `addOnQuota` / `totalUsagePercentage`），参考 [mmqz/cpa-multi-plugins](https://github.com/mmqz/cpa-multi-plugins) 的 `plugins/qoder`（含 CN `openapi.qoder.com.cn` 与 intl `openapi.qoder.sh` 的区域差异）。
- **Trae**：`POST /trae/api/v2/pay/ide_user_ent_usage` 的 `user_entitlement_pack_list` 三层 quota 回退与 bonus 语义，参考同一仓库的 `plugins/trae/upstream`。
- **MiniMax**：`GET {host}/v1/token_plan/remains`（`Authorization: Bearer <API Key>`，官方在 [MiniMax-AI/MiniMax-M2#99](https://github.com/MiniMax-AI/MiniMax-M2/issues/99) 的回复中给出）以及 `current_*_usage_count` 实为「剩余」而非「已用」的字段语义。
- **WorkBuddy / CodeBuddy**：`POST {host}/billing/meter/get-user-resource-summary`（`data.Packages[].CycleTotalCapacity/CycleRemainCapacity/CycleUsedCapacity`）与 `POST copilot.tencent.com/v2/billing/meter/get-user-resource`（`Accounts[].Capacity*`）两条路径，按 `www.codebuddy.cn` / `www.workbuddy.cn` / `www.workbuddy.ai` / `www.codebuddy.ai` 依次探测。

国际版与国内版在同一品牌下共用同一条接口路径与同一套字段（只有 host 不同），因此加号里每个品牌只占一项，程序按 host 顺序自动探测，不要求用户自己选区域。

## 字体

界面西文使用 SIL Open Font License 的 [Inter](https://rsms.me/inter/)。中文回退到系统已安装的 Noto Sans SC / 微软雅黑。未内嵌 Apple SF Pro 或苹方。
