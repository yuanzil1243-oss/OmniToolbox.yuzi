# OmniToolbox.yuzi

Yuzi 的 OmniToolbox（树树妙妙屋）在线模块仓库。

## 模块列表

- **快捷出售**（QuickSell）— 按住选中的 Alt、Ctrl、Shift 修饰键并右键单击物品，在 NPC 商店或雇员出售界面执行游戏原生出售动作；修饰键和自动确认设置会保存到本机配置。
- **极限技调整**（LimitBreakAdjustments）— 调整极限技条旋转、条间距、文本位置与显示，并提供三条极限技预览。
- **成就追踪**（AchievementTracker）— 直接读取游戏原生目标列表和成就数据；有计数器的成就显示实时进度，无计数器的成就显示达成条件，支持按角色保存、地区过滤和手动搜索添加。地区预设优先使用游戏数据中的明确关联，再使用离线整理的补充映射；启用后可使用 `/omni 成就追踪悬浮窗` 切换游戏内窗口。
- **隐藏目标圈**（HideTargetCircle）— 默认隐藏选中目标的目标圈，可选拔出武器或战斗状态时显示，并设置战斗结束后的延迟隐藏时间。

## 在 Omni 中添加

在「插件设置 → 在线模块」中填写清单地址：

```
https://raw.githubusercontent.com/yuanzil1243-oss/OmniToolbox.yuzi/main/TreeHouseModules.json
```

首次安装后模块默认关闭，请在「树树妙妙屋 → 在线」中手动启用。

## 开发约定

模块源码统一放在 `Modules/`，版本文件固定使用根目录 `TreeHouseModules.json`。新增模块时在清单中追加条目，并填写源码相对路径与 SHA256。

成就地区补充数据维护在 `Modules/AchievementTracker.cs` 的 `OfflineRegionSupplements` 中，只保存成就 ID 与 `TerritoryType` ID。Wiki 只在开发期用于整理和复核映射，不作为游戏运行时依赖；每次大版本资料完整后再更新映射。区域不明确、地区 ID 无法解析或候选区域超过 10 个时不自动预设。映射只在新添加或重新选择成就时应用，不覆盖现有目标的地区设置。

Omni Toolbox 在线模块。默认按住 **Shift＋鼠标右键单击**背包物品，自动选择游戏原生菜单的“出售”或“让雇员出售”。设置支持 Alt、Ctrl、Shift 的非空组合，需同时按住所选键。

仅在 NPC 商店或雇员物品／市场列表界面、且原生菜单确实提供可用的系统出售动作时触发。不选择市场上架，不绕过游戏数量或贵重物品确认。不使用角色缓存猜测槽位，直接验证本次点击的实时槽位；不修改 Omni 本体。

在线模块库：
https://raw.githubusercontent.com/yuanzil1243-oss/OmniToolbox.SellOnClickOnline/master/TreeHouseModules.json

在 Omni 在线模块中刷新该仓库并更新“按键出售”。作者：Yuzi。版本 0.2.0 已通过本机 Omni 1.1.6.6 / Dalamud API 15 程序集编译；实际出售仍需游戏内验证。

技术参考：SimpleTweaksPlugin 的 QuickSellItems 展示了原生物品上下文菜单出售入口。本项目保留原生确认流程。
