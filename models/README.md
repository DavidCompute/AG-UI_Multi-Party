# models/ —— 内置 embedding 模型（llama.cpp server 的挂载源）

语义记忆（RAG）的 embedding 由 compose 里的 **`llama-embed`** 服务提供：它把本目录的
`embedding.gguf` 以只读方式挂进容器（容器内路径 `/models/embedding.gguf`）。

**模型不入源码仓库**（约 605MB，超过 GitHub 单文件限制），所以克隆之后必须自己放进来一份。

## 一键准备

```powershell
# Windows（默认从 ModelScope 取 bge-m3-Q8_0，约 605MB，1024 维）
powershell -ExecutionPolicy Bypass -File tools/download-embedding-model.ps1 -OutDir models
```

```bash
# Linux / macOS：按脚本里的默认 URL 自行下载
curl -L -o models/embedding.gguf \
  https://www.modelscope.cn/models/gpustack/bge-m3-GGUF/resolve/master/bge-m3-Q8_0.gguf
```

## 要求与注意

- **维度必须是 1024**（bge-m3）。换模型必须同步改 `MEMORY_EMBEDDING_DIMENSIONS`，否则向量维度
  与建表维度不一致，RAG 会**静默失效**（`tools/download-embedding-model.ps1` 里也有尺寸护栏）。
- 要镜像 / 内网源：用 `tools/download-embedding-model.ps1 -Url <你的地址>`，或直接拷贝到本目录
  并命名为 `embedding.gguf`。
- 文件缺失时：`llama-embed` 容器会**打印补模型的命令然后退出**（不会抛裸的 GGUF 打开失败），
  `web` 会一直等到它健康为止——即不会带着坏掉的记忆服务启动。
- 已入库向量与模型强相关：**换引擎（如 Ollama ↔ llama.cpp）不影响**（同模型下向量一致，
  cosine=1.000000），但**换模型或换量化**会让新旧向量语义漂移，需要重灌记忆。

## 与桌面版的关系

桌面版用同一个模型的另一份拷贝：`src/AguiGroupChat.Desktop/models/embedding.gguf`（随 MSI 打包，
桌面版是进程内 LLamaSharp 推理，与本目录/容器互不影响）。
