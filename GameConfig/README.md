# GameConfig — 客户端/服务器共享配置交付目录

客户端与服务器的移动手感参数、状态迁移表、位移曲线、体素数据统一从本目录读取。
本目录里既有“录入源”，也有“生成产物”：

- **状态迁移表**：录入源是 `StateTransitionTable/state_transition_table.xlsx`，同目录的 `state_transition_table.json` 是导表生成物。
- **玩家移动参数**：`Player/player_config.xlsx` 管理服务器读取的 9 个数值字段，曲线/旋转/动画名等 Unity 字段仍由 `Player.asset` 维护，合并生成 `Player/player_config.json`。
- **位移曲线、体素数据**：由 Unity 工具生成。

不要手改 `StateTransitionTable/state_transition_table.json` 和 `Player/player_config.json`，也不要直接改客户端 `.asset` 或服务器 `config.json` 里的移动参数。

## 目录结构

| 文件 | 内容 |
|---|---|
| `StateTransitionTable/state_transition_table.xlsx` | 状态迁移表 Excel 录入源（矩阵：行=源状态，列=目标状态，交叉格填 `1` 表示允许） |
| `StateTransitionTable/state_transition_table.json` | 状态迁移表（由 Excel 导表生成，服务器读） |
| `Player/player_config.xlsx` | 玩家配置 Excel 录入源（仅服务器实际读取的 9 个数值字段） |
| `Player/player_config.json` | 完整玩家配置（Excel 服务器字段 + Unity 曲线/旋转/动画字段合并生成） |
| `displacement_curves.json` | 位移曲线导出（服务器端逐帧位移上限，`tools/export_displacement` 生成） |
| `MainCity_voxels.bytes` | 体素地形数据（Unity `Tools > Voxel Generator` 生成，服务器读取） |
| `README.md` | 本文档 |

## 状态迁移表读写流程（Excel 源）

```
编辑 GameConfig/StateTransitionTable/state_transition_table.xlsx   (唯一录入源)
        │
        └─ Unity 菜单 BigWorld/Config/从Excel导出状态迁移表
           ├─ 生成 GameConfig/StateTransitionTable/state_transition_table.json（服务器读）
           └─ 生成 Assets/Resources/Config/StateTransitionTable.json（客户端读）
```

导表工具会强校验：工作表名必须为 `state_transition_table`；行/列的 13 个状态名必须与客户端
`MoveTransitionTable.StateNames`（并与服务器 `StateNameToMoveState` 同步维护）完全一致，缺一个或多一个都会失败；
交叉格只接受 `1 / x / X / y / Y / √ / 是 / true / yes`（允许）或留空、`0 / false / no / 否 / - / ×`（禁止）；
矩阵内不要使用公式，也不要在矩阵外随意填写内容。

Excel 源文件与两份生成 JSON 都纳入版本管理；改完 Excel 后请先执行上面的导出菜单再提交。

## player_config 读写流程（Excel + Unity 混合）

```
Unity Player.asset（曲线/旋转/动画名/资产路径等 Unity 字段）
        │  Unity 菜单 BigWorld/Config/导出到共享文件夹
        ▼
完整 JSON 骨架 ──＋── GameConfig/Player/player_config.xlsx（9 个服务器数值字段）
        │                Unity 菜单 BigWorld/Config/从Excel导出玩家配置
        ▼
GameConfig/Player/player_config.json（合并生成物，禁止手改）
        │
        ├─ 客户端：Unity 菜单 BigWorld/Config/从共享文件夹导入
        │           → 刷新 Assets/Data/Player.asset
        │
        └─ 服务器：重启时直接读 ../GameConfig/Player/player_config.json
```

- `Player/player_config.xlsx` 只允许包含服务器实际读取的 9 个字段：`grounded.base_speed`、`grounded.sprint.speed_modifier`、`grounded.sprint.sprint_to_run_time`、`grounded.roll.speed_modifier`、`airborne.fall.fall_speed_limit`、`airborne.fall.gravity`、`collider.height`、`collider.center_y`、`voxel_max_step_height`。
- 其他玩家配置仍在 Unity `Player.asset` 里维护；Unity 改完非 Excel 字段后先执行 `导出到共享文件夹`，再执行 `从Excel导出玩家配置` 合并。
- `从Excel导出玩家配置` 会在写 JSON 后自动刷新客户端 `Player.asset`。

**位移曲线**（服务器端）：Unity 改完曲线/动画后，菜单 `BigWorld/Config/导出位移曲线到共享文件夹`，
结果直接写入 `GameConfig/displacement_curves.json`（schema 与旧 `tools/export_displacement` Go 工具一致）。

**体素数据**：`Tools > Voxel Generator` 生成时默认输出到本目录（`GameConfig/MainCity_voxels.bytes`），
并自动同步一份到客户端 `Assets/Resources/VoxelData/`（运行时 `Resources.Load` 加载，且只有客户端
工程内文件才打包，故两份是同步副本，本目录为源）。

## player_config 字段 ↔ 客户端类映射

顶层段：`grounded` / `airborne` / `collider` / `slope` / `layers` / `voxel_max_step_height` / `animation`，对应 `PlayerConfig` 的 `GroundedData` / `AirborneData` / `DefaultColliderData` / `SlopeData` / `LayerData` / `VoxelMaxStepHeight` / `AnimationData`。

- `grounded.base_speed` → `PlayerGroundedData.BaseSpeed`（基础移速）
- `grounded.base_rotation.target_rotation_reach_time` → `PlayerRotationData.TargetRotationReachTime`（Vector3）
- `grounded.walk/run/dash/sprint/roll` → 各 `Player*Data.SpeedModifier`；dash 还有连击时间、冷却等
- `grounded.stop` → `PlayerStopData` 三档减速力 + 位移曲线
- `grounded.slope_speed_angles` → `AnimationCurve`（2 个 keyframes）
- `airborne.jump` / `airborne.fall` → `PlayerJumpData` / `PlayerFallData`（fall.gravity 是服务器重力参数）
- `collider` → `DefaultColliderData`（服务器用它算落点/碰撞）
- `slope` → `SlopeData`
- `layers.ground_layer_bits` → `PlayerLayerData.GroundLayer`（LayerMask 的 int bits）
- `voxel_max_step_height` → `PlayerConfig.VoxelMaxStepHeight`
- `animation` → `PlayerAnimationData` 动画状态名
- 曲线字段（`curve_x` 等）是**客户端工程内**资产相对路径（`Assets/Resources/Animations/DisplacementCurves/...`），客户端导入时按路径解析，服务器不读。

## Player/player_config.xlsx 说明

- 工作表名：`server_params`；表头固定为 `A1=字段路径`、`B1=值`、`C1=说明`。
- 只允许出现上面列出的 9 个服务器字段，缺少、重复、未知字段都会导表失败；值必须是数字，并做基本范围校验（重力/下落限速/碰撞体高度必须 > 0 等）。
- 不要在该 sheet 的 A/B/C 三列之外填写内容。

## 服务器如何派生移动参数

服务器读取后按以下公式派生（见 `world/gameconfig.go`）：

| 服务器字段 | 共享配置来源 |
|---|---|
| `max_step_height` | `voxel_max_step_height` |
| `sprint_speed_mps` | `grounded.base_speed × grounded.sprint.speed_modifier` |
| `roll_speed_mps` | `grounded.base_speed × grounded.roll.speed_modifier` |
| `fall_gravity_mps2` | `airborne.fall.gravity` |
| `fall_speed_limit_mps` | `airborne.fall.fall_speed_limit` |
| `player_height` / `player_center_y` | `collider.height` / `collider.center_y` |

> 服务器不再有移动参数副本：`sprint_speed_mps` 等值全部从 `Player/player_config.json` 派生，
> `common/config.json` 里只保留 `player_config_file` / `state_transition_table_file` 路径。
> 上表中这些数值字段的录入源是 `Player/player_config.xlsx`；改完后执行
> `BigWorld/Config/从Excel导出玩家配置`，服务器重启即生效。

## state_transition_table 说明

录入源是 `StateTransitionTable/state_transition_table.xlsx` 的 `state_transition_table` 工作表（矩阵），
`StateTransitionTable/state_transition_table.json` 与客户端 `Assets/Resources/Config/StateTransitionTable.json` 均为导表生成物，禁止手改。

状态名使用客户端 `MoveTransitionTable.StateNames` 定义的名字：`Idling, Walking, Running, Sprinting, LightStopping, MediumStopping, HardStopping, LightLanding, Rolling, Dashing, JumpUp, Falling, JumpDown`。

- 客户端：`MoveTransitionTable.CanTransition(from, to)` 直接查生成到 `Resources` 的表。
- 服务器：`world/gameconfig.go` 把名字映射成 `pb.MoveState`（`Idling→MOVE_IDLE`、`Walking→MOVE_WALK`…），非法迁移返回「非法状态转换」；加载时遇到未知状态名会直接报错，不再静默跳过。
- 导表工具：Unity 菜单 `BigWorld/Config/从Excel导出状态迁移表`。

## 注意

- 客户端增删状态时，需同步：`MoveStateMachine.cs` 里 `MoveTransitionTable.StateMappings`、服务器 `world/gameconfig.go` 的 `StateNameToMoveState` 映射，并更新 Excel 矩阵的表头与行头；导表器会自动按 `StateNames` 校验完整性。
- 曲线资产（DisplacementCurveAsset）仍留在客户端工程内（动画相关），共享 JSON 只存路径引用。
- 重力：共享值取客户端 `airborne.fall.gravity = 10`，与旧服务器 config.json 的 `1.0` 不同，统一后服务器自由落体会更快，需实测手感。
