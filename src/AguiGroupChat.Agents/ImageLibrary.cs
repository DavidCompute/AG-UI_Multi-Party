namespace AguiGroupChat.Agents;

/// <summary>
/// 图库：用户上传的图片集合，供文档技能（PPT / Word / PDF）按语义检索自动配图。
///
/// <para>
/// 与知识库同源（<see cref="KnowledgeBaseCatalog"/>）：都是“用户自备素材 + 向量检索”，
/// 区别只在存的是<b>图片</b>而不是文本切片 —— 所以权限模型、异步入库、共享语义都照知识库来，
/// 少一层切片（一张图一个向量）。
/// </para>
///
/// <para>
/// 存在的意义：让配图<b>不依赖外网</b>。企业内网部署里 Wikimedia 之类图库通常不可达，
/// 而“自己有一批公司图片，让数字员工配图时自动挑”才是真实需求。
/// </para>
/// </summary>
public sealed class ImageLibrary
{
    public required string LibId { get; set; }

    public string Name { get; set; } = "";

    public string Description { get; set; } = "";

    /// <summary>创建者 userId；null = 系统级（所有用户可见）。</summary>
    public string? OwnerId { get; set; }

    /// <summary>群级共享：指定群的成员也能查看 / 让技能使用该图库（只读，不可增删图片）。</summary>
    public List<string> SharedGroupIds { get; set; } = [];

    /// <summary>
    /// 本库的<b>检索严格度</b>（相似度门槛 0.30~0.95，null = 未设置，沿用调用方传的值）。
    ///
    /// <para>
    /// 为何要能按库调：图库之间描述风格差别很大 ——
    /// 描述是<b>短人名 / 标签</b>时，通用关键词的得分普遍偏高（容易配上不相干的图），门槛要收紧；
    /// 描述是<b>长句</b>时，标题型查询得分普遍偏低（实测 0.62），门槛要放松才配得上。
    /// 一个全局值必然两头都不合适。
    /// </para>
    ///
    /// <para>
    /// 生效位置：<c>/ag-ui/images/search</c> 在**每个库**上分别用它过滤向量命中（库设了就用库的，否则用请求里的）。
    /// 词面兜底不直接套这个门槛：BM25 分先换算到同一量纲（<see cref="Bm25Ranker.ToSimilarity"/>），
    /// 再过一条固定底线（<see cref="Bm25Ranker.KeywordSimilarityFloor"/>）与一道<b>词组证据</b>
    ///（<see cref="Bm25Ranker.HasPhraseEvidence"/>：查询里得有一段连续词项命中，不是散落一个常用词）。
    /// </para>
    /// </summary>
    public double? MinScore { get; set; }

    /// <summary>图片清单（向量存记忆存储 <c>GroupId=img:{LibId}</c>，此处仅元数据）。</summary>
    public List<ImageAsset> Assets { get; set; } = [];

    public long UpdatedAtMs { get; set; }
}

/// <summary>
/// 图库中的一张图片。
///
/// <para>
/// <see cref="Caption"/> 是语义检索的<b>唯一依据</b>（平台没有 CLIP 那种“以图搜图”），
/// 上传后由视觉模型自动生成，用户可改；改了会重新向量化。
/// </para>
/// </summary>
public sealed class ImageAsset
{
    public required string AssetId { get; set; }

    public string FileName { get; set; } = "";

    /// <summary>相对图库根目录的存储名（<c>{assetId}{ext}</c>）——绝对路径不写进快照，避免换机器后失效。</summary>
    public string StoredName { get; set; } = "";

    public string ContentType { get; set; } = "";

    public int Width { get; set; }

    public int Height { get; set; }

    public long Bytes { get; set; }

    /// <summary>语义描述（视觉模型生成 / 用户手改）。检索就是拿它做 embedding。</summary>
    public string Caption { get; set; } = "";

    /// <summary>用户标签（与描述一起参与向量化）。</summary>
    public List<string> Tags { get; set; } = [];

    /// <summary>处理状态：processing（生成描述 / 向量化中）| ready（可检索）| error（失败，见 Error）。</summary>
    public string Status { get; set; } = "processing";

    public string? Error { get; set; }

    public long UploadedAtMs { get; set; }

    public string? UploadedBy { get; set; }

    /// <summary>参与向量化的文本：描述 + 标签 + 文件名（文件名常含有用线索，但权重低——排在最后）。</summary>
    public string EmbeddingText()
    {
        var sb = new System.Text.StringBuilder();
        if (Caption.Length > 0) sb.Append(Caption);
        if (Tags.Count > 0)
        {
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(string.Join(" ", Tags));
        }
        if (FileName.Length > 0)
        {
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(FileName);
        }
        return sb.ToString();
    }
}
