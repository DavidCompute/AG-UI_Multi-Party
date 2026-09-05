using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using AguiGroupChat.Hub.Infra;
using AguiGroupChat.Hub.Messaging;
using AguiGroupChat.Hub.Models;
using AguiGroupChat.Hub.Options;
using AguiGroupChat.Hub.Persistence;

namespace AguiGroupChat.Hub.Users;

/// <summary>
/// 用户管理与认证服务：注册 / 登录 / 会话令牌（滑动过期）/ 登出 / 修改密码 / 资料维护。
/// 会话令牌存于进程内，可经快照持久化（重启后保持登录态）；可替换为 Redis / JWT 等方案。
/// </summary>
public sealed class AuthService
{
    private readonly IUserStore _store;
    private readonly AuthOptions _options;
    private readonly TimeProvider _time;
    private readonly ChangeHub? _changes;
    private readonly ILogger<AuthService> _logger;
    private readonly ISessionStore _sessions;
    // 服务端主动终止实时连接（可选注入；为空时跳过——既有测试构造不依赖它）
    private readonly ConnectionManager? _connections;

    // 登录失败限速：按「IP + 用户名」组合键计数，窗口内失败次数超限后临时拒绝（防暴力破解）。
    // 组合键防「同一 IP 刷不同用户名绕过单用户名限速」的 DoS；纯 username 维度会被分布式小号批量绕开。
    private const int LoginFailWindowMs = 5 * 60 * 1000;
    private const int LoginFailMaxAttempts = 10;
    private readonly ConcurrentDictionary<string, (int Count, long FirstFailMs)> _loginFailures = new(StringComparer.Ordinal);

    // 登录时序拉平：用户不存在时也执行一次 PBKDF2（固定哑盐 / 哑哈希），避免响应耗时差异造成用户名枚举侧信道
    private static readonly string DummySalt = Convert.ToBase64String([0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0A, 0x0B, 0x0C, 0x0D, 0x0E, 0x0F, 0x10]);
    private static readonly string DummyHash = Convert.ToBase64String([0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17, 0x18, 0x19, 0x1A, 0x1B, 0x1C, 0x1D, 0x1E, 0x1F, 0x20, 0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28, 0x29, 0x2A, 0x2B, 0x2C, 0x2D, 0x2E, 0x2F, 0x30]);

    public AuthService(IUserStore store, AuthOptions options, TimeProvider time, ILogger<AuthService> logger, ChangeHub? changes = null, ISessionStore? sessions = null, ConnectionManager? connections = null)
    {
        _store = store;
        _options = options;
        _time = time;
        _logger = logger;
        _changes = changes;
        _sessions = sessions ?? new InMemorySessionStore();
        _connections = connections;
    }

    /// <summary>
    /// 注册新账号并自动签发会话。可选 <paramref name="memberId"/> 仅供内部（示例数据）固定身份使用。
    /// </summary>
    public UserAccount Register(string username, string password, string? nickname, string? avatar, string? memberId = null)
    {
        username = (username ?? "").Trim();
        if (username.Length < 3)
            throw new AguiProtocolException(ErrorCodes.BadRequest, "用户名至少 3 个字符");
        if (username.Length > 50)
            throw new AguiProtocolException(ErrorCodes.BadRequest, "用户名最多 50 个字符");
        if (string.IsNullOrEmpty(password) || password.Length < 6)
            throw new AguiProtocolException(ErrorCodes.BadRequest, "密码至少 6 位");
        if (_store.GetUserByUsername(username) is not null)
            throw new AguiProtocolException(ErrorCodes.UserExists, $"用户名「{username}」已被注册");

        var now = _time.GetUtcNow().ToUnixTimeMilliseconds();
        var (salt, hash) = PasswordHasher.Hash(password);
        var firstUser = _options.FirstUserIsAdmin && _store.ListUsers().Count == 0; // 首个注册账号
        var isAdmin = IsConfiguredAdmin(username, memberId ?? "")
            || firstUser; // 首个注册账号自动成为管理员
        // 首次注册的账号默认授予超级管理员（全平台最高角色），否则新部署没人能管理平台角色
        var platformRole = firstUser ? PlatformRole.SuperAdmin : PlatformRole.User;
        var user = new UserAccount
        {
            UserId = memberId ?? "user_" + IdGenerator.NewId(),
            Username = username,
            PasswordSalt = salt,
            PasswordHash = hash,
            Nickname = string.IsNullOrWhiteSpace(nickname) ? username : nickname.Trim(),
            Avatar = avatar,
            CreatedAt = now,
            UpdatedAt = now,
            IsAdmin = isAdmin,
            // 首个账号（默认自举）为超级管理员；其余注册账号显式平台角色为 User（IsAdmin/配置由 ResolveRole 推导）
            PlatformRole = platformRole,
        };
        if (!_store.AddUser(user))
            throw new AguiProtocolException(ErrorCodes.UserExists, $"用户名「{username}」已被注册");

        _logger.LogInformation("新用户注册 {UserId}（{Username}）", user.UserId, user.Username);
        return user;
    }

    /// <summary>用户名 + 密码登录，成功签发会话令牌。先验密、后计次：密码正确不会被锁定拦截；
    /// 密码错误（或用户不存在）窗口内连续失败超限时拒绝（防暴力破解）。
    /// <paramref name="clientKey"/> 为客户端标识（通常为来源 IP）：失败锁定键 = clientKey + "|" + username，
    /// 防止同一 IP 刷不同用户名绕过限速；缺省（null）时回退纯 username，兼容既有调用与测试。</summary>
    public LoginResult Login(string username, string password, string? clientKey = null)
    {
        var name = (username ?? "").Trim();
        // 失败锁定键：IP + 用户名（IP 缺失时退化为纯用户名，至少保留单用户名维度限速）
        var key = string.IsNullOrWhiteSpace(clientKey) ? name : clientKey.Trim() + "|" + name;
        var nowMs = _time.GetUtcNow().ToUnixTimeMilliseconds();

        var user = _store.GetUserByUsername(name);
        if (user is null)
            PasswordHasher.Verify(password ?? "", DummySalt, DummyHash); // 拉平时序：用户不存在也执行一次 PBKDF2，防用户名枚举

        // 先验密、后计次：密码正确 → 清除失败计数并正常签发（不再被锁定拦截）
        if (user is null || !PasswordHasher.Verify(password ?? "", user.PasswordSalt, user.PasswordHash))
        {
            RecordLoginFailure(key, nowMs);
            if (_loginFailures.TryGetValue(key, out var fail)
                && nowMs - fail.FirstFailMs < LoginFailWindowMs
                && fail.Count >= LoginFailMaxAttempts)
                throw new AguiProtocolException(ErrorCodes.UserBadCredentials, "尝试次数过多，请稍后再试");
            throw new AguiProtocolException(ErrorCodes.UserBadCredentials, "用户名或密码错误");
        }

        _loginFailures.TryRemove(key, out _); // 登录成功清零失败计数
        if (user.IsDisabled)
            throw new AguiProtocolException(ErrorCodes.UserBadCredentials, "账号已被禁用，请联系管理员");
        var token = IssueSession(user.UserId, out var expiresAt);
        return new LoginResult(user, token, expiresAt);
    }

    /// <summary>记录一次登录失败（窗口内计数）。总量超 1 万时先清理窗口过期项，仍超限则淘汰最旧一项，防字典攻击撑爆字典。</summary>
    private void RecordLoginFailure(string key, long nowMs)
    {
        if (_loginFailures.Count >= 10000)
        {
            foreach (var kv in _loginFailures)
            {
                if (nowMs - kv.Value.FirstFailMs >= LoginFailWindowMs)
                    _loginFailures.TryRemove(kv.Key, out _);
            }
            if (_loginFailures.Count >= 10000)
            {
                var oldest = _loginFailures.OrderBy(kv => kv.Value.FirstFailMs).FirstOrDefault();
                if (oldest.Key is not null)
                    _loginFailures.TryRemove(oldest.Key, out _); // 淘汰最旧的一项（FirstFailMs 最小）
            }
        }
        _loginFailures.AddOrUpdate(key,
            _ => (1, nowMs),
            (_, old) => nowMs - old.FirstFailMs < LoginFailWindowMs ? (old.Count + 1, old.FirstFailMs) : (1, nowMs));
    }

    /// <summary>
    /// 对外 API 密钥（6.4）鉴权：命中 <see cref="AuthOptions.ApiKeys"/> 中某条且其用户名存在时返回该用户。
    /// 用恒定时间比较避免时序侧信道；未配置密钥或未命中返回 null。
    /// </summary>
    public UserAccount? ResolveApiKey(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        foreach (var entry in _options.ApiKeys)
        {
            if (string.IsNullOrWhiteSpace(entry.ApiKey) || string.IsNullOrWhiteSpace(entry.Username)) continue;
            if (FixedTimeEquals(entry.ApiKey, token.Trim()))
                return _store.GetUserByUsername(entry.Username);
        }
        return null;
    }

    private static bool FixedTimeEquals(string a, string b)
    {
        var ba = System.Text.Encoding.UTF8.GetBytes(a);
        var bb = System.Text.Encoding.UTF8.GetBytes(b);
        if (ba.Length != bb.Length) return false;
        var d = 0;
        for (var i = 0; i < ba.Length; i++) d |= ba[i] ^ bb[i];
        return d == 0;
    }

    /// <summary>校验令牌并滑动续期；无效 / 已过期 / 超过绝对有效期返回 null。</summary>
    public UserAccount? ValidateToken(string? token)
    {
        if (string.IsNullOrEmpty(token)) return null;
        var key = HashToken(token);
        var session = _sessions.TryGet(key);
        if (session is null) return null;

        var now = _time.GetUtcNow();
        var absoluteTtl = TimeSpan.FromDays(Math.Max(1, _options.AbsoluteSessionTtlDays));
        if (now > session.ExpiresAt || now > session.IssuedAt + absoluteTtl)
        {
            _sessions.Remove(key);
            return null;
        }
        session.ExpiresAt = now + session.Ttl; // 滑动续期
        _sessions.Upsert(session, session.Ttl); // 续期后写回（Redis 模式更新 TTL / 过期时间，供多副本共享）
        _changes?.Notify(); // 续期后通知持久化（memory 快照模式下重启后令牌过期时间保持最新）
        var user = _store.GetUserById(session.UserId);
        // 被管理员禁用后：已有会话令牌立即失效（账号删除 / 停用场景要求登出在线会话）
        if (user is null || user.IsDisabled)
        {
            _sessions.Remove(key);
            _changes?.Notify();
            return null;
        }
        return user;
    }

    /// <summary>退出登录：吊销指定令牌，并终止该账号已建立的实时连接（在线即登出）。</summary>
    public void Logout(string? token)
    {
        if (string.IsNullOrEmpty(token)) return;
        var key = HashToken(token);
        var session = _sessions.TryGet(key);
        if (_sessions.Remove(key))
        {
            _changes?.Notify();
            if (session is not null) _connections?.AbortConnectionsOf(session.UserId);
        }
    }

    /// <summary>修改密码：校验旧密码后更新，并吊销该用户全部旧会话（需重新登录）。</summary>
    public void ChangePassword(string userId, string oldPassword, string newPassword)
    {
        var user = _store.GetUserById(userId)
            ?? throw new AguiProtocolException(ErrorCodes.UserNotFound, "用户不存在");
        if (!PasswordHasher.Verify(oldPassword ?? "", user.PasswordSalt, user.PasswordHash))
            throw new AguiProtocolException(ErrorCodes.UserPasswordInvalid, "旧密码不正确");
        if (string.IsNullOrEmpty(newPassword) || newPassword.Length < 6)
            throw new AguiProtocolException(ErrorCodes.BadRequest, "新密码至少 6 位");

        (user.PasswordSalt, user.PasswordHash) = PasswordHasher.Hash(newPassword);
        user.UpdatedAt = _time.GetUtcNow().ToUnixTimeMilliseconds();
        _store.UpdateUser(user);

        var invalidated = 0;
        foreach (var s in _sessions.All().Where(s => s.UserId == userId))
        {
            if (_sessions.Remove(s.TokenHash)) invalidated++;
        }
        if (invalidated > 0)
        {
            _logger.LogInformation("用户 {UserId} 修改密码，已吊销 {Count} 个会话", userId, invalidated);
            _changes?.Notify();
        }
        // 改密后立即使该账号已建实时连接掉线（其会话已全部吊销）
        if (invalidated > 0) _connections?.AbortConnectionsOf(userId);
    }

    /// <summary>管理员禁用 / 启用账号：禁用时立即吊销该用户全部会话（在线即登出）。</summary>
    public void SetUserDisabled(string userId, bool disabled)
    {
        var user = _store.GetUserById(userId)
            ?? throw new AguiProtocolException(ErrorCodes.UserNotFound, "用户不存在");
        if (user.IsDisabled == disabled) return;
        user.IsDisabled = disabled;
        user.UpdatedAt = _time.GetUtcNow().ToUnixTimeMilliseconds();
        _store.UpdateUser(user);
        if (disabled) RevokeUserSessions(userId);
        _logger.LogInformation("管理员{Action}用户 {UserId}（{Username}）", disabled ? "禁用" : "启用", userId, user.Username);
    }

    /// <summary>管理员重置密码：校验新密码强度并吊销该用户全部会话（需重新登录）。</summary>
    public void AdminResetPassword(string userId, string newPassword)
    {
        var user = _store.GetUserById(userId)
            ?? throw new AguiProtocolException(ErrorCodes.UserNotFound, "用户不存在");
        if (string.IsNullOrEmpty(newPassword) || newPassword.Length < 6)
            throw new AguiProtocolException(ErrorCodes.BadRequest, "新密码至少 6 位");
        if (newPassword.Length > 128)
            throw new AguiProtocolException(ErrorCodes.BadRequest, "新密码最长 128 位");

        (user.PasswordSalt, user.PasswordHash) = PasswordHasher.Hash(newPassword);
        user.UpdatedAt = _time.GetUtcNow().ToUnixTimeMilliseconds();
        _store.UpdateUser(user);
        RevokeUserSessions(userId);
        _logger.LogInformation("管理员重置用户 {UserId}（{Username}）的密码", userId, user.Username);
    }

    /// <summary>吊销某用户的全部会话令牌（禁用账号 / 重置密码 / 修改密码后调用）。</summary>
    private void RevokeUserSessions(string userId)
    {
        var invalidated = 0;
        foreach (var s in _sessions.All().Where(s => s.UserId == userId))
        {
            if (_sessions.Remove(s.TokenHash)) invalidated++;
        }
        if (invalidated > 0)
        {
            _logger.LogInformation("用户 {UserId} 已吊销 {Count} 个会话", userId, invalidated);
            _changes?.Notify();
        }
        // 会话吊销（禁用 / 重置 / 改密沿用）后，该账号已建实时连接一并终止
        if (invalidated > 0) _connections?.AbortConnectionsOf(userId);
    }

    /// <summary>更新资料（昵称 / 头像 / 个人记忆开关），返回更新后的账号。昵称 / 头像带长度上限防存储 DoS。</summary>
    public UserAccount UpdateProfile(string userId, string? nickname, string? avatar, bool? personalMemoryEnabled = null)
    {
        var user = _store.GetUserById(userId)
            ?? throw new AguiProtocolException(ErrorCodes.UserNotFound, "用户不存在");
        if (!string.IsNullOrWhiteSpace(nickname))
        {
            var nick = nickname.Trim();
            if (nick.Length > 50)
                throw new AguiProtocolException(ErrorCodes.BadRequest, "昵称最多 50 个字符");
            user.Nickname = nick;
        }
        if (avatar is not null)
        {
            if (avatar.Length > 2048)
                throw new AguiProtocolException(ErrorCodes.BadRequest, "头像地址最长 2048 个字符");
            user.Avatar = avatar;
        }
        if (personalMemoryEnabled is not null) user.PersonalMemoryEnabled = personalMemoryEnabled.Value;
        user.UpdatedAt = _time.GetUtcNow().ToUnixTimeMilliseconds();
        _store.UpdateUser(user);
        return user;
    }

    public UserAccount? GetUser(string userId) => _store.GetUserById(userId);

    /// <summary>是否系统管理员：账号 IsAdmin 标记（首个用户 / 显式指定）或配置名单（userId / username）命中。</summary>
    public bool IsAdmin(string? userId)
    {
        if (string.IsNullOrEmpty(userId)) return false;
        var user = _store.GetUserById(userId);
        if (user is not null && user.IsAdmin) return true;
        return IsConfiguredAdmin(user?.Username ?? "", userId);
    }

    /// <summary>
    /// 解析用户<b>生效</b>平台角色（RBAC 分层）：取「显式 <see cref="UserAccount.PlatformRole"/>」与
    /// 「IsAdmin 标记 / Auth:AdminUserIds 配置 → 至少 Admin」两者的较高者。未登录 / 不存在返回 <see cref="PlatformRole.User"/>。
    /// </summary>
    public PlatformRole ResolveRole(string? userId)
    {
        if (string.IsNullOrEmpty(userId)) return PlatformRole.User;
        var user = _store.GetUserById(userId);
        var explicitRole = user?.PlatformRole ?? PlatformRole.User;
        // 既有的 IsAdmin 标记 / 配置名单仍视为至少 Admin（向后兼容）；SuperAdmin 由显式角色或配置名单授予
        var adminDerived = (user is not null && user.IsAdmin)
                           || IsConfiguredAdmin(user?.Username ?? "", userId);
        var superDerived = IsConfiguredSuperAdmin(user?.Username ?? "", userId);
        var derived = superDerived ? PlatformRole.SuperAdmin
                    : adminDerived ? PlatformRole.Admin
                    : PlatformRole.User;
        return (PlatformRole)Math.Max((int)explicitRole, (int)derived);
    }

    /// <summary>配置名单（Auth:SuperAdminUserIds，逗号分隔的 userId / username）是否命中。</summary>
    private bool IsConfiguredSuperAdmin(string username, string userId)
        => IsConfiguredInList(_options.SuperAdminUserIds, username, userId);

    /// <summary>解析逗号分隔名单（userId 精确匹配 / username 大小写不敏感匹配）。</summary>
    private static bool IsConfiguredInList(string list, string username, string userId)
    {
        if (string.IsNullOrWhiteSpace(list)) return false;
        foreach (var item in list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (item.Equals(userId, StringComparison.Ordinal)
                || (!string.IsNullOrEmpty(username) && item.Equals(username, StringComparison.OrdinalIgnoreCase)))
                return true;
        }
        return false;
    }

    /// <summary>用户生效角色是否至少达到 <paramref name="min"/>（RBAC 分层判定）。</summary>
    public bool HasRole(string? userId, PlatformRole min) => ResolveRole(userId) >= min;

    /// <summary>是否超级管理员（可管理平台角色 / 管理员名单）。</summary>
    public bool IsSuperAdmin(string? userId) => ResolveRole(userId) >= PlatformRole.SuperAdmin;

    /// <summary>
    /// 设置某账号的<b>显式</b>平台角色（RBAC 分层，仅超级管理员调用）。
    /// 防呆：禁止把账号平台角色降到低于其 IsAdmin 标记实际承担的管理要求（ResolveRole 仍会推导为至少 Admin）；
    /// 禁止降级最后一名超级管理员（避免平台失去管理入口）。
    /// </summary>
    public UserAccount SetPlatformRole(string targetUserId, PlatformRole role)
    {
        var target = _store.GetUserById(targetUserId)
            ?? throw new AguiProtocolException(ErrorCodes.UserNotFound, "用户不存在");
        if (role is < PlatformRole.User or > PlatformRole.SuperAdmin)
            throw new AguiProtocolException(ErrorCodes.BadRequest, "平台角色取值非法");
        // 防呆：最后一名超级管理员不可被降级（否则平台无最高权限管理入口）
        if (ResolveRole(targetUserId) >= PlatformRole.SuperAdmin
            && role < PlatformRole.SuperAdmin
            && IsLastSuperAdmin(targetUserId))
            throw new AguiProtocolException(ErrorCodes.GroupPermissionDenied, "不能降级最后一名超级管理员");

        target.PlatformRole = role;
        // 显式角色 >= Admin 时同步 IsAdmin 标记（保持整个系统的 IsAdmin 语义一致）；
        // 降级到 User/Operator 时清除 IsAdmin 标记，除非命中 Auth:AdminUserIds 配置（配置仍由 ResolveRole 推导为 Admin，不硬改配置）。
        target.IsAdmin = role >= PlatformRole.Admin || IsConfiguredAdmin(target.Username, target.UserId);
        target.UpdatedAt = _time.GetUtcNow().ToUnixTimeMilliseconds();
        _store.UpdateUser(target);
        _logger.LogInformation("平台角色变更：{Target} → {Role}", targetUserId, role);
        return target;
    }

    private bool IsLastSuperAdmin(string excludeUserId)
        => _store.ListUsers().Where(u => u.UserId != excludeUserId).All(u => ResolveRole(u.UserId) < PlatformRole.SuperAdmin)
           && ResolveRole(excludeUserId) >= PlatformRole.SuperAdmin;

    /// <summary>配置名单（Auth:AdminUserIds，逗号分隔的 userId / username）是否命中。大小写不敏感匹配用户名。</summary>
    private bool IsConfiguredAdmin(string username, string userId)
        => IsConfiguredInList(_options.AdminUserIds, username, userId);

    public IReadOnlyList<UserAccount> ListUsers() => _store.ListUsers();

    /// <summary>导出全部会话（供持久化快照；Token 为 SHA-256 哈希，明文令牌不落盘）。</summary>
    public IReadOnlyList<PersistedSession> SnapshotSessions()
        => _sessions.All().Select(s => new PersistedSession
        {
            Token = s.TokenHash,
            UserId = s.UserId,
            ExpiresAt = s.ExpiresAt.ToUnixTimeMilliseconds(),
            IssuedAt = s.IssuedAt.ToUnixTimeMilliseconds(),
            SessionId = string.IsNullOrEmpty(s.SessionId) ? null : s.SessionId,
        }).ToList();

    /// <summary>恢复会话（跳过已过期 / 超过绝对有效期 / 用户已不存在的令牌）。</summary>
    public void RestoreSessions(IEnumerable<PersistedSession> sessions)
    {
        foreach (var s in sessions)
        {
            if (_sessions.TryGet(s.Token) is not null) continue;
            if (_store.GetUserById(s.UserId) is null) continue;
            var expiresAt = DateTimeOffset.FromUnixTimeMilliseconds(s.ExpiresAt);
            if (_time.GetUtcNow() > expiresAt) continue;

            var ttl = expiresAt - _time.GetUtcNow();
            // 旧快照无 IssuedAt：按「过期时间 - 滑动 TTL」推算签发时间（绝对有效期判定仍生效）
            var issuedAt = s.IssuedAt is { } issued
                ? DateTimeOffset.FromUnixTimeMilliseconds(issued)
                : expiresAt - ttl;
            var absoluteTtl = TimeSpan.FromDays(Math.Max(1, _options.AbsoluteSessionTtlDays));
            if (_time.GetUtcNow() > issuedAt + absoluteTtl) continue;

            var session = new UserSession
            {
                TokenHash = s.Token,
                UserId = s.UserId,
                Ttl = ttl,
                ExpiresAt = expiresAt,
                IssuedAt = issuedAt,
                SessionId = s.SessionId ?? "ses_" + IdGenerator.NewId(),
            };
            _sessions.Upsert(session, ttl);
        }
    }

    /// <summary>列出某用户的全部活跃会话（供多设备会话管理）。返回去掉令牌的元信息。</summary>
    public IReadOnlyList<AuthSessionInfo> GetUserSessions(string userId)
        => _sessions.All().Where(s => s.UserId == userId)
            .Select(s => new AuthSessionInfo(
                SessionId: string.IsNullOrEmpty(s.SessionId) ? "ses_" + s.TokenHash[..8] : s.SessionId,
                IssuedAt: s.IssuedAt.ToUnixTimeMilliseconds(),
                ExpiresAt: s.ExpiresAt.ToUnixTimeMilliseconds()))
            .OrderByDescending(s => s.IssuedAt)
            .ToList();

    /// <summary>吊销某用户的一个指定会话（sessionId）；返回是否找到并吊销。</summary>
    public bool RevokeSession(string userId, string sessionId)
    {
        var removed = false;
        foreach (var s in _sessions.All().Where(s => s.UserId == userId && s.SessionId == sessionId).ToList())
        {
            if (_sessions.Remove(s.TokenHash)) removed = true;
        }
        if (removed) _changes?.Notify();
        return removed;
    }

    /// <summary>吊销某用户除当前会话外的全部会话（sessionId 保留）。返回吊销条数。</summary>
    public int RevokeOtherSessions(string userId, string currentSessionId)
    {
        var removed = 0;
        foreach (var s in _sessions.All().Where(s => s.UserId == userId && s.SessionId != currentSessionId).ToList())
        {
            if (_sessions.Remove(s.TokenHash)) removed++;
        }
        if (removed > 0) _changes?.Notify();
        return removed;
    }

    /// <summary>返回当前令牌对应会话的 SessionId（供「吊销其他会话」识别当前会话），令牌无效返回 null。</summary>
    public string? GetSessionIdOfToken(string? token)
        => string.IsNullOrEmpty(token) || _sessions.TryGet(HashToken(token)) is not { } s ? null
            : (string.IsNullOrEmpty(s.SessionId) ? "ses_" + HashToken(token)[..8] : s.SessionId);

    private string IssueSession(string userId, out long expiresAt)
    {
        // 顺手清理过期会话，避免无限增长
        var now = _time.GetUtcNow();
        foreach (var s in _sessions.All().Where(s => now > s.ExpiresAt))
            _sessions.Remove(s.TokenHash);

        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var ttl = TimeSpan.FromHours(Math.Max(1, _options.SessionTtlHours));
        var session = new UserSession
        {
            TokenHash = HashToken(token),
            UserId = userId,
            Ttl = ttl,
            ExpiresAt = now + ttl,
            IssuedAt = now,
            SessionId = "ses_" + IdGenerator.NewId(),
        }; // 字典键存哈希，返回给客户端的仍是明文令牌
        _sessions.Upsert(session, ttl);
        expiresAt = (now + ttl).ToUnixTimeMilliseconds();
        _changes?.Notify();
        return token;
    }

    /// <summary>吊销全部会话（系统初始化用：清空数据后所有已登录端立即失效）。</summary>
    public void ClearSessions()
    {
        _sessions.Clear();
        _changes?.Notify();
    }

    /// <summary>会话令牌哈希（SHA-256 → base64url）：快照落盘只存哈希，明文令牌不落盘。</summary>
    private static string HashToken(string token)
        => Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(token)))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>会话元信息（多设备会话管理，4.4）：不含令牌本身。</summary>
public sealed record AuthSessionInfo(string SessionId, long IssuedAt, long ExpiresAt);

/// <summary>登录结果：账号 + 会话令牌 + 过期时间戳。</summary>
public sealed record LoginResult(UserAccount User, string Token, long ExpiresAt);
