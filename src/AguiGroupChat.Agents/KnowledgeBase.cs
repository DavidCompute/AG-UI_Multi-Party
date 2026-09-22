namespace AguiGroupChat.Agents;

/// <summary>
/// 知识库：智能体可绑定的知识文档集合。文档上传（经附件提取文本）后切片 + 向量化，
/// 存入语义记忆向量表（GroupId 约定 <c>kb:{KbId}</c>），智能体回复前按绑定列表检索相关片段注入上下文。
/// 元数据目录经快照持久化（AddSection("kb")），向量随文档增删维护。
/// </summary>
public sealed class KnowledgeBase
{
    public required string KbId { get; set; }

    public string Name { get; set; } = "";

    public string Description { get; set; } = "";

    /// <summary>创建者 userId；null = 系统级（所有用户可见）。</summary>
    public string? OwnerId { get; set; }

    /// <summary>
    /// 群级共享（2.4）：指定群（groupId 列表）的成员也能<b>查看 / 绑定</b>该知识库（只读，不可改文档）。
    /// 空 = 仅创建者 / 系统级；非空则在此基础上额外开放给所列群的成员（仍需登录）。
    /// </summary>
    public List<string> SharedGroupIds { get; set; } = [];

    /// <summary>
    /// 本库的<b>检索严格度</b>（相似度门槛 0.10~0.80，null = 未设置，沿用调用方传的值 / 平台默认 0.25）。
    ///
    /// <para>
    /// 为何要能按库调：知识库之间文档风格差别很大 ——
    /// 文档是<b>短条目 / 标签 / 目录</b>（制度条款、产品参数、人名表）时，通用提问得分普遍偏高，门槛要<b>收紧</b>；
    /// 文档是<b>长篇论述</b>时，一句提问与其中某段的相似度普遍偏低，门槛要<b>放松</b>才召得回。
    /// 一个全局值（<c>Agents:Memory:MinScore</c> 默认 0.25）必然两头都不合适。
    /// </para>
    ///
    /// <para>
    /// 生效位置：<see cref="KnowledgeBaseCatalog.SearchAsync"/> 在**每个库**上分别用它过滤向量命中
    /// （库设了就用库的，否则用调用方传的）。词面兜底不直接套这个门槛：BM25 分先换算到同一量纲
    /// （<see cref="Bm25Ranker.ToSimilarity"/>）再过一条**固定底线**（<see cref="Bm25Ranker.KeywordSimilarityFloor"/>）
    /// 与一道**词组证据**（<see cref="Bm25Ranker.HasPhraseEvidence"/>：查询里得有一段连续词项命中），
    /// 笛住罕罕见词 / 专有号，不让“恰好共用一个常用词”蒙混过关。
    /// </para>
    /// </summary>
    public double? MinScore { get; set; }

    /// <summary>文档清单（向量存记忆存储，此处仅元数据）。</summary>
    public List<KbDocument> Documents { get; set; } = [];

    public long UpdatedAtMs { get; set; }
}

/// <summary>知识库文档元数据（正文切片向量在记忆存储中：GroupId=kb:{KbId}，MessageId=docId:{i}）。</summary>
public sealed class KbDocument
{
    public required string DocId { get; set; }

    public string FileName { get; set; } = "";

    /// <summary>源附件 ID（POST /ag-ui/upload 返回；正文经 AttachmentStore 提取）。</summary>
    public string AttachmentId { get; set; } = "";

    /// <summary>切片数（= 写入的向量条数）。</summary>
    public int ChunkCount { get; set; }

    /// <summary>处理状态：processing（切片向量化中）/ ready（已入库）/ error（失败，见 Error）。</summary>
    public string Status { get; set; } = "ready";

    /// <summary>失败原因（Status=error 时）。</summary>
    public string? Error { get; set; }

    public long AddedAtMs { get; set; }
}
