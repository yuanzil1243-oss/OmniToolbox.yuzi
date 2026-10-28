# OmniToolbox.yuzi

Yuzi 的 OmniToolbox（树树妙妙屋）在线模块仓库。

当前正式清单使用 DLL 交付。前四个模块来自 `OmniToolbox-Yuzi-Local-Omni1.1.8.5-20261028` 修复包，最低宿主为 Omni **1.1.8.5**；成就追踪 **0.6.0** 使用本仓库源码构建，最低宿主为 Omni **1.1.8.6**。

| 模块 | 发布文件 | 版本 |
| --- | --- | --- |
| 隐藏目标圈 | `Modules/HideTargetCircle.dll` | 0.1.6 |
| 快捷出售 | `Modules/SellOnClick.dll` | 0.2.9 |
| 极限技调整 | `Modules/LimitBreakAdjustments.dll` | 0.1.6 |
| 按键连点 | `Modules/KeyRepeater.dll` | 0.2.3 |
| 成就追踪 | `Modules/AchievementTracker.dll` | 0.6.0 |

清单中的路径、版本和 SHA256 与上述 DLL 对应。前四个模块的原有 `.cs` 文件保留，但不再作为在线下载文件；修复包未包含对应源码，因此这些旧源码不代表修复包 DLL 的完整实现。成就追踪的完整源码、构建工程和存储测试随本仓库保存。

## 模块列表

- **快捷出售**（QuickSell）— 按住选中的 Alt、Ctrl、Shift 修饰键并右键单击物品，在 NPC 商店或雇员出售界面执行游戏原生出售动作；修饰键和自动确认设置会保存到本机配置。
- **极限技调整**（LimitBreakAdjustments）— 调整极限技条旋转、条间距、文本位置与显示，并提供三条极限技预览。
- **成就追踪**（AchievementTracker）— 直接读取游戏原生目标列表和成就数据；有计数器的成就显示实时进度，无计数器的成就显示达成条件，支持按角色保存、地区过滤和手动搜索添加。地区预设优先使用游戏数据中的明确关联，再使用离线整理的补充映射；启用后可使用 `/omni AchievementTracker overlay` 切换游戏内窗口。
- **隐藏目标圈**（HideTargetCircle）— 默认隐藏选中目标的目标圈，可选拔出武器或战斗状态时显示，并设置战斗结束后的延迟隐藏时间。
- **按键连点**（KeyRepeater）— 自动读取游戏技能栏主、副快捷键；按住任意已绑定按键或组合键时，按设定 CPS 持续发送输入，松开后立即停止。

## 在 Omni 中添加

在「插件设置 → 在线模块」中填写清单地址：

```
https://raw.githubusercontent.com/yuanzil1243-oss/OmniToolbox.yuzi/main/TreeHouseModules.json
```

首次安装后模块默认关闭，请在「树树妙妙屋 → 在线」中手动启用。

## 开发约定

模块源码统一放在 `Modules/`，版本文件固定使用根目录 `TreeHouseModules.json`。新增模块时在清单中追加条目，并填写发布文件（`.cs` 或 `.dll`）相对路径与实际文件的 SHA256。

**版本规则：以后任何改动都必须递增涉及模块的版本号，同步构建产物、清单、SHA256 和相关说明，不得覆盖发布同版本的不同内容。

### 成就追踪 0.6.0

- 采用 API 1 的嵌套 `Settings` / 可写 `config`，设置词条完整本地化，事件与 Hook 通过 `FeatureLifetime` 管理。
- 宿主配置只保存新角色默认设置；角色目标、备注、排序、地区和显示设置继续保存在 Omni 配置目录的 `AchievementTracker-{ContentId:X16}.json`，原字段名保留。
- 原无角色文件 `AchievementTracker.json` 仅在符合原迁移条件时导入一个角色，角色档案成功写入后才记录迁移完成；已有角色文件优先。
- 保存队列固定角色路径和 JSON 快照，按顺序写入；先写临时文件再替换正式文件，保留上一版 `.bak`。主文件损坏时尝试备份；无有效档案或版本不兼容时暂停该角色的修改和写入。
- 从备份恢复时保留 `.corrupt-*` 原件，不把损坏内容覆盖到有效备份。实时成就计数和请求队列只保存在内存。
- 已完成本机 Release/x64 编译和 18 项离线存储检查；游戏内的加载、原生进度、重登恢复与角色切换由用户验证。

构建（默认引用本机 Omni 1.1.8.6 / Dalamud Hooks dev，可用 MSBuild 属性 `OmniDir`、`DalamudDir` 覆盖路径）：

```powershell
dotnet build build/AchievementTracker.csproj -c Release
dotnet run --project tests/AchievementTracker.StorageTests/StorageTests.csproj -c Release
```

成就地区补充数据维护在 `Modules/AchievementTracker.cs` 的 `OfflineRegionSupplements` 中，只保存成就 ID 与 `TerritoryType` ID。Wiki 只在开发期用于整理和复核映射，不作为游戏运行时依赖；每次大版本资料完整后再更新映射。区域不明确、地区 ID 无法解析或候选区域超过 10 个时不自动预设。映射只在新添加或重新选择成就时应用，不覆盖现有目标的地区设置。

Omni Toolbox 在线模块。默认按住 **Shift＋鼠标右键单击**背包物品，自动选择游戏原生菜单的“出售”或“让雇员出售”。设置支持 Alt、Ctrl、Shift 的非空组合，需同时按住所选键。

仅在 NPC 商店或雇员物品／市场列表界面、且原生菜单确实提供可用的系统出售动作时触发。不选择市场上架，不绕过游戏数量或贵重物品确认。不使用角色缓存猜测槽位，直接验证本次点击的实时槽位；不修改 Omni 本体。

在线模块库：
https://raw.githubusercontent.com/yuanzil1243-oss/OmniToolbox.SellOnClickOnline/master/TreeHouseModules.json

在 Omni 在线模块中刷新该仓库并更新“按键出售”。作者：Yuzi。版本 0.2.0 已通过本机 Omni 1.1.6.6 / Dalamud API 15 程序集编译；实际出售仍需游戏内验证。

技术参考：SimpleTweaksPlugin 的 QuickSellItems 展示了原生物品上下文菜单出售入口。本项目保留原生确认流程。
