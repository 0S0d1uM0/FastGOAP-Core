# FastGOAP-Core

[English](./README.md)

一个通用 GOAP 规划中间件：核心为原生 C++ Planner，通过桥接层接入不同引擎（当前已适配 Unity）。

## 项目概述

FastGOAP-Core 将 GOAP 规划从具体引擎逻辑中解耦，提供可复用的中间件层。

- 规划核心在 C++ 中运行。
- 引擎侧负责数据桥接、调度与结果回写。
- 通过稳定的 C ABI 对接不同引擎，新增引擎时无需重写规划器。

## 目标

- 构建引擎无关的 GOAP 规划中间件。
- 通过统一 ABI 支持多引擎接入。
- 在大规模单位场景下，利用预算调度保证帧时间稳定。
- 支持托管/原生后端切换，兼顾迭代效率与部署稳定性。

## 当前功能

- 原生 C++ 规划核心：
  - `Native/FastGoapMiddleware/src/FastGoapMiddleware.cpp`（C ABI 导出层）
  - `Native/FastGoapMiddleware/src/FastGoapPlanner.cpp`（规划求解器）
  - `Native/FastGoapMiddleware/src/FastGoapRuntime.cpp`（上下文与工作线程运行时）
- C ABI v1 上下文流程：
  - 创建上下文
  - 上传图数据
  - 提交请求
  - 轮询结果
  - 查询错误信息
- Unity 桥接与调度（`MiddlewareUnity/`）：
  - 每帧预算提交/处理/回收
  - in-flight 请求管理、超时回退、失败退避
  - ECS 计划回写
  - 运行时统计与诊断日志
- 后端模式：
  - `Managed`
  - `Native`
  - `Auto`（优先原生，失败回退托管）

## 仓库结构

```text
FastGOAP-Core/
  README.md
  README.zh-CN.md
  MiddlewareUnity/
    GoapMiddlewareSchedulerSystem.cs
    GoapMiddlewareBackend.cs
    GoapMiddlewareInterop.cs
    GoapManagedRuntime.cs
    GoapMiddlewareConfig.cs
    GoapMiddlewareMetricsSystem.cs
    UnitTacticalOrderCommandBridgeSystem.cs
    Authoring/
      GoapMiddlewareConfigAuthoring.cs
  Native/
    FastGoapMiddleware/
      src/FastGoapMiddleware.cpp
      src/FastGoapMiddlewareTypes.h
      src/FastGoapPlanner.h
      src/FastGoapPlanner.cpp
      src/FastGoapRuntime.h
      src/FastGoapRuntime.cpp
      CMakeLists.txt
      build_win64.bat
      build/
```

## 架构流程

1. Unity 侧收集 Agent 的世界状态、目标和动作可用性。
2. Scheduler 将其打包为位图请求 `GoapPlanRequest`。
3. `GoapMiddlewareBackend` 将请求转发到 Native 或 Managed 后端。
4. C++ 规划器异步求解并输出定长 `GoapPlanResult`。
5. Unity 轮询结果并回写计划，执行系统按计划驱动行为。

## ABI v1 核心接口

主接口：

- `Goap_CreateContext`
- `Goap_DestroyContext`
- `Goap_UploadGraph`
- `Goap_SubmitRequestsV1`
- `Goap_PollResultsV1`
- `Goap_GetLastError`

兼容保留接口：

- `Goap_Init`
- `Goap_SubmitRequests`
- `Goap_PollResults`
- `Goap_Shutdown`

## 构建（Windows）

### 方式 A：批处理脚本（最快）

```bat
cd Native\FastGoapMiddleware
build_win64.bat
```

脚本会使用 MSVC 构建 `FastGoapMiddleware.dll`，并尝试复制到 `..\..\Assets\Plugins\x86_64\`。
如果你的 Unity 工程路径不同，请自行修改 `build_win64.bat` 里的目标路径。

### 方式 B：CMake

```bash
cd Native/FastGoapMiddleware
cmake -S . -B build
cmake --build build --config Release
```

## Unity 接入（当前适配）

1. 在场景中添加 `GoapMiddlewareConfigAuthoring`：
   - `MiddlewareUnity/Authoring/GoapMiddlewareConfigAuthoring.cs`
2. 将 `PlanningPipeline` 设为 `Middleware`。
3. 推荐使用 `BackendMode = Auto`。
4. 调整关键预算参数：
   - `MaxSubmitPerFrame`
   - `MaxProcessPerFrame`
   - `MaxPollPerFrame`
   - `ReplanIntervalSeconds`
   - `RequestTimeoutSeconds`

## 为什么用“中间件 + 桥接”

这种结构可以清晰分工：

- 引擎层：数据采集、调度、执行对接。
- 核心层：求解性能、内存行为、ABI 稳定性。

新增引擎时，通常只需要新增桥接层，规划核心可以复用。

## 当前状态与路线图

当前状态：

- C++ 规划器：可用。
- Unity 桥接：已实现。
- 托管回退后端：已实现。

计划中：

- 更多引擎桥接（Unreal、Godot、自研引擎）。
- 更完善的图更新工具与调试可视化。
- 更完整的基准测试与自动化测试。

---

FastGOAP-Core 的定位是实用优先：高负载下帧时间稳定、核心可移植、接入行为可预期。
