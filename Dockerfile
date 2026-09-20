# syntax=docker/dockerfile:1
# AG-UI 群聊扩展协议 Hub —— Web 完整演示镜像（Hub + MSAGENT 智能体网关 + 静态前端）
# 构建：docker build -t agui-group-chat-web .
# 运行：docker run --rm -p 5200:8080 -e DEEPSEEK_API_KEY=sk-xxx -v agui-web-data:/app/data agui-group-chat-web

# ---------- 阶段一：编译发布 ----------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# 先只复制项目文件做 restore，最大化利用 Docker 层缓存
COPY AguiGroupChat.slnx ./
COPY src/AguiGroupChat.Hub/AguiGroupChat.Hub.csproj src/AguiGroupChat.Hub/
COPY src/AguiGroupChat.Agents/AguiGroupChat.Agents.csproj src/AguiGroupChat.Agents/
COPY src/AguiGroupChat.SkillHosting/AguiGroupChat.SkillHosting.csproj src/AguiGroupChat.SkillHosting/
COPY src/AguiGroupChat.Web/AguiGroupChat.Web.csproj src/AguiGroupChat.Web/
RUN dotnet restore src/AguiGroupChat.Web/AguiGroupChat.Web.csproj

# 复制全部源码并发布（Release，框架依赖）
COPY src/ src/
RUN dotnet publish src/AguiGroupChat.Web/AguiGroupChat.Web.csproj \
    -c Release -o /app/publish --no-restore

# ---------- 阶段二：精简运行 ----------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

# 容器内监听端口（对外映射由 docker compose / -p 控制）
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

# 持久化快照目录：compose 中挂载命名卷 agui-web-data
# 安装 curl（健康检查用，官方镜像不内置）与字体：
#   dotnet:aspnet 运行镜像默认<b>不含任何系统字体</b>，而 docx 内置技能的图表/文本渲染依赖字体
#   （ImageSharp 取系统字体；缺字体会直接报“未发现可用字体”）。
#   fonts-dejavu-core 提供基础拉丁字形；fonts-noto-cjk 提供中文（图表中文标签必需）。
#   fonts-droid-fallback 供内置 PDF 技能（pdf_doc）使用：PDF 必须把字体嵌进文件，
#   而 PDFsharp 只对 glyf(TrueType 轮廓)字体做子集化 —— fonts-noto-cjk 是 CFF/OTTO，
#   整份字体塞进 PDF 会让一页中文文档变成 13MB+；DroidSansFallbackFull.ttf 是 glyf，
#   实测同样一页只 46KB。它是 Apache-2.0，无授权顾虑（见 tools/pdf-skills/README.md）。
RUN apt-get update && apt-get install -y --no-install-recommends \
        curl \
        fonts-dejavu-core \
        fonts-noto-cjk \
        fonts-droid-fallback \
    && rm -rf /var/lib/apt/lists/*

# 官方镜像内置非 root 的 app 用户（APP_UID=1654，主组与其相同），显式切换并以该用户运行
# /app/docs 是内置 docx 技能的默认落盘目录（compose 中由 agui-docs 命名卷挂载）。
# 命名卷首次创建时归 root，而容器以 app 运行 —— 必须预建并 chown，否则技能写入报 Permission denied。
RUN mkdir -p /app/data /app/docs && chown $APP_UID:$APP_UID /app/data /app/docs

# 办公文档「在线查看」（docx / xlsx / pptx → PDF 内联渲染）依赖 LibreOffice：
# 它不是可选能力，而是 /ag-ui/preview/{id} 端点的**运行时依赖** —— 缺了它用户点「在线查看」
# 只会看到“服务端未安装文档转换组件”（503）。
# 为什么三个组件都要装：Impress 只管 pptx，Writer 管 docx，Calc 管 xlsx。
# 实测只装 libreoffice-impress 时 docx / xlsx 会报 "source file could not be loaded"（踩过）。
RUN apt-get update && apt-get install -y --no-install-recommends \
        libreoffice-writer \
        libreoffice-calc \
        libreoffice-impress \
    && rm -rf /var/lib/apt/lists/*

# ---------- 可选：版面 QA 工具（默认不装）----------
# poppler-utils（pdf → png，并且 pdftotext 能抽文本）
# 用途：把产物渲成图做「肉眼版式校验」与自动检查（见 tools/preview-pptx.py）。
# 注意：办公文档「在线查看」**不需要**它 —— 那条路径把 LibreOffice 转出的 PDF 原样返回给浏览器，
# 不需要再栅格化。所以它仍是开发/QA 能力，默认关。
# 需要时显式开启：compose 里设 AGUI_PREVIEW_TOOLS=true，或
#   docker compose build --build-arg AGUI_PREVIEW_TOOLS=true web
#
# 为什么值得装：渲染走的是**另一套渲染器**（LibreOffice），能拦住我们自己检不到的：
#   文字溢出文本框、字体/字形缺失、以及“PowerPoint 提示需要修复”这类版本兼容问题。
ARG AGUI_PREVIEW_TOOLS=false
RUN if [ "$AGUI_PREVIEW_TOOLS" = "true" ]; then \
        apt-get update && apt-get install -y --no-install-recommends \
            poppler-utils \
        && rm -rf /var/lib/apt/lists/*; \
    fi
USER $APP_UID

COPY --from=build /app/publish ./

# 健康检查：GET /ag-ui/health（curl 于上方 apt 安装）
HEALTHCHECK --interval=30s --timeout=3s --start-period=10s --retries=3 \
    CMD curl -fsS http://localhost:8080/ag-ui/health || exit 1

ENTRYPOINT ["dotnet", "AguiGroupChat.Web.dll"]
