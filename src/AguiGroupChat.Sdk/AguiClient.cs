using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AguiGroupChat.Sdk.Models;

namespace AguiGroupChat.Sdk;

/// <summary>
/// AG-UI Hub HTTP 上行 API 客户端：封装鉴权卸载、群组 / 成员 / 话题 / 消息 / 智能体 / 附件管理。
/// 所有写接口按 Hub 鉴权规则携带 <c>Authorization: Bearer &lt;token&gt;</c>。
/// 令牌来源优先级：<see cref="Token"/> 显式赋值 → <see cref="AguiClientOptions.TokenProvider"/>。
/// </summary>
public sealed class AguiClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly AguiClientOptions _options;
    private bool _disposed;

    public AguiClient(AguiClientOptions options)
        : this(options, new HttpClient()) { }

    public AguiClient(AguiClientOptions options, HttpClient httpClient)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        if (options.BaseUri is null)
            throw new ArgumentException("BaseUri 不能为空", nameof(options));
        _http = httpClient;
        _http.BaseAddress = options.BaseUri;
        if (options.Timeout.HasValue)
            _http.Timeout = options.Timeout.Value;
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    /// <summary>
    /// 当前会话令牌。登录 / 注册接口成功后 SDK 会自动写入；
    /// 第三方应用也可在外部登录后直接赋值。HTTP 与实时通道共用。
    /// </summary>
    public string? Token { get; set; }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _http.Dispose();
    }

    #region 内部 HTTP 工具

    private HttpRequestMessage Build(HttpMethod method, string path, object? body = null)
    {
        var req = new HttpRequestMessage(method, path);
        var token = Token ?? _options.TokenProvider?.Invoke();
        if (!string.IsNullOrEmpty(token))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null)
            req.Content = JsonContent.Create(body, options: AguiJson.Options);
        return req;
    }

    /// <summary>发送请求并反序列化为 T；非成功状态码抛 <see cref="AguiException"/>。</summary>
    private async Task<T?> SendAsync<T>(HttpRequestMessage req, CancellationToken ct)
    {
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw ParseError(resp, body);
        if (body.Length == 0 || typeof(T) == typeof(string))
            return body is null ? default : (T)(object)body;
        try
        {
            return JsonSerializer.Deserialize<T>(body, AguiJson.Options);
        }
        catch (JsonException ex)
        {
            // 响应体与预期模型不匹配时抛出明确错误，避免上层拿到 null 后困惑。
            throw new AguiException(ErrorCodes.BadRequest,
                $"响应反序列化失败（请核对 SDK 与 Hub 版本 / 枚举字符串化配置）：{ex.Message}",
                (int)resp.StatusCode, body);
        }
    }

    /// <summary>发送请求但忽略响应体（用于仅返回 { ok = true } 之类的接口）。</summary>
    private async Task SendNoContentAsync(HttpRequestMessage req, CancellationToken ct)
    {
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw ParseError(resp, body);
    }

    private static AguiException ParseError(HttpResponseMessage resp, string body)
    {
        string code = ErrorCodes.BadRequest, message = body;
        if (!string.IsNullOrWhiteSpace(body))
        {
            try
            {
                var err = JsonSerializer.Deserialize<AguiError>(body, AguiJson.Options);
                if (err is not null && !string.IsNullOrEmpty(err.Code))
                {
                    code = err.Code;
                    message = err.Message ?? body;
                }
            }
            catch { /* 非 JSON 响应体，保留原文 */ }
        }
        return new AguiException(code, message, (int)resp.StatusCode, body);
    }

    #endregion

    #region 身份认证

    /// <summary>注册用户（注册即登录，返回令牌）。</summary>
    public async Task<AuthResponse> RegisterAsync(string username, string password, string? nickname = null, string? avatar = null, CancellationToken ct = default)
    {
        var result = await RequestAuthAsync("/ag-ui/user/register", new { username, password, nickname, avatar }, ct).ConfigureAwait(false);
        ApplyToken(result.Token);
        return result;
    }

    /// <summary>用户名 + 密码登录（可选 TOTP 动态码）。</summary>
    public async Task<AuthResponse> LoginAsync(string username, string password, string? totpCode = null, CancellationToken ct = default)
    {
        var body = string.IsNullOrEmpty(totpCode)
            ? (object)new { username, password }
            : new { username, password, totpCode };
        var result = await RequestAuthAsync("/ag-ui/user/login", body, ct).ConfigureAwait(false);
        ApplyToken(result.Token);
        return result;
    }

    /// <summary>登出（吊销当前令牌）。</summary>
    public Task LogoutAsync(CancellationToken ct = default)
    {
        var token = Token;
        return SendNoContentAsync(Build(HttpMethod.Post, "/ag-ui/user/logout", new { }), ct);
    }

    /// <summary>获取当前用户资料。</summary>
    public Task<UserProfile?> GetCurrentUserAsync(CancellationToken ct = default)
        => SendAsync<UserProfile>(Build(HttpMethod.Get, "/ag-ui/user/me"), ct);

    /// <summary>修改密码（成功后吊销该用户全部旧会话）。</summary>
    public Task ChangePasswordAsync(string oldPassword, string newPassword, CancellationToken ct = default)
        => SendNoContentAsync(Build(HttpMethod.Post, "/ag-ui/user/password", new { oldPassword, newPassword }), ct);

    /// <summary>修改资料（昵称 / 头像 / 个人记忆开关）。变更同步到各群成员资料。</summary>
    public Task<UserProfile?> UpdateProfileAsync(string? nickname = null, string? avatar = null, bool? personalMemoryEnabled = null, CancellationToken ct = default)
        => SendAsync<UserProfile>(Build(HttpMethod.Put, "/ag-ui/user/profile", new { nickname, avatar, personalMemoryEnabled }), ct);

    /// <summary>用户目录（登录后可见，用于建群成员选择）。</summary>
    public Task<IReadOnlyList<UserDirectoryEntry>?> ListUsersAsync(CancellationToken ct = default)
        => SendAsync<IReadOnlyList<UserDirectoryEntry>>(Build(HttpMethod.Get, "/ag-ui/users"), ct);

    /// <summary>TOTP 启用状态查询。</summary>
    public Task<TotpStatus?> GetTotpStatusAsync(CancellationToken ct = default)
        => SendAsync<TotpStatus>(Build(HttpMethod.Get, "/ag-ui/user/totp"), ct);

    private async Task<AuthResponse> RequestAuthAsync(string path, object body, CancellationToken ct)
    {
        using var resp = await _http.PostAsJsonAsync(path, body, AguiJson.Options, ct).ConfigureAwait(false);
        var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw ParseError(resp, text);
        return JsonSerializer.Deserialize<AuthResponse>(text, AguiJson.Options) ?? new AuthResponse();
    }

    private void ApplyToken(string? token)
    {
        if (!string.IsNullOrEmpty(token))
            Token = token;
    }

    /// <summary>TOTP 状态（GET /ag-ui/user/totp 响应）。</summary>
    public sealed class TotpStatus
    {
        public bool Enabled { get; set; }
    }

    #endregion

    #region 群组管理

    /// <summary>创建群组；groupName 留空可先调群名自动生成。</summary>
    public Task<Group?> CreateGroupAsync(GroupCreateRequest request, CancellationToken ct = default)
        => SendAsync<Group>(Build(HttpMethod.Post, "/ag-ui/group/create", request), ct);

    /// <summary>更新群信息。</summary>
    public Task<Group?> UpdateGroupAsync(GroupUpdateRequest request, CancellationToken ct = default)
        => SendAsync<Group>(Build(HttpMethod.Post, "/ag-ui/group/update", request), ct);

    /// <summary>解散群组。</summary>
    public Task DisbandGroupAsync(string groupId, string operatorId, CancellationToken ct = default)
        => SendNoContentAsync(Build(HttpMethod.Post, "/ag-ui/group/disband", new { groupId, operatorId }), ct);

    /// <summary>群详情快照（返回 GROUP_STATE_SNAPSHOT 结构）。</summary>
    public Task<GroupStateSnapshotEvent?> GetGroupSnapshotAsync(string groupId, CancellationToken ct = default)
        => SendAsync<GroupStateSnapshotEvent>(Build(HttpMethod.Get, $"/ag-ui/group/{groupId}"), ct);

    /// <summary>成员列表。</summary>
    public Task<IReadOnlyList<GroupMember>?> GetGroupMembersAsync(string groupId, CancellationToken ct = default)
        => SendAsync<IReadOnlyList<GroupMember>>(Build(HttpMethod.Get, $"/ag-ui/group/{groupId}/members"), ct);

    /// <summary>某成员自己加入的群列表（仅本人可查）。</summary>
    public Task<IReadOnlyList<MemberGroupDto>?> GetMyGroupsAsync(string memberId, CancellationToken ct = default)
        => SendAsync<IReadOnlyList<MemberGroupDto>>(Build(HttpMethod.Get, $"/ag-ui/member/{memberId}/groups"), ct);

    #endregion

    #region 群成员 / 话题

    /// <summary>添加成员。</summary>
    public Task<GroupMemberAddResult?> AddMembersAsync(GroupMemberAddRequest request, CancellationToken ct = default)
        => SendAsync<GroupMemberAddResult>(Build(HttpMethod.Post, "/ag-ui/group/member/add", request), ct);

    /// <summary>移除成员。</summary>
    public Task<GroupMemberRemoveResult?> RemoveMembersAsync(GroupMemberRemoveRequest request, CancellationToken ct = default)
        => SendAsync<GroupMemberRemoveResult>(Build(HttpMethod.Post, "/ag-ui/group/member/remove", request), ct);

    /// <summary>主动退群。</summary>
    public Task LeaveGroupAsync(string groupId, string memberId, CancellationToken ct = default)
        => SendNoContentAsync(Build(HttpMethod.Post, "/ag-ui/group/member/leave", new { groupId, memberId }), ct);

    /// <summary>更新成员。</summary>
    public Task<GroupMember?> UpdateMemberAsync(GroupMemberUpdateRequest request, CancellationToken ct = default)
        => SendAsync<GroupMember>(Build(HttpMethod.Post, "/ag-ui/group/member/update", request), ct);

    /// <summary>新建话题。</summary>
    public Task<GroupTopic?> CreateTopicAsync(GroupTopicCreateRequest request, CancellationToken ct = default)
        => SendAsync<GroupTopic>(Build(HttpMethod.Post, "/ag-ui/group/topic/create", request), ct);

    /// <summary>删除话题。</summary>
    public Task DeleteTopicAsync(string groupId, string topicId, string operatorId, CancellationToken ct = default)
        => SendNoContentAsync(Build(HttpMethod.Post, "/ag-ui/group/topic/delete", new { groupId, topicId, operatorId }), ct);

    /// <summary>清空话题聊天记录。</summary>
    public Task<ClearTopicResult?> ClearTopicAsync(string groupId, string topicId, string operatorId, CancellationToken ct = default)
        => SendAsync<ClearTopicResult>(Build(HttpMethod.Post, "/ag-ui/group/topic/clear", new { groupId, topicId, operatorId }), ct);

    /// <summary>话题列表。</summary>
    public Task<IReadOnlyList<GroupTopic>?> GetTopicsAsync(string groupId, CancellationToken ct = default)
        => SendAsync<IReadOnlyList<GroupTopic>>(Build(HttpMethod.Get, $"/ag-ui/group/{groupId}/topics"), ct);

    #endregion

    #region 消息

    /// <summary>发送消息（content 可空，纯附件消息）。</summary>
    public Task<GroupMessage?> SendMessageAsync(GroupMessageSendRequest request, CancellationToken ct = default)
        => SendAsync<GroupMessage>(Build(HttpMethod.Post, "/ag-ui/group/message/send", request), ct);

    /// <summary>撤回消息。</summary>
    public Task RecallMessageAsync(string groupId, string messageId, string operatorId, CancellationToken ct = default)
        => SendNoContentAsync(Build(HttpMethod.Post, "/ag-ui/group/message/recall", new { groupId, messageId, operatorId }), ct);

    /// <summary>重新回答最后一条智能体消息。</summary>
    public Task RegenerateMessageAsync(GroupMessageRegenerateRequest request, CancellationToken ct = default)
        => SendNoContentAsync(Build(HttpMethod.Post, "/ag-ui/group/message/regenerate", request), ct);

    /// <summary>停止智能体运行。</summary>
    public Task<StopRunResult?> StopAgentRunAsync(string groupId, string runId, string operatorId, CancellationToken ct = default)
        => SendAsync<StopRunResult>(Build(HttpMethod.Post, "/ag-ui/group/agent/stop", new { groupId, runId, operatorId }), ct);

    /// <summary>正在输入。</summary>
    public Task SendTypingAsync(string groupId, bool isTyping, string? memberId = null, CancellationToken ct = default)
        => SendNoContentAsync(Build(HttpMethod.Post, "/ag-ui/group/message/typing", new { groupId, memberId, isTyping }), ct);

    /// <summary>已读回执。</summary>
    public Task SendReadAsync(string groupId, string readMessageId, string? memberId = null, CancellationToken ct = default)
        => SendNoContentAsync(Build(HttpMethod.Post, "/ag-ui/group/message/read", new { groupId, memberId, readMessageId }), ct);

    /// <summary>群消息历史分页（before = 游标消息 ID，不含；count 默认 50，上限 100）。</summary>
    public Task<IReadOnlyList<SnapshotMessage>?> GetMessagesAsync(string groupId, string? before = null, int? count = null, string? topicId = null, CancellationToken ct = default)
    {
        var query = QueryString(
            ("before", before),
            ("count", count?.ToString()),
            ("topicId", topicId));
        return SendAsync<IReadOnlyList<SnapshotMessage>>(Build(HttpMethod.Get, $"/ag-ui/group/{groupId}/messages{query}"), ct);
    }

    /// <summary>话题消息历史分页。</summary>
    public Task<IReadOnlyList<SnapshotMessage>?> GetTopicMessagesAsync(string groupId, string topicId, string? before = null, int? count = null, CancellationToken ct = default)
    {
        var query = QueryString(
            ("before", before),
            ("count", count?.ToString()));
        return SendAsync<IReadOnlyList<SnapshotMessage>>(Build(HttpMethod.Get, $"/ag-ui/group/{groupId}/topics/{Uri.EscapeDataString(topicId)}/messages{query}"), ct);
    }

    /// <summary>群消息搜索（q 必填；topicId 可选；count 默认 20）。</summary>
    public Task<IReadOnlyList<SnapshotMessage>?> SearchMessagesAsync(string groupId, string q, string? topicId = null, int? count = null, CancellationToken ct = default)
    {
        var query = QueryString(
            ("q", q),
            ("topicId", topicId),
            ("count", count?.ToString()));
        return SendAsync<IReadOnlyList<SnapshotMessage>>(Build(HttpMethod.Get, $"/ag-ui/group/{groupId}/messages/search{query}"), ct);
    }

    /// <summary>多智能体讨论：后台串行触发多个群内智能体。</summary>
    public Task<DiscussionStarted?> StartDiscussionAsync(string groupId, string content, IReadOnlyList<string>? agentIds = null, string? topicId = null, CancellationToken ct = default)
        => SendAsync<DiscussionStarted>(Build(HttpMethod.Post, $"/ag-ui/group/{groupId}/discussion", new DiscussionHttpRequest { Content = content, AgentIds = agentIds, TopicId = topicId }), ct);

    /// <summary>人机交互决策（仅触发者可决策）。</summary>
    public Task<InteractionResolved?> ResolveInteractionAsync(GroupInteractionResolveRequest request, CancellationToken ct = default)
        => SendAsync<InteractionResolved>(Build(HttpMethod.Post, "/ag-ui/group/interaction/resolve", request), ct);

    /// <summary>SSE 场景动态订阅（connectionId 来自 GROUP_CONNECTED 握手，POST /ag-ui/group/subscribe）。</summary>
    public Task<SseSubResult?> SubscribeSseAsync(string connectionId, IReadOnlyList<string> groupIds, CancellationToken ct = default)
        => SendAsync<SseSubResult>(Build(HttpMethod.Post, "/ag-ui/group/subscribe", new SseSubscribeRequest { ConnectionId = connectionId, GroupIds = groupIds }), ct);

    /// <summary>SSE 场景动态退订（connectionId 来自 GROUP_CONNECTED 握手，POST /ag-ui/group/unsubscribe）。</summary>
    public Task<SseSubResult?> UnsubscribeSseAsync(string connectionId, IReadOnlyList<string> groupIds, CancellationToken ct = default)
        => SendAsync<SseSubResult>(Build(HttpMethod.Post, "/ag-ui/group/unsubscribe", new SseSubscribeRequest { ConnectionId = connectionId, GroupIds = groupIds }), ct);

    private static string QueryString(params (string Key, string? Value)[] pairs)
    {
        var sb = new StringBuilder();
        foreach (var (key, value) in pairs)
        {
            if (string.IsNullOrEmpty(value)) continue;
            sb.Append(sb.Length == 0 ? '?' : '&');
            sb.Append(key).Append('=').Append(Uri.EscapeDataString(value));
        }
        return sb.ToString();
    }

    #endregion

    #region 智能体

    /// <summary>智能体目录（GET /ag-ui/agents）。</summary>
    public Task<IReadOnlyList<AgentDefinitionDto>?> ListAgentsAsync(CancellationToken ct = default)
        => SendAsync<IReadOnlyList<AgentDefinitionDto>>(Build(HttpMethod.Get, "/ag-ui/agents/"), ct);

    /// <summary>新增智能体（需登录，创建者成为其 Owner）。</summary>
    public Task<AgentUpsertResult?> CreateAgentAsync(AgentUpsertRequest request, CancellationToken ct = default)
        => SendAsync<AgentUpsertResult>(Build(HttpMethod.Post, "/ag-ui/agents/", request), ct);

    /// <summary>更新智能体（仅创建者可编辑，内置智能体只读）。</summary>
    public Task<AgentUpsertResult?> UpdateAgentAsync(string agentId, AgentUpsertRequest request, CancellationToken ct = default)
        => SendAsync<AgentUpsertResult>(Build(HttpMethod.Put, $"/ag-ui/agents/{Uri.EscapeDataString(agentId)}", request), ct);

    /// <summary>删除智能体（仅创建者可删，内置智能体只读）。</summary>
    public Task<AgentDeleteResult?> DeleteAgentAsync(string agentId, CancellationToken ct = default)
        => SendAsync<AgentDeleteResult>(Build(HttpMethod.Delete, $"/ag-ui/agents/{Uri.EscapeDataString(agentId)}"), ct);

    /// <summary>为群注册触发规则（协议 §6，需调用者为群成员且智能体为该群成员）。</summary>
    public Task<AgentRegisterResult?> RegisterAgentAsync(AgentRegisterRequest request, CancellationToken ct = default)
        => SendAsync<AgentRegisterResult>(Build(HttpMethod.Post, "/ag-ui/agent/register", request), ct);

    /// <summary>注销触发规则（指定群 → 需群主/管理员；全部群 → 仅系统管理员）。</summary>
    public Task<AgentUnregisterResult?> UnregisterAgentAsync(AgentUnregisterRequest request, CancellationToken ct = default)
        => SendAsync<AgentUnregisterResult>(Build(HttpMethod.Post, "/ag-ui/agent/unregister", request), ct);

    #endregion

    #region 附件

    /// <summary>上传附件（multipart 的 file 字段，可多个），返回附件元信息列表。</summary>
    public async Task<IReadOnlyList<AttachmentInfo>> UploadAsync(IEnumerable<UploadFile> files, CancellationToken ct = default)
    {
        var token = Token ?? _options.TokenProvider?.Invoke();
        using var content = new MultipartFormDataContent();
        foreach (var file in files)
        {
            var bytes = await ReadAllBytesAsync(file.Stream, file.Length, ct).ConfigureAwait(false);
            var part = new ByteArrayContent(bytes);
            part.Headers.ContentType = string.IsNullOrEmpty(file.ContentType)
                ? null
                : MediaTypeHeaderValue.Parse(file.ContentType);
            content.Add(part, "file", file.FileName);
        }
        using var req = new HttpRequestMessage(HttpMethod.Post, "/ag-ui/upload") { Content = content };
        if (!string.IsNullOrEmpty(token))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw ParseError(resp, body);
        return JsonSerializer.Deserialize<UploadResult>(body, AguiJson.Options)?.Attachments ?? [];
    }

    private static async Task<byte[]> ReadAllBytesAsync(Stream stream, long? length, CancellationToken ct)
    {
        if (stream is MemoryStream ms)
            return ms.ToArray();
        using var target = new MemoryStream(length.HasValue ? (int)Math.Min(length.Value, int.MaxValue) : 0);
        await stream.CopyToAsync(target, ct).ConfigureAwait(false);
        return target.ToArray();
    }

    #endregion

    #region 结果 DTO

    /// <summary>上传文件描述。</summary>
    public sealed class UploadFile
    {
        /// <summary>文件名（须在 Hub 扩展名白名单内）。</summary>
        public required string FileName { get; set; }
        /// <summary>文件字节流。</summary>
        public required Stream Stream { get; set; }
        /// <summary>可选 MIME 类型。</summary>
        public string? ContentType { get; set; }
        /// <summary>可选长度提示。</summary>
        public long? Length { get; set; }
    }

    /// <summary>AddMembersAsync 返回结构（服务端返回成员列表）。</summary>
    public sealed class GroupMemberAddResult
    {
        public IReadOnlyList<GroupMember>? Members { get; set; }
    }

    /// <summary>RemoveMembersAsync 返回结构。</summary>
    public sealed class GroupMemberRemoveResult
    {
        public IReadOnlyList<string>? Removed { get; set; }
    }

    /// <summary>ClearTopicAsync 返回结构。</summary>
    public sealed class ClearTopicResult
    {
        public bool Cleared { get; set; }
        public string? TopicId { get; set; }
        public int RemovedCount { get; set; }
    }

    /// <summary>StopAgentRunAsync 返回结构。</summary>
    public sealed class StopRunResult
    {
        public bool Stopped { get; set; }
    }

    /// <summary>StartDiscussionAsync 返回结构。</summary>
    public sealed class DiscussionStarted
    {
        public bool Started { get; set; }
        public IReadOnlyList<string>? Agents { get; set; }
    }

    /// <summary>ResolveInteractionAsync 返回结构。</summary>
    public sealed class InteractionResolved
    {
        public bool Resolved { get; set; }
    }

    /// <summary>CreateAgentAsync / UpdateAgentAsync 返回结构。</summary>
    public sealed class AgentUpsertResult
    {
        public bool Created { get; set; }
        public bool Updated { get; set; }
        public string? AgentId { get; set; }
        public string? Nickname { get; set; }
    }

    /// <summary>DeleteAgentAsync 返回结构。</summary>
    public sealed class AgentDeleteResult
    {
        public bool Deleted { get; set; }
        public string? AgentId { get; set; }
    }

    /// <summary>RegisterAgentAsync 返回结构。</summary>
    public sealed class AgentRegisterResult
    {
        public bool Registered { get; set; }
        public string? AgentId { get; set; }
        public IReadOnlyList<string>? GroupIds { get; set; }
    }

    /// <summary>UnregisterAgentAsync 返回结构。</summary>
    public sealed class AgentUnregisterResult
    {
        public bool Unregistered { get; set; }
        public string? AgentId { get; set; }
    }

    /// <summary>SubscribeSseAsync / UnsubscribeSseAsync 返回结构。</summary>
    public sealed class SseSubResult
    {
        public bool Subscribed { get; set; }
        public bool Unsubscribed { get; set; }
    }

    #endregion
}
