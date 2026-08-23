# 品牌图标

AG-UI 群聊（知聚）的官方品牌图标。

## 图形含义

圆角渐变底板（品牌主色 `#4f8cff` 系）上，**六个代表成员 / 智能体的白色节点**
通过连线汇聚到中央"协作中枢"圆环——寓意**多人 / 多智能体围绕协作圆桌聚集讨论**
（呼应产品定位"多智能体群聊协作平台"与品牌名"知聚"——会聚、聚集）。

小尺寸下仍保持清晰：中心环 + 节点 + 连线构成可辨识的"网络 / 中枢"剪影。

## 文件

| 文件 | 用途 |
|---|---|
| `agui-icon.svg` | 矢量源图（权威设计稿，任意缩放） |
| `agui-icon-16..256.png` | 多尺寸 PNG（通用） |
| `agui-icon.ico` | Windows 多尺寸 ICO（16/24/32/48/64/128/256，Web 与桌面 exe 均用） |
| `favicon-16.png` / `favicon-32.png` | Web favicon 位图 |

## 在项目中的接入位置

- **Web**：`src/AguiGroupChat.Web/wwwroot/` —— `favicon.svg` + `favicon-32.png` + `favicon-16.png`
  （`index.html` 的 `<link rel="icon">`），`agui-icon-256.png` 作为 apple-touch-icon。
- **桌面（WPF）**：`src/AguiGroupChat.Desktop/Assets/agui-icon.ico`（exe + 任务栏，`<ApplicationIcon>`）
  与 `MainWindow` 的 `Icon`（标题栏）。
- **桌面（Avalonia）**：`src/AguiGroupChat.Desktop.Cross/Assets/agui-icon.ico`（exe）与
  `Assets/agui-icon-256.png`（窗口图标，`MainWindow.SetWindowIcon`）。

## 重新生成

矢量源见 `agui-icon.svg`；位图 / ICO 由脚本栅格化生成：

```bash
dotnet run --project tools/icon-gen -- assets
```

产出的位图与 ICO 覆盖到上述各接入目录即可（已生成的成品已在此与各接入目录）。
