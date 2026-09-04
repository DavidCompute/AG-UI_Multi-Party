using System.Text.RegularExpressions;

namespace AguiGroupChat.Agents;

/// <summary>
/// 技能类型：定义技能的「实现形态」。
///   - <see cref="Shell"/>：一段可执行命令 / 脚本（bash / python / node 等），在技能专属沙箱内运行。
///   - <see cref="Http"/>：调用一个 HTTP(S) 接口（method + url + headers/body 模板）。
///   - <see cref="Prompt"/>：纯提示词 / 流程模板（无可执行代码，给宿主模型的一段指令，由模型自带推理 / 聚合）。
///   - <see cref="Dotnet"/>：一段 C# 源码：服务端用 Roslyn 动态编译受限执行；或（executionLocation=client）+ 本机桥/内网机在本机编译执行（管理员建立）。
/// </summary>
public enum AgentSkillKind
{
    /// <summary>命令 / 脚本：在技能专属沙箱目录里执行（需批准）。</summary>
    Shell,

    /// <summary>HTTP 调用：请求外部接口（SSRF / 内网防护）。</summary>
    Http,

    /// <summary>提示词 / 流程模板：无可执行代码，给模型的说明。</summary>
    Prompt,

    /// <summary>C# 源码技能：Roslyn 动态编译受限执行（管理员建立）；executionLocation=server → 服务端编译；client → 本机桥在该用户机器/内网机编译执行。</summary>
    Dotnet,

    /// <summary>
    /// 受控「组织落库」技能：本身不可作为 prompt/shell/http/dotnet 执行；仅系统管理员可建/编辑/删除，
    /// 被挂载它的数字员工作为“把一支组织直接部署到库”的能力（经唯一官方引擎落库、仅管理员放行）。
    /// SkillRunner 不为此类提供普通执行体；运行期由 AgentCatalog 转换为受控部署动作。
    /// 命名带下画线以稳定匹配技能库 wire 的 kind 字符串 "org_deploy"（如其它 kind 一样，枚举成员名=小写 wire 名），
    /// 避免 Enum.TryParse("org_deploy") 因找不到同名成员而静默退化（否则会落成 Prompt / 失去特权闸）。
    /// </summary>
    Org_deploy,
}

/// <summary>
/// 技能执行位置：决定这个技能在<b>哪里</b>真正执行。
/// 默认 <see cref="Server"/>：由服务端（本 Hub 的 SkillRunner）执行，行为与既有版本完全一致，<b>现有技能不受影响</b>。
/// 选 <see cref="Client"/>：模型调用时服务端<b>不执行</b>，把调用与运行配置下发给前端，由前端（WebView / 浏览器 / 本机桥）执行后回传结果。
/// </summary>
public enum AgentSkillExecutionLocation
{
    /// <summary>服务端执行（默认）：沿用现有 SkillRunner 在 Hub 进程内执行。</summary>
    Server,

    /// <summary>客户端执行：服务端只转发调用，由前端执行并回传结果。</summary>
    Client,
}

/// <summary>
/// 可复用技能定义（OpenClaw 风格）：一段「能跑的功能 / 可复用的提示词」，独立于具体数字员工，
/// 存于技能库，可被任意数字员工挂载复用。
/// 与旧的 <see cref="AgentSkillConfig"/>（调另一个智能体代为回答）语义完全分开。
/// </summary>
public sealed class AgentSkillDefinition
{
    /// <summary>技能唯一 ID（给模型的工具名，须符合 OpenAI 工具名规范：字母/数字/下划线/连字符；可中文意译但运行时映射为 ASCII 工具名）。</summary>
    public string SkillId { get; set; } = "";

    /// <summary>技能显示名（管理界面）。</summary>
    public string Name { get; set; } = "";

    /// <summary>给模型的调用说明：何时调用、需传哪些参数、返回什么。</summary>
    public string Description { get; set; } = "";

    /// <summary>技能类型：shell / http / prompt。</summary>
    public AgentSkillKind Kind { get; set; } = AgentSkillKind.Prompt;

    /// <summary>
    /// 技能正文：
    ///   - shell：脚本 / 命令文本（多行脚本直接执行）；
    ///   - http：JSON 配置（method / url / headers / body 模板，可选）；
    ///   - prompt：提示词 / 流程模板正文。
    /// </summary>
    public string Body { get; set; } = "";

    /// <summary>
    /// 可选：技能参数（给模型的 JSON 说明，如 <c>[{"name":"query","description":"...","required":true}]</c>）。
    /// shell / http 运行时把它们作为输入参数注入执行环境。
    /// </summary>
    public string ParametersJson { get; set; } = "";

    /// <summary>脚本解释器 / 运行时：shell 类型专用（如 bash / python3 / node）。留空 = 由 Body 首行 shebang 决定，否则默认按多行脚本用 bash。</summary>
    public string? Interpreter { get; set; }

    /// <summary>HTTP 请求超时秒数（默认 30）。</summary>
    public int HttpTimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// 是否需人工批准后执行（默认 true）。代码 / HTTP 技能一律默认需批准（安全兜底）；
    /// 纯提示词技能可设 false 免审批。
    /// </summary>
    public bool RequiresApproval { get; set; } = true;

    /// <summary>执行位置（默认服务端 = 现状不变）。选客户端时见 <see cref="ClientRunner"/>。</summary>
    public AgentSkillExecutionLocation ExecutionLocation { get; set; } = AgentSkillExecutionLocation.Server;

    /// <summary>
    /// 客户端执行所需的前端运行配置（仅 <see cref="ExecutionLocation"/> 为 <see cref="AgentSkillExecutionLocation.Client"/> 时有效）。
    /// 由前端执行器解析：
    ///   - kind=<c>http</c>：JSON 配置，如 <c>{"method":"GET","url":"https://...","headers":{},"body":null}</c>（浏览器侧 fetch，沿用 URL scheme 白名单）；
    ///   - kind=<c>shell</c>：JSON 配置，如 <c>{"command":"...","cwd":".","timeoutSec":30}</c>，前端经<b>本机桥</b>交桌面壳执行（需审批 + 隔离目录）。
    /// 留空则前端用技能的 kind 与 Body 作默认执行依据。
    /// </summary>
    public string? ClientRunner { get; set; }

    /// <summary>创建者 userId（系统内置为 null）。</summary>
    public string? OwnerId { get; set; }

    /// <summary>技能名 / 智能体工具名的合法模式（OpenAI 工具名：字母数字下划线连字符）。</summary>
    private static readonly Regex ToolNamePattern = new("^[a-zA-Z0-9_-]{1,64}$", RegexOptions.Compiled);

    /// <summary>校验 SkillId 是否可直接作为工具名（ASCII 工具名）。</summary>
    public static bool IsValidAsciiToolId(string skillId)
        => !string.IsNullOrWhiteSpace(skillId) && ToolNamePattern.IsMatch(skillId);

    /// <summary>
    /// 由任意用户自定义 ID（可含中文 / 空格）生成 ASCII 工具名：替换为下划线、去重后缀。
    /// 用 <paramref name="occupied"/> 规避冲突（会就地更新占用集合）。
    /// </summary>
    public static string ToAsciiToolId(string raw, ISet<string> occupied, string fallback = "skill")
    {
        var safe = Regex.Replace(raw ?? "", "[^a-zA-Z0-9_-]", "_").Trim('_');
        if (safe.Length == 0) safe = fallback;
        if (safe.Length > 40) safe = safe[..40];
        var id = safe;
        for (var i = 2; !occupied.Add(id); i++) id = $"{safe}_{i}";
        return id;
    }
}
