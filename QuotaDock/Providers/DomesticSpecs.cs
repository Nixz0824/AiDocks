using System.Net.Http;

namespace QuotaDock.Providers;

internal static class DomesticSpecs
{
    public static readonly HttpQuotaSpec[] All =
    [
        Spec("kimi-cn", "Kimi 国内", "◐", "#F5C518",
            "请先运行 kimi login，或设置 KIMI_API_KEY / MOONSHOT_API_KEY。",
            ["KIMI_API_KEY", "MOONSHOT_API_KEY", "KIMI_CN_API_KEY"],
            [@"{Home}\.kimi-code\config.toml", @"{Home}\.kimi\config.toml", @"{Home}\.kimi-code\credentials.json"],
            [
                "https://api.kimi.com/coding/v1/usage",
                "https://api.moonshot.cn/v1/users/me/balance"
            ]),
        Spec("kimi-intl", "Kimi 国际", "◐", "#F5C518",
            "请先在 platform.kimi.ai 创建 Key，或设置 MOONSHOT_API_KEY。",
            ["MOONSHOT_API_KEY", "KIMI_INTL_API_KEY"],
            [@"{Home}\.kimi-code\config.toml", @"{Home}\.kimi\config.toml"],
            ["https://api.moonshot.ai/v1/users/me/balance"]),
        Spec("workbuddy-cn", "WorkBuddy 国内", "▣", "#2A9D8F",
            "请先登录 WorkBuddy / CodeBuddy 国内桌面端。",
            ["CODEBUDDY_AUTH_TOKEN", "WORKBUDDY_TOKEN"],
            [
                @"{LocalAppData}\CodeBuddyExtension\Data\Public\auth\workbuddy-desktop.info",
                @"{LocalAppData}\CodeBuddyExtension\Data\Public\auth\codebuddy-desktop.info",
                @"{Home}\.codebuddy\settings.json"
            ],
            [
                "https://www.workbuddy.cn/billing/meter/get-user-resource-summary",
                "https://copilot.tencent.com/v2/billing/meter/get-user-resource"
            ],
            post: true,
            body: """{"productCode":"p_tcaca"}"""),
        Spec("workbuddy-intl", "WorkBuddy 国际", "▣", "#2A9D8F",
            "请先登录国际版 WorkBuddy / CodeBuddy，或设置 CODEBUDDY_AUTH_TOKEN。",
            ["CODEBUDDY_AUTH_TOKEN", "WORKBUDDY_INTL_TOKEN"],
            [
                @"{LocalAppData}\CodeBuddyExtension\Data\Public\auth\workbuddy-desktop.info",
                @"{AppData}\WorkBuddy\auth.json",
                @"{Home}\.codebuddy\settings.json"
            ],
            [
                "https://www.workbuddy.ai/billing/meter/get-user-resource-summary",
                "https://www.codebuddy.ai/billing/meter/get-user-resource-summary"
            ],
            post: true,
            body: "{}"),
        Spec("glm-cn", "GLM 国内", "▲", "#0F6FFF",
            "请先订阅 GLM Coding Plan 并设置智谱 API Key（BIGMODEL_API_KEY）。",
            ["BIGMODEL_API_KEY", "ZHIPU_API_KEY", "GLM_API_KEY"],
            [@"{Home}\.zcode\credentials.json", @"{Home}\.bigmodel\api_key"],
            ["https://open.bigmodel.cn/api/monitor/usage/quota/limit"],
            bearer: false),
        Spec("glm-intl", "GLM 国际", "▲", "#0F6FFF",
            "请先订阅 GLM Coding Plan 并设置 ZAI_API_KEY。",
            ["ZAI_API_KEY", "ZAI_KEY"],
            [@"{Home}\.zcode\credentials.json", @"{Home}\.zai\api_key"],
            ["https://api.z.ai/api/monitor/usage/quota/limit"],
            bearer: false),
        Spec("qoder-cn", "Qoder 国内", "◆", "#FF6A00",
            "请先登录 Qoder CN / qoderclicn，或设置 QODER_PAT。",
            ["QODER_PAT", "QODER_CN_TOKEN"],
            [
                @"{Home}\.qoder-cn\credentials.json",
                @"{Home}\.qodercn\auth.json",
                @"{AppData}\Qoder CN\User\globalStorage\state.json"
            ],
            [
                "https://api.qoder.com.cn/api/v1/cloud/usage",
                "https://qoder.com.cn/api/usage"
            ]),
        Spec("qoder-intl", "Qoder 国际", "◆", "#FF6A00",
            "请先登录 Qoder 国际版，或设置 QODER_TOKEN。",
            ["QODER_TOKEN", "QODER_API_KEY"],
            [@"{Home}\.qoder\credentials.json", @"{AppData}\Qoder\User\globalStorage\state.json"],
            ["https://api.qoder.com/v1/usage", "https://qoder.com/api/usage"]),
        Spec("trae-cn", "Trae 国内", "▸", "#3B82F6",
            "请先登录 Trae 国内桌面端，或设置 TRAE_CN_TOKEN。",
            ["TRAE_CN_TOKEN", "TRAE_TOKEN"],
            [
                @"{AppData}\TRAE SOLO CN\User\globalStorage\storage.json",
                @"{AppData}\Trae CN\User\globalStorage\storage.json"
            ],
            [
                "https://api.trae.cn/trae/api/v2/ug/checkin_credits/status",
                "https://api.trae.cn/icube/api/v1/user"
            ],
            post: true,
            body: "{}"),
        Spec("trae-intl", "Trae 国际", "▸", "#3B82F6",
            "请先登录 Trae 国际版，或设置 TRAE_TOKEN。",
            ["TRAE_TOKEN", "TRAE_INTL_TOKEN"],
            [@"{AppData}\Trae\User\globalStorage\storage.json", @"{AppData}\TRAE\User\globalStorage\storage.json"],
            ["https://api.trae.ai/icube/api/v1/user"],
            post: true,
            body: "{}"),
        Spec("minimax-cn", "MiniMax 国内", "▬", "#E11D48",
            "请先订阅 MiniMax Token/Coding Plan 并设置 MINIMAX_API_KEY。",
            ["MINIMAX_API_KEY", "MINIMAX_CN_API_KEY"],
            [@"{Home}\.mmx\config.json", @"{Home}\.minimax\credentials.json"],
            [
                "https://www.minimaxi.com/v1/api/openplatform/coding_plan/remains",
                "https://www.minimaxi.com/v1/token_plan/remains"
            ]),
        Spec("minimax-intl", "MiniMax 国际", "▬", "#E11D48",
            "请先订阅 MiniMax Token Plan 并设置 MINIMAX_API_KEY。",
            ["MINIMAX_API_KEY", "MINIMAX_INTL_API_KEY"],
            [@"{Home}\.mmx\config.json", @"{Home}\.minimax\credentials.json"],
            [
                "https://www.minimax.io/v1/api/openplatform/coding_plan/remains",
                "https://www.minimax.io/v1/token_plan/remains"
            ])
    ];

    public static HttpQuotaProvider? Create(HttpClient httpClient, string id)
    {
        var spec = All.FirstOrDefault(item => item.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        return spec is null ? null : new HttpQuotaProvider(httpClient, spec);
    }

    private static HttpQuotaSpec Spec(
        string id,
        string name,
        string glyph,
        string accent,
        string hint,
        string[] env,
        string[] files,
        string[] urls,
        bool post = false,
        string? body = null,
        bool bearer = true) =>
        new(id, name, glyph, accent, hint, env, files, urls, post, body, bearer);
}
