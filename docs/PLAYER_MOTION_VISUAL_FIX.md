# 玩家移动发糊与顿挫：问题、根因与修复总结

本文档记录一次针对「移动时画面发糊、有顿挫感」的排查与修复过程，便于后续维护与调参。

---

## 1. 现象

- 移动时团簇/画面**发软、发糊**（与运动方向相关时更明显）。
- 同时有**顿挫、不跟手**感，与纯「抗锯齿发糊」的静态柔化不同。

---

## 2. 分阶段结论（根因）

### 2.1 渲染与后处理（初期分析）

- 主相机曾使用 URP **FXAA** 时，全屏对高对比边做邻域混合，运动的小圆边界在屏上快速扫过会**整体变柔**。
- 工程里 **DefaultVolume** 中运动模糊、泛光、景深等强度为 0，**不是**主因。
- 曾将主相机抗锯齿从 FXAA 换为 **SMAA** 并恢复后处理等，对「发糊+顿挫」的改善**有限**（问题不只在后处理）。

### 2.2 物理与显示时间轴（主要技术根因）

1. **固定步长与刷新率**  
   项目原 `Fixed Timestep` 为 **0.02s（50Hz）**，而常见显示器为 **60 / 90 / 120 / 144Hz**。离散步进与连续刷新的错配会带来**阶跃感**；多刷新、少物理时，同一位置被画多帧再跳变，主观上又糊又顿。

2. **输入与受力的时序**  
   玩家方向由 `GameInput` 在 `Update` 更新，而 `SetPlayerInput` 在 `PlayerControl.LateUpdate` 写入。若物理在 `FixedUpdate` 中**只读**上一帧 `LateUpdate` 里存好的方向，会多**一帧**才反映到力上，加重**不跟手**。

3. **错误的「位置插值」**  
   曾用 `lerp(积分前, 积分后, α)`，且 `α` 取 `(Time.time - Time.fixedTime) / fixedDeltaTime`。在「刚算完一次物理」的时刻，该 **α 往往接近 0**，画面会偏向**积分前**状态，等于**把已算好的新状态又压回历史位置**，观感上既是**滞后**又像**糊/拖尾**。

4. **摄像机与离散物理脱节**  
   `CameraFollow` 用 `SmoothDamp` 追随**积分后的质心**，粒子若已在显示层做补偿，相机却仍按「慢半拍」的轨迹走，会加重**相对运动模糊**。

---

## 3. 已实施的修复（按模块）

### 3.1 `ParticleSimulation.cs`（核心）

| 措施 | 说明 |
|------|------|
| **`_fixedPhysicsHz`（默认 120）** | 在 `Awake` 中设置 `Time.fixedDeltaTime = 1/hz`，与高频显示器更合拍；设为 **0** 则不改项目时间设置。 |
| **`CurrentPhysicsInputDir`** | 在 **`FixedUpdate`** 内优先使用同物体 **`GameInput.DirectionThisFrame`**，使受力方向与 **`Update`** 同步，避免晚一帧才进物理。 |
| **`VisualSmoothingMode`** | 移除有问题的帧间 **lerp**；默认 **`VelocityExtrapolation`**：绘制位置为 **`pos + vel × (Time.time - Time.fixedTime)`**（余量封顶，避免卡顿后爆冲）。 |
| **`PlayerOwnedAverageVelocity`** | 统计玩家拥有粒子的平均速度，供相机做**同一套时间余量**的外推对齐。 |
| **`UsesVelocityVisualSmoothing`** | 告知相机是否在启用速度外推显示。 |

### 3.2 `CameraFollow.cs`

- **`smoothTime <= 0`** 时直接对齐目标，避免对 `SmoothDamp` 传非法或极小的平滑时间。
- **`_extrapolateWithPlayerVelocity`**（默认开启）：在无边界模式下，目标点在质心上叠加 **`PlayerOwnedAverageVelocity × rem`**，并与模拟的显示外推一致。
- 场景中可将 **`_unboundedSmoothTime`** 设为 **0**，去掉相机滞后（质心抖动明显时可略微加大平滑）。

### 3.3 `ProjectSettings/TimeManager.asset`

- `Fixed Timestep` 与运行时逻辑对齐为 **120Hz（0.008333333）**（若改 `_fixedPhysicsHz`，注意与项目设置一致或由代码覆盖）。

### 3.4 场景 / 相机（曾有过一轮）

- 主相机：按需关闭重型 **FXAA**、或改用 **SMAA / None**；体积里运动模糊等保持为 **0** 时起不到糊屏作用。
- **`SampleScene`**：无边界摄像机平滑等可按体验微调。

---

## 4. 调试与调参建议

| 目标 | 建议 |
|------|------|
| **省 CPU** | 将 **`_fixedPhysicsHz`** 降为 **90** 或 **60**。 |
| **关掉显示补偿做对比** | **`Visual Smoothing`** 选 **None**，确认顿挫是否来自离散步长本身。 |
| **相机是否与球「不同步」** | 开关 **`Extrapolate With Player Velocity`**，或与 **`UsesVelocityVisualSmoothing`** 配对使用。 |
| **仍嫌糊（静态也糊）** | 回到渲染侧：相机 **Anti-aliasing**（None / SMAA）、关闭不必要的后处理。 |
| **受力「扯来扯去」** | 调节 **`_damping`、`ClusterCohesionStrength`、`PlayerInputForce`** 等，属于玩法手感而非时间轴 bug。 |

---

## 5. 涉及的主要文件

- `Assets/Scripts/Simulation/ParticleSimulation.cs` — 固定步长、输入方向、`VisualSmoothingMode`、速度外推、`PlayerOwnedAverageVelocity`
- `Assets/Scripts/Rendering/CameraFollow.cs` — 平滑边界、速度与外推对齐
- `Assets/Scripts/Player/PlayerControl.cs` — 仍为 `LateUpdate` 内 `SetPlayerInput`（质心、团簇等）；物理方向以 **`GameInput` + FixedUpdate** 为准
- `Assets/Scripts/Input/GameInput.cs` — 每帧方向采样
- `ProjectSettings/TimeManager.asset` — 默认固定步长
- `Assets/Scenes/SampleScene.unity` — 主相机、粒子物体上的组件引用与公开字段

---

## 6. 一句话总结

**发糊与顿挫并行时，不能只调抗锯齿：**  
需要在 **固定物理步长 + 输入进入物理的时机 + 显示层如何填补「物理步之间」的时间** 上对齐；本次采用 **更高物理频率、FixedUpdate 内即时输入、速度外推绘制、相机按同一余量外推质心**，并去掉 **数学上反向的帧间 lerp**，从机制上减轻「糊 + 顿」。
