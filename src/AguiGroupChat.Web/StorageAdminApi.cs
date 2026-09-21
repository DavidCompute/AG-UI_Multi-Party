using AguiGroupChat.Hub.Infra;
using AguiGroupChat.Hub.Models;
using AguiGroupChat.Hub.Storage;
using AguiGroupChat.Hub.Users;

namespace AguiGroupChat.Web;

/// <summary>
/// 存储治理（管理员控制台）：
///   GET  /ag-ui/admin/storage         —— 附件占用统计 + 可回收空间（只读，随时可看）
///   POST /ag-ui/admin/storage/reclaim —— 回收无引用的孤儿附件（<b>默认关闭</b>，需显式开启配置）
///
/// <para>
/// 背景：清空 / 删除话题、撤回消息、账号擦除都只删“消息”，不删附件文件（这是刻意的：附件是用户
/// 上传的资料）。于是磁盘上会积累大量“对话里已无入口”的孤儿文件，且只增不减。
/// 这个页面把账算清楚，并把删除动作做成需要显式开启的能力。
/// </para>
/// </summary>
public static class StorageAdminApi
{
    public static void MapStorageAdminApi(this WebApplication app)
    {
        var root = app.MapGroup("/ag-ui/admin/storage");

        // 只读统计：管理员可看；不注册回收能力（轻量宿主）时如实说明，而不是显示 0
        root.MapGet("/", (StorageGovernanceOptions options, IAttachmentLifecycle? lifecycle) =>
        {
            if (lifecycle is null)
                return Results.Ok(new
                {
                    available = false,
                    allowReclaim = false,
                    graceHours = options.GraceHours,
                    totalFiles = 0, totalBytes = 0L,
                    referencedFiles = 0,
                    unreferencedFiles = 0, unreferencedBytes = 0L,
                    orphanFiles = 0, orphanBytes = 0L,
                });

            var stats = lifecycle.Inspect(TimeSpan.FromHours(options.GraceHours));
            return Results.Ok(new
            {
                available = true,
                allowReclaim = options.AllowReclaim,
                graceHours = stats.GracePeriodHours,
                totalFiles = stats.TotalFiles,
                totalBytes = stats.TotalBytes,
                referencedFiles = stats.ReferencedFiles,
                // 无引用（含宽限期内）——只报“可回收”会让管理员误以为没有任何浪费
                unreferencedFiles = stats.UnreferencedFiles,
                unreferencedBytes = stats.UnreferencedBytes,
                orphanFiles = stats.OrphanFiles,
                orphanBytes = stats.OrphanBytes,
            });
        }).AddEndpointFilter(new WebIdentity.RequireAdminFilter());

        // 回收：默认关闭（StorageGovernance:AllowReclaim），开启后才允许；每次回收写审计
        root.MapPost("/reclaim", (HttpContext ctx, AuthService auth, AuditLogService audit,
            StorageGovernanceOptions options, IAttachmentLifecycle? lifecycle) =>
        {
            if (!options.AllowReclaim)
                return Results.Json(new AguiError(ErrorCodes.GroupPermissionDenied,
                        "存储回收未开启（需配置 StorageGovernance:AllowReclaim=true）"),
                    statusCode: StatusCodes.Status403Forbidden);
            if (lifecycle is null)
                return Results.Json(new AguiError(ErrorCodes.BadRequest, "当前宿主未注册附件回收能力"),
                    statusCode: StatusCodes.Status400BadRequest);

            var me = WebIdentity.UserId(ctx)!;
            var result = lifecycle.ReclaimOrphans(TimeSpan.FromHours(options.GraceHours), dryRun: false);
            audit.Record("storage.reclaim", me, auth.GetUser(me)?.Username,
                detail: $"回收孤儿附件 {result.DeletedFiles} 个 / {result.DeletedBytes} 字节（宽限期 {options.GraceHours} 小时）");
            return Results.Ok(new { ok = true, files = result.DeletedFiles, bytes = result.DeletedBytes });
        }).AddEndpointFilter(new WebIdentity.RequireAdminFilter());
    }
}

/// <summary>
/// 存储治理配置（appsettings 的 <c>StorageGovernance</c> 节；两个组合根各自注册为单例）。
/// </summary>
public sealed class StorageGovernanceOptions
{
    /// <summary>
    /// 是否允许管理员回收存储（删除无引用的孤儿附件）。<b>默认 false</b>：这是破坏性操作，
    /// 必须显式开启（<c>StorageGovernance:AllowReclaim</c> / 环境变量 <c>StorageGovernance__AllowReclaim=true</c>）。
    /// 关闭时统计照常可看，只是回收按钮不可用。
    /// </summary>
    public bool AllowReclaim { get; set; }

    /// <summary>
    /// 孤儿附件判定宽限期（小时，默认 168 = 7 天）。
    ///
    /// <para>
    /// 为什么必须有宽限期：文件是“先上传、后随消息发送”的——刚上传还没点发送、正在生成的技能产物，
    /// 此刻都处于“暂时无引用”状态。没有宽限期就会把它们当孤儿删掉。
    /// </para>
    /// </summary>
    public int OrphanGraceHours { get; set; } = 168;

    /// <summary>归一化后的宽限期（1 小时 ~ 365 天），避免配置写 0 / 负数把宽限期变成“立刻就删”。</summary>
    public int GraceHours => Math.Clamp(OrphanGraceHours, 1, 24 * 365);
}
