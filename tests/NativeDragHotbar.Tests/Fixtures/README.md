# 热键栏离线测试数据

- `native-bar-identities.json` 与 `native-bar-geometry.json` 仅保留回归测试需要的原生栏编号、布局及几何数值，不包含原始内存转储或地址。
- `native-local-chains.json`、`native-bindings-20261109.json` 和 `windows-se-keys.json` 用于离线重放父节点变换、原生键位及 SE/Windows 键码映射；测试不连接游戏或写入游戏配置。
- `KeyRepeaterTiming.cs` 是仅在测试程序中编译的连点时序参考实现，用于检查 IPC 首击去重；它不会编译进热键栏 DLL，也不代表更新或发布按键连点模块。
- 默认测试只构建和检查本模块。开发时如需额外检查已构建的按键连点 0.2.5，可设置 `NATIVE_DRAG_HOTBAR_CHECK_REPEATER=1`；正式热键栏打包不依赖该开发模块。
