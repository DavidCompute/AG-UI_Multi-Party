using System.Text.Json;
using AguiGroupChat.Hub;
using AguiGroupChat.Hub.Infra;
using AguiGroupChat.Hub.Models;
using AguiGroupChat.Hub.Persistence;
using AguiGroupChat.Hub.Users;

namespace AguiGroupChat.Web;

/// <summary>
/// 托管 API 密钥管理（管理员控制台）：创建 / 列出 / 吊销，用于外部系统程序化接入（替代 appsettings 里
/// 明文、需重启的 <c>Auth:ApiKeys</c>）。
///   GET    /ag-ui/admin/api-keys        —— 列出（只回前缀，不回密钥）
///   POST   /ag-ui/admin/api-keys        —— 创建（返回一次性明文密钥）
///   DELETE /ag-ui/admin/api-keys/{id}   —— 吊销并删除
/// 全部仅系统管理员可用。密钥明文只在创建时返回一次，服务端只存 SHA-256 哈希。
/// </summary>
public static class ApiKeyApi
{
    public static void MapApiKeyApi(this WebApplication app)
    {
        var root = app.MapGroup("/ag-ui/admin/api-keys");

        // ---- 列出（脱敏：只回前缀）----
        root.MapGet("/", (ManagedApiKeyStore store) => Results.Ok(store.List()
            .Select(k => new
            {
                k.Id, k.Name, k.Username, k.KeyPrefix, k.CreatedAt, k.CreatedBy, k.LastUsedAt,
            })))
            .AddEndpointFilter(new WebIdentity.RequireAdminFilter());

        // ---- 创建 ----
        root.MapPost("/", (ApiKeyHttpRequest req, HttpContext ctx, AuthService auth, IUserStore users,
            ManagedApiKeyStore store, ChangeHub changes, AuditLogService audit) =>
        {
            var me = WebIdentity.UserId(ctx)!;
            var name = (req.Name ?? "").Trim();
            if (name.Length == 0 || name.Length > 60)
                return Results.BadRequest(new AguiError(ErrorCodes.BadRequest, "名称必填且 ≤60 字符"));
            var username = (req.Username ?? "").Trim();
            if (username.Length == 0)
                return Results.BadRequest(new AguiError(ErrorCodes.BadRequest, "必须指定绑定账号（username）"));
            var bound = users.GetUserByUsername(username);
            if (bound is null)
                return Results.BadRequest(new AguiError(ErrorCodes.BadRequest, $"账号「{username}」不存在"));

            var plaintext = ManagedApiKeyStore.Generate();
            var key = new ManagedApiKey
            {
                Id = "ak_" + IdGenerator.NewId(),
                Name = name,
                Username = bound.Username,
                KeyPrefix = ManagedApiKeyStore.PrefixOf(plaintext),
                KeyHash = ManagedApiKeyStore.Hash(plaintext),
                CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                CreatedBy = me,
            };
            store.Add(key);
            changes.Notify();

            audit.Record("apikey.create", me, auth.GetUser(me)?.Username, targetType: "apikey", targetId: key.Id,
                detail: $"创建 API 密钥「{key.Name}」绑定 {bound.Username}");
            // 明文密钥仅此一次返回
            return Results.Ok(new
            {
                key.Id, key.Name, key.Username, key.KeyPrefix, key.CreatedAt, key.CreatedBy,
                secret = plaintext,
            });
        }).AddEndpointFilter(new WebIdentity.RequireAdminFilter());

        // ---- 吊销（删除）----
        root.MapDelete("/{id}", (string id, HttpContext ctx, AuthService auth,
            ManagedApiKeyStore store, ChangeHub changes, AuditLogService audit) =>
        {
            var me = WebIdentity.UserId(ctx)!;
            if (!store.Remove(id)) return Results.NotFound(new AguiError(ErrorCodes.BadRequest, "密钥不存在"));
            changes.Notify();
            audit.Record("apikey.revoke", me, auth.GetUser(me)?.Username, targetType: "apikey", targetId: id,
                detail: "吊销 API 密钥");
            return Results.Ok(new { ok = true });
        }).AddEndpointFilter(new WebIdentity.RequireAdminFilter());
    }

    /// <summary>注册托管密钥到持久化扩展区「apiKeys」：重启后恢复（只含哈希，无明文）。</summary>
    public static void RegisterApiKeyPersistence(this IServiceProvider services)
    {
        var store = services.GetRequiredService<ManagedApiKeyStore>();
        Func<object?> snapshot = () => store.List().Select(k => (object)k).ToList();
        Action<JsonElement> restore = element =>
        {
            var saved = element.Deserialize<List<ManagedApiKey>>(AguiJson.Options) ?? [];
            store.ReplaceAll(saved);
        };

        var persistence = services.GetService<PersistenceService>();
        if (persistence is not null)
            persistence.AddSection("apiKeys", snapshot, restore);
        else
            services.GetService<ISectionStore>()?.AddSection("apiKeys", snapshot, restore);
    }
}

/// <summary>创建托管 API 密钥请求体。</summary>
public sealed record ApiKeyHttpRequest(string? Name, string? Username);
