# 密钥与敏感文件

这三个程序**只读取本机已有登录**，不把 Token 写进仓库、不上传到我们的服务器。

## 不会进 Git 的内容

不要提交、不要打包进发行说明：

- `.env`、API Key、OAuth client secret
- `auth.json`、`.credentials.json`、`oauth_creds.json`
- `%APPDATA%\<程序名>\settings.json`、`error.log`
- `%LOCALAPPDATA%\<程序名>\usage-cache.json`、`history.json`
- Cursor / Claude / Codex / Grok / Gemini 桌面端或 CLI 的登录库

根目录 `.gitignore` 已忽略上述常见文件名。

## 程序怎么用登录

| 来源 | 只读位置 | 发给谁 |
| --- | --- | --- |
| Codex / ChatGPT | `%CODEX_HOME%\auth.json` 或隐藏调用 `codex app-server` | OpenAI 官方额度接口 |
| Grok | `%GROK_HOME%\auth.json` | xAI 官方额度接口 |
| Claude | `~/.claude/.credentials.json` 或 `CLAUDE_CODE_OAUTH_TOKEN` | Anthropic 官方额度接口 |
| Cursor | Cursor 桌面应用本机数据库 | Cursor 官方用量接口 |
| Gemini | `~/.gemini/oauth_creds.json` | Google 官方额度接口 |
| OpenCode Go | 本机 `auth.json` / `OPENCODE_GO_API_KEY` | OpenCode 官方用量接口 |

线路监测不使用这些 Token，只走本机代理探测公开站点。

崩溃日志写在 `%APPDATA%\<程序名>\error.log`，写入前会去掉 `Bearer`、JWT、`sk-`、`client_secret` 等片段。线路历史不落盘出口 IP。

## Gemini 续期（可选）

仓库里**没有** Google OAuth client secret。本机 access token 仍可查额度；若要刷新过期 token，在用户环境里自行设置：

```
GEMINI_OAUTH_CLIENT_ID
GEMINI_OAUTH_CLIENT_SECRET
```

或把 `client_id` / `client_secret` 放进本机 `oauth_creds.json`（不要提交该文件）。

Claude 桌面端的公开 OAuth client id 可用 `CLAUDE_OAUTH_CLIENT_ID` 覆盖。

## 发现误提交

立刻轮换被暴露的密钥，从 Git 历史中删除该文件，并在 GitHub 上走 secret scanning 解除/轮换流程。不要用「再提交一次删除」当作已经从历史上抹掉。
