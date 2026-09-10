namespace AguiGroupChat.Agents;

/// <summary>
/// 服务端技能沙箱的<b>实际可用运行时清单</b>，用于在「自然语言生成技能」与「技能自测自动修复」
/// 的提示词里明确告知模型：哪些命令能跑、哪些装了也没有。
///
/// <para>
/// 为什么需要它：早先的提示词只说明“目标平台是 Linux/Windows、写 bash 还是 PowerShell”，
/// 但没说要跑的东西<b>是否真的存在</b>。模型在需要“生成 Word / 处理图片 / 解析 JSON”时，
/// 会很自然地选 Python（<c>python3 - &lt;&lt;'PY' ... from docx import Document</c>）——
/// 而镜像里根本没有 python3，技能一跑就是 <c>command not found</c> / 退出码 127。
/// 实测踩到过：技能库里出现了一个自建的 <c>docx_report_2</c>（kind=shell，走 Python + python-docx），
/// 运行必然失败，还会和内置的 dotnet 版 docx 技能抢模型的选择。
/// </para>
///
/// <para>
/// 做法：把“有什么 / 没有什么”写进提示词，让模型第一次就选对技术栈，
/// 而不是等盲跑失败了再靠修复轮次去“劝”——修复轮次有限，且模型可以坚持原方案。
/// </para>
/// </summary>
public static class SkillSandboxCapabilities
{
    /// <summary>
    /// 服务端 Linux 容器（本项目 Dockerfile 镜像）里<b>确认可用</b>的命令。
    /// 注意与 Dockerfile 的 apt 安装清单保持一致：目前只装了 curl / fonts-*。
    /// </summary>
    private static readonly string[] LinuxAvailable =
    [
        "bash", "sh", "dash",          // 三种 POSIX shell
        "coreutils（sed/awk/grep/sort/head/tail/cut/tr/wc/find/xargs/date/printf 等）",
        "perl",                         // 基础镜像自带
        "curl",                         // Dockerfile 显式安装（健康检查也用）
        "dotnet",                       // 平台自身运行时（*nix 容器）
        "tar", "gzip", "base64",
    ];

    /// <summary>服务端 Linux 容器里<b>确认缺失</b>的命令。列出来是因为这些恰恰是模型最常想用的。</summary>
    private static readonly string[] LinuxMissing =
    [
        "python / python3 / pip（无任何 Python 运行时，也装不了第三方 Python 库）",
        "node / npm / npx",
        "pwsh / powershell",
        "git", "jq", "zip / unzip", "wget",
        "ImageMagick（convert）", "ffmpeg",
        "libreoffice / pandoc / wkhtmltopdf 等文档转换工具",
    ];

    /// <summary>是否处于本项目自带的 Linux 容器沙箱（Docker 部署）。</summary>
    private static bool IsContainerLikeSandbox =>
        !OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS();

    /// <summary>
    /// 给模型看的「可用运行时」说明段，按<b>目标执行位置</b>分流。
    /// </summary>
    /// <param name="forClient">
    /// true 表示目标是<b>用户那台机器</b>（本机/客户端技能，经本机桥执行）：只有一些通用提醒，
    /// 不能把服务端容器的清单写进去。
    /// </param>
    public static string Describe(bool forClient = false)
    {
        if (forClient)
        {
            // 本机执行：能力取决于用户自己的机器，平台无法枚举。
            // 只提醒“别假设装了某个运行时”，并引导到不依赖外部命令的 dotnet。
            return "执行环境可用能力：" +
                "该技能将在<b>用户本机</b>执行（经本机桥），可用命令行工具取决于那台机器实际安装情况，" +
                "平台无法枚举，因此<b>不要假设已安装 python / node / jq 等运行时</b>。" +
                "若需求必须依赖某个外部程序，优先改用 kind=dotnet（C# 源码由本机 .NET 运行时编译执行，不依赖外部命令），" +
                "或在正文中先检测该程序是否存在、不存在时给出清晰提示。";
        }

        if (!IsContainerLikeSandbox)
        {
            // Windows 桌面版 / macOS 自托管：宿主即用户本机，能力取决于该机安装情况。
            // 仍要提醒“别假设装了 Python”，但清单不能写死。
            return "服务端沙箱可用能力：" +
                "该环境是宿主本机（非固定容器），可用的命令行工具取决于用户机器实际安装情况；" +
                "请勿假设已安装 python / node / pwsh 等运行时。若需求的实现必须依赖某个外部程序，" +
                "应优先改用 kind=dotnet（C# 源码，由平台自带 .NET 运行时编译执行，不依赖外部命令），" +
                "或在正文中显式检查该程序是否存在并给出清晰提示。";
        }

        return
            "服务端沙箱（Linux 容器）可用能力——<b>生成正文时必须严格据此选择技术栈</b>：\n" +
            "【可用】" + string.Join("、", LinuxAvailable) + "。\n" +
            "【不可用，写了必然失败（command not found）】" + string.Join("；", LinuxMissing) + "。\n" +
            "据此给两条硬性要求：\n" +
            "1) kind=shell 的正文<b>严禁</b>调用 python / python3 / node / pwsh / jq / git 等上表“不可用”的命令，" +
            "也不要指望通过 apt / pip / npm 现场安装（沙箱无网络安装权限与相应包管理器）。\n" +
            "2) 若需求本质上需要 Python 生态（如 python-docx / pandas / Pillow）、或需要文档转换类外部工具，" +
            "<b>不要</b>硬写 shell，" +
            (OperatingSystem.IsWindows()
                ? "应改用 kind=dotnet。"
                : "应改用 kind=dotnet（C# 源码由平台自带 .NET 运行时编译执行，可访问 BCL，必要时可声明 #r \"nuget: 包名, 版本\" 联网还原）。") +
            "\n" +
            "3) shell 技能只用来做「用 bash + coreutils + curl + perl 就能完成」的事（文本处理、文件操作、HTTP 调用等）。";
    }
}
