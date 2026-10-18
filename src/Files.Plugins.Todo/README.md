# Todo 插件开发文档

轻量待办清单插件：todo.txt 纯文本 + 截图附件，通过 `IToolWindowProvider` 注册为侧边栏"待办"入口的独立工具窗口。

## 项目结构

| 文件 | 职责 |
|---|---|
| `TodoPlugin.cs` | 插件入口：初始化 `TodoStore`，响应侧边栏点击（首次创建窗口，之后激活并刷新） |
| `TodoStore.cs` | 数据层：todo.txt / done.txt 读写与行解析、附件注册、分组与外观配置持久化 |
| `TodoWindow.cs` | 全部 UI：纯代码构建 WinUI 界面（无 XAML 文件），列表 / 分组卡片 / 筛选 / 颜色图标编辑器 |

## 数据目录（默认 `data\todo\`，可自定义）

| 路径 | 内容 |
|---|---|
| `todo.txt` | 未完成任务，每行一条 |
| `done.txt` | 已完成任务归档 |
| `attachments\` | 截图等附件，任务行内以 `att:<文件名>` 引用 |
| `config.json` | 分组、选中视图、外观配置 |
| `window.size` | 窗口尺寸（`宽 高`），关闭时写入、启动时读回 |

## todo.txt 行格式与扩展键

标准 todo.txt 语法之上，扩展键追加在行尾（外部 todo.txt 工具会忽略未知键，兼容互转）：

| 键 | 含义 |
|---|---|
| `(A)` 行首 | 优先级 A / B / C |
| `2026-10-18` | 创建日期（todo.txt 标准） |
| `created-time:HH:mm:ss` | 创建时间扩展键 |
| `completed-time:HH:mm:ss` | 完成时间扩展键（仅 done.txt） |
| `att:<文件名>` | 附件文件，位于 `attachments\` |
| `star:1` | 星标 |
| `@标签` | 分组（todo.txt 原生 context，全文检索） |

解析在 `TodoStore.ParseAsync`：正则剥离扩展键与优先级后，剩余部分即任务正文。修改行（勾选 / 星标 / 就地编辑）后 `RawLine` 置空，保存时重新序列化。

## config.json 结构

```json
{
  "DataDirectory": "…\\data\\todo",
  "Groups": ["工作", "sgame"],
  "SelectedGroup": null,
  "GroupStyles": [
    { "Name": "工作", "Color": "3B6DB3", "Icon": "\uE8B7" },
    { "Name": ":unassigned:", "Color": "C0392B" }
  ]
}
```

- `SelectedGroup`：`null` = 全部；`:starred:` = 星标视图；`:unassigned:` = 收集箱；其余为分组名。
- `GroupStyles`：每个分组的颜色（RRGGBB）与图标（Segoe Fluent 字形）；`:unassigned:` 是收集箱的保留样式键。未设置的分组按 `GroupPalette` 顺序自动配色（收集箱默认 `3B6DB3` / `\uE8B7`，星标固定 `FFB900` / `\uE735`）。
- 序列化使用 System.Text.Json 源生成（`TodoConfigContext`），新增配置字段需可空 + 默认值，旧文件缺字段时必须空值保护（`?? []`）。

## 界面行为要点

- **窗口尺寸**：拖动时经 `AppWindow.Changed` 记录（最大化 / 最小化不计），关闭时写入 `window.size`；启动按保存尺寸恢复，越界值回落默认 520×780。
- **分组卡片**：「全部」视图按分组渲染彩色卡片（收集箱在最前），卡片头 = 图标 + 分组名 + `已完成/总数`；卡内"添加待办"回车即落入该分组（自动附加 `@标签`）。内容宽度自适应 1~3 列（阈值约 440px/列），列数变化才重排。
- **平铺视图**：选中具体分组 / 星标 / 收集箱时仍为单列任务列表。
- **选中高亮**：任务行是带 `TodoTask` Tag 的 `Border`，`EnumerateTaskCards` 递归遍历（卡片嵌套层级加深后仍生效）。
- **颜色色块**：用 `Border` + `Tapped` 而非 `Button`——按钮悬停态的半透明白会盖掉自定义背景色。
- **就地编辑**：编辑框靠 `LostFocus` + 窗口级 `PointerPressed` 兜底提交；`Expander.Content` 只构建一次，运行期替换会在点击分发中崩溃（0x80070057）。

## 构建与调试

```powershell
# 必须单独编译插件：构建 Files.App 不会重新编译 / 复制插件 DLL
msbuild -restore src/Files.Plugins.Todo/Files.Plugins.Todo.csproj -p:Configuration=Release -p:Platform=x64
```

- 插件编译后由 `CopyToAppPluginsDirectory` 目标自动复制到 `Files.App\bin\Unpackaged\x64\<配置>\net10.0-windows10.0.26100.0\win-x64\plugins\`。
- 插件加载结果看 `data\appdata\debug.log`：`[Plugins] 3/3 plugins loaded` 表示全部初始化成功，插件抛异常会带堆栈记在这里。
- 改动后需重启应用（插件在启动时加载）；先关闭运行中的 FilesPlus 再编译，避免 DLL 被占用。
