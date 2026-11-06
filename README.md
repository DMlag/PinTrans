# PinTrans 截图贴图与 AI 原位翻译

[![Build](https://github.com/DMlag/PinTrans/actions/workflows/build.yml/badge.svg)](https://github.com/DMlag/PinTrans/actions/workflows/build.yml)
[![Release](https://img.shields.io/github/v/release/DMlag/PinTrans?label=Release)](https://github.com/DMlag/PinTrans/releases)
[![License](https://img.shields.io/badge/License-NonCommercial--ShareAlike-blue.svg)](LICENSE)

Windows 截图工具，**主要使用多模态大模型接口翻译**。截图直接交给大模型，它一次完成识别和翻译，译文**画在原文的位置上**。  
为什么不先 OCR 之后再翻译？因为离线 OCR 对小语种、复杂排版、艺术字与气泡文字的识别能力较弱；但为了满足日常快速复制中英文和扫码的需求，软件同时内置了**零延迟本地离线 OCR 与二维码识别**。

## 功能

| 功能 | 说明 |
|---|---|
| 区域截图与智能吸附 | 冻结全屏后框选；支持 **四阶段智能吸附**（顶层窗口 + Win32 子控件 + UI Automation 网页/现代 UI 元素 + 图像边缘/文字行块识别）；**鼠标滚轮**或 **`Tab` / `Shift+Tab`** 顺滑切换父子层级；带放大镜和取色；方向键逐像素微调 |
| 矢量化标注编辑 | 矩形、椭圆、箭头、直线、画笔、荧光笔、文字、**马赛克**、**高斯模糊**、序号；**支持点选已有标注进行二次移动、拖拽控制点缩放、二次改色/改粗细、双击修改文字、`Delete` 删除**；全状态撤销/重做 |
| AI 翻译（原位显示） | 用 Gemini 结构化流式输出拿到每段文字的精确坐标，自动采样周围背景色盖住原文并自适应字号画上译文；按住 `Shift` 对照原文；单击某段即可复制译文 |
| 离线 OCR 与二维码识别 | 内置 Windows 原生离线 OCR（含小图双线性超分与中文空格智能净化）+ 多码制二维码/条形码识别（支持深色模式反色码）；框选二维码时自动弹出**复制 / 打开链接**快捷条；贴图模式下支持单击复制单行或展开文本面板自由划选 |
| 长截图（滚动截图） | 手动滚动或自动滚轮注入；基于行哈希签名自动无缝拼接并识别固定页头/页尾；长图支持整张分段翻译 |
| HDR 截图 | 在 HDR 显示器上通过 DXGI 拿到 FP16 scRGB 原始数据；可保存为 `.jxr`（保留完整 HDR 亮度）；复制/PNG/翻译自动使用色调映射后的 SDR 图 |
| 贴图 | 把截图钉在屏幕上：可拖动、滚轮缩放、保存、离线 OCR 识字/扫码、AI 原位翻译；小尺寸贴图工具栏自动外置悬浮，绝不遮挡画面 |
| 代理 | 系统代理 / 直连 / HTTP / SOCKS5（可带账号密码认证），也可自定义 API 中转地址（Base URL） |
| 快捷键 | 所有全局快捷键均可在设置里自定义录制 |

### 默认快捷键

| 操作 | 快捷键 |
|---|---|
| 截图 | `F1` |
| 截图并翻译（AI 原位翻译） | `F2` |
| 截图离线识字 / 扫码 (OCR) | `F3` |
| 长截图 | `Ctrl+F1` |
| 全屏截图到剪贴板 | 未设置（可在设置中绑定） |

**截图窗口内快捷键：**
- `Enter` / 双击：复制并完成
- **鼠标滚轮** / `Tab` / `Shift+Tab`：切换父子控件吸附层级
- `Ctrl+S`：保存 · `Ctrl+T`：AI 翻译 · `Ctrl+O`：离线识字/扫码 · `Ctrl+P`：贴图
- `Ctrl+Z` / `Ctrl+Y`：撤销 / 重做 · `Delete` / `Backspace`：删除当前选中的标注
- `R` 矩形 · `E` 椭圆 · `A` 箭头 · `L` 直线 · `P` 画笔 · `H` 荧光笔 · `T` 文字 · `M` 马赛克 · `B` 高斯模糊 · `N` 序号
- `方向键`：微调光标、选区或已选中标注的位置 · `C`：复制当前像素颜色 · `Esc`：退出 · `右键`：取消当前工具/取消选中标注/重选区域

**贴图窗口内快捷键：**
- 按住 `Shift`：临时查看原文 · `O`：运行离线 OCR / 二维码识别 · `Ctrl+T`：重新运行 AI 翻译
- `Ctrl+C`：复制当前图片 · `Ctrl+Shift+C`：复制全部译文/识别文字 · `Ctrl+S`：另存为 · `鼠标滚轮`：缩放贴图 · `Esc` / 双击：关闭贴图

## 使用

1. 从 [Releases](https://github.com/DMlag/PinTrans/releases) 下载最新版的 `PinTrans-*-win-x64.zip`（或 `win-arm64.zip`），解压后直接运行 `PinTrans.exe`。程序是自包含的单文件，不需要另外安装 .NET 运行时。
2. 第一次运行会打开设置。在 **AI 翻译** 页填写 Gemini API Key（可在 [Google AI Studio](https://aistudio.google.com/apikey) 创建）。若仅使用截图、标注、贴图和离线 OCR/扫码功能，无需填写 API Key 即可直接使用。
3. 在中国大陆使用 AI 翻译时，可到 **网络代理** 页配置代理（例如 Clash：HTTP `127.0.0.1:7890`），或者填写自建的中转 Base URL，然后点 **测试并获取模型**。
4. 按 `F1` 截图、按 `F2` 框选区域直接原位翻译、按 `F3` 框选区域离线提取文字或扫描二维码。
5. 如需彻底清理配置或卸载软件，可在 **设置 → 常规 → 卸载与数据清理** 中一键清除注册表开机启动项、`%AppData%\PinTrans` 配置与日志、单文件释放缓存，并自动移除程序文件（或运行 `PinTrans.exe --uninstall`）。

> **Google AI Ultra 额度提示**：订阅附带的每月 Google Cloud 抵扣金需要在 [Google Developer Program](https://developers.google.com/profile) 的权益页面激活，并关联到 API Key 所在 Cloud 项目的结算账号，Gemini API 的费用才会从这笔额度里扣。截图翻译推荐使用 Flash 系列模型，每次约 $0.001–0.003。

## 架构

```text
AiPhoto.slnx
├─ src/AiPhoto.Core          跨平台的纯核心逻辑（net10.0），含完整单元测试
│  ├─ Imaging/               BGRA / FP16 图像缓冲、色调映射、高斯模糊、旋转、文字背景/前景色估计
│  ├─ Ocr/                   二维码/条形码识别（ZXing）、离线 OCR 小图超分预处理与中英文排版空格净化
│  ├─ Snapping/              智能吸附索引（Z-Order 遮挡裁剪、去重）与图像边缘/文字行块检测
│  ├─ Stitching/             长截图拼接（行哈希签名 + SAD 精配准 + 固定页头页尾检测）、长图切分
│  ├─ Translation/           IImageTranslator 抽象、Gemini 实现（SSE 流式 + JSON Schema）、增量 JSON 解析
│  ├─ Settings/              设置模型与持久化存储（敏感凭据加密由 ISecretProtector 负责）
│  └─ Hotkeys/ Net/          快捷键解析、HTTP/SOCKS5 代理 HttpClient 工厂
├─ src/AiPhoto.App           Windows WPF 桌面应用（net10.0-windows10.0.19041.0）
│  ├─ Capture/               DXGI Desktop Duplication（HDR）、GDI 回退、UIA 异步控件探测、截图主窗口
│  ├─ Annotations/           矢量化标注模型、命中测试与控制点手柄、高斯模糊/马赛克、全状态撤销/重做栈
│  ├─ Ocr/                   Windows.Media.Ocr 本地离线识别服务
│  ├─ Translation/           译文原位排版（自适应字号、背景覆盖）与交互高亮层
│  ├─ Pin/ Viewer/           贴图窗口（悬浮工具栏 + OCR 划选抽屉）、长图查看器
│  ├─ LongCapture/           长截图会话（自动滚轮注入、选区边框、实时预览面板）
│  ├─ Settings/              设置窗口、快捷键录制控件
│  └─ Services/AppHost.cs    组合根：系统托盘、全局快捷键、网络、窗口调度
└─ tests/AiPhoto.Core.Tests  xUnit 单元测试套件（280+ 测试用例）
```

### 扩展点

- **新的翻译服务**（OpenAI、Claude、DeepSeek、本地多模态模型等）：实现 `IImageTranslator`，在 `AppHost.RebuildNetwork` 里按 `Settings.Translation.Provider` 选择。
- **新的标注工具**：继承 `Annotation`（拖拽类继承 `DragAnnotation`），在 `AnnotationTool` 和 `AnnotationFactory` 里注册，然后在 `CaptureWindow.xaml` 的工具栏加一个按钮。
- **新的设置项**：在 `AppSettings` 的对应分组加属性（JSON 会自动兼容旧文件），在 `SettingsWindow.xaml` 绑定。

## 开发与构建

需要 .NET 10 SDK。WPF 项目在 Windows 上运行，同时支持在 Linux / macOS 上交叉编译与运行核心测试（`EnableWindowsTargeting`）。

```bash
dotnet build AiPhoto.slnx -c Release
dotnet test tests/AiPhoto.Core.Tests -c Release
dotnet run --project src/AiPhoto.App          # Windows
dotnet publish src/AiPhoto.App -c Release -r win-x64 -o publish/win-x64
```

GitHub Actions（[`.github/workflows/build.yml`](.github/workflows/build.yml)）在每次推送时自动在 Windows 环境编译、运行测试，并产出 `win-x64` 与 `win-arm64` 单文件压缩包；推送 `v*` 标签时自动创建 GitHub Release。

- 设置文件：`%AppData%\PinTrans\settings.json`（API Key 和代理密码使用 Windows DPAPI 当前用户密钥加密存储）
- 日志目录：`%AppData%\PinTrans\logs`

## 已知限制

- 译文定位依赖多模态模型返回的坐标，通常准确，但文字极其密集时可能偏几个像素。
- 长截图需要目标窗口响应鼠标滚轮。带惯性滚动或懒加载的页面，可以把“滚动后等待”调大；若遇到特殊自绘滚动条，可改用手动慢速滚动。
- 独占全屏（Exclusive Fullscreen）游戏可能无法被桌面复制接口捕获，请改用无边框窗口模式。受硬件 DRM 保护的视频画面会显示为黑屏。

## 开源许可（License）

本项目采用 **[PinTrans 开源非商用及相同方式共享许可协议 (GPL-3.0 Copyleft + Non-Commercial / CC BY-NC-SA 4.0)](LICENSE)**：
- **允许个人免费使用、学习与修改**；
- **强制衍生开源（ShareAlike）**：任何修改、二次开发或衍生分发版本，必须以相同协议完整公开源代码；
- **严禁商业用途（Non-Commercial）**：未经作者书面授权，严禁将本软件或其衍生版本用于任何商业售卖、套壳收费、付费引流或企业商业集成。
