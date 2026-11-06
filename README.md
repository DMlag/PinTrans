# AiPhoto 截图翻译

Windows 截图工具，内置 **主要使用多模态大模型翻译**。截图直接交给大模型，它一次完成识别和翻译，译文**画在原文的位置上**。
为什么不ocr之后再翻译？因为离线ocr对比小语种的识别能力比较差

## 功能

| 功能 | 说明 |
|---|---|
| 区域截图与智能吸附 | 冻结全屏后框选；支持 **PixPin 风格四层智能吸附**（顶层窗口 + Win32 子控件 + UI Automation 网页/现代 UI 元素 + 图像边缘/文字块）；**鼠标滚轮**或 **`Tab` / `Shift+Tab`** 切换父子层级；带放大镜和取色；方向键逐像素微调 |
| 标注编辑 | 矩形、椭圆、箭头、直线、画笔、荧光笔、文字、马赛克、序号；可撤销/重做；按住 Shift 约束形状 |
| AI 翻译（原位显示） | 用 Gemini 结构化输出拿到每段文字的坐标，用背景色盖住原文再画上译文；按住 Shift 看原文；单击某段可复制 |
| 长截图 | 手动滚动或自动滚动；自动识别固定的页头/页尾；长图可以整张分段翻译 |
| HDR 截图 | 在 HDR 显示器上通过 DXGI 拿到 FP16 scRGB 原始数据；可保存为 `.jxr`（保留 HDR）；复制/PNG/翻译用色调映射后的 SDR 图 |
| 贴图 | 把截图钉在屏幕上：可拖动、滚轮缩放、复制、保存、翻译 |
| 代理 | 系统代理 / HTTP / SOCKS5（可带认证），也可自定义 API 中转地址 |
| 快捷键 | 全局快捷键都可以在设置里修改 |

### 默认快捷键

| 操作 | 快捷键 |
|---|---|
| 截图 | `F1` |
| 截图并翻译 | `F2` |
| 长截图 | `Ctrl+F1` |
| 全屏截图到剪贴板 | 未设置 |

截图窗口内：`Enter`/双击 复制并完成 · **鼠标滚轮** / `Tab` / `Shift+Tab` 切换父子吸附层级 · `Ctrl+S` 保存 · `Ctrl+T` 翻译 · `Ctrl+P` 贴图 · `Ctrl+Z/Y` 撤销/重做 · `R E A L P H T M N` 切换标注工具 · `C` 复制颜色 · `Esc` 退出 · 右键 取消当前工具或选区。

## 使用

1. 从 [Releases](../../releases) 或 Actions 构建产物下载 `AiPhoto-*-win-x64.zip`，解压后运行 `AiPhoto.exe`。程序是自包含的单文件，不需要另外安装 .NET。
2. 第一次运行会打开设置。在 **AI 翻译** 页填写 Gemini API Key（[Google AI Studio](https://aistudio.google.com/apikey) 创建）。
3. 在中国大陆使用时，到 **网络代理** 页配置代理（例如 Clash：HTTP `127.0.0.1:7890`），或者填写中转 Base URL。然后点 **测试并获取模型**。
4. 按 `F2` 框选区域，几秒后译文就会显示在原位置。

> **Google AI Ultra 额度**：订阅附带的每月 Google Cloud 抵扣金需要在 [Google Developer Program](https://developers.google.com/profile) 的权益页面激活，并关联到 API Key 所在 Cloud 项目的结算账号，Gemini API 的费用才会从这笔额度里扣。截图翻译用 Flash 模型，每次大约 $0.001–0.003。

## 架构

```
AiPhoto.slnx
├─ src/AiPhoto.Core        跨平台的纯逻辑（net10.0），有单元测试
│  ├─ Imaging/             BGRA / FP16 图像缓冲、色调映射、旋转、文字颜色估计
│  ├─ Snapping/            智能吸附索引（Z-Order 遮挡裁剪、去重）与纯 C# 图像边缘/文字块检测
│  ├─ Stitching/           长截图拼接（行哈希 + 固定页头页尾检测）、长图切分
│  ├─ Translation/         IImageTranslator 抽象、Gemini 实现（SSE 流式 + JSON Schema）、增量 JSON 解析
│  ├─ Settings/            设置模型与存储（密钥加密由 ISecretProtector 负责）
│  ├─ Hotkeys/ Net/        快捷键解析、代理 HttpClient 工厂
├─ src/AiPhoto.App         WPF 应用（net10.0-windows）
│  ├─ Capture/             DXGI Desktop Duplication（HDR）、GDI 回退、JXR 编码、截图窗口
│  ├─ Annotations/         标注模型、工厂、撤销/重做、渲染层
│  ├─ Translation/         译文排版（自动字号、背景/前景色）与覆盖层
│  ├─ Pin/ Viewer/         贴图窗口、长图查看器
│  ├─ LongCapture/         长截图会话（自动滚动、选区边框、控制面板）
│  ├─ Settings/            设置窗口、快捷键录制控件
│  └─ Services/AppHost.cs  组合根：托盘、全局快捷键、网络、窗口调度
└─ tests/AiPhoto.Core.Tests  xUnit
```

### 扩展点

- **新的翻译服务**（OpenAI、Claude、本地模型等）：实现 `IImageTranslator`，在 `AppHost.RebuildNetwork` 里按 `Settings.Translation.Provider` 选择。
- **新的标注工具**：继承 `Annotation`（拖拽类继承 `DragAnnotation`），在 `AnnotationTool` 和 `AnnotationFactory` 里注册，然后在 `CaptureWindow.xaml` 的工具栏加一个按钮。
- **新的设置项**：在 `AppSettings` 的对应分组加属性（JSON 会自动兼容旧文件），在 `SettingsWindow.xaml` 绑定。

## 开发

需要 .NET 10 SDK。WPF 项目只能在 Windows 上运行，但能在任何系统上编译（`EnableWindowsTargeting`）。

```bash
dotnet build AiPhoto.slnx
dotnet test tests/AiPhoto.Core.Tests
dotnet run --project src/AiPhoto.App          # Windows
dotnet publish src/AiPhoto.App -c Release -r win-x64 -o publish/win-x64
```

GitHub Actions（`.github/workflows/build.yml`）在 Windows 上构建、跑测试，并发布 win-x64 / win-arm64 单文件。推送 `v*` 标签时自动创建 Release。

设置文件：`%AppData%\AiPhoto\settings.json`（API Key 和代理密码用 DPAPI 加密）。日志：`%AppData%\AiPhoto\logs`。

## 已知限制

- 译文定位依赖模型返回的坐标，通常准确，但文字非常密集时可能偏几个像素。
- 长截图需要被截的程序响应鼠标滚轮。带惯性滚动或懒加载的页面，可以把“滚动后等待”调大。分数缩放下滚动出现亚像素位移时，拼接可能失败，这时改用手动慢速滚动。
- 独占全屏的游戏可能截不到画面，请改用无边框窗口模式。受 DRM 保护的视频会是黑屏。
