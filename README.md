# FastGOAP-Core

[中文文档](./README.zh-CN.md)

A universal GOAP planner middleware with a native C++ core and bridge adapters for game engines (Unity adapter available now).

## Overview

FastGOAP-Core decouples GOAP planning from engine-specific runtime code and exposes a stable middleware layer.

- The planner core runs in C++.
- Engine-side code is responsible for data bridging, scheduling, and result application.
- A C ABI is used as the integration boundary, so additional engines can be added without rewriting the planner.

## Project Goals

- Build an engine-agnostic GOAP planner middleware.
- Support multiple engines through a single ABI contract.
- Keep frame-time stable under large agent counts via budgeted scheduling.
- Provide managed/native backend switching for iteration speed and deployment safety.

## Current Features

- Native C++ planner core:
	- `Native/FastGoapMiddleware/src/FastGoapMiddleware.cpp` (C ABI exports)
	- `Native/FastGoapMiddleware/src/FastGoapPlanner.cpp` (planning solver)
	- `Native/FastGoapMiddleware/src/FastGoapRuntime.cpp` (context/worker runtime)
- C ABI v1 context workflow:
	- create context
	- upload graph
	- submit requests
	- poll results
	- query last error
- Unity bridge and scheduler in `MiddlewareUnity/`:
	- budgeted submit/process/poll per frame
	- in-flight tracking, timeout fallback, failure backoff
	- ECS plan writeback
	- runtime metrics and diagnostics
- Backend modes:
	- `Managed`
	- `Native`
	- `Auto` (prefer native, fallback to managed)

## Repository Layout

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

## Architecture

1. Engine-side (Unity) gathers world state, goals, and action availability for each agent.
2. Scheduler packs them into bitmap-based `GoapPlanRequest`.
3. `GoapMiddlewareBackend` forwards requests to native or managed runtime.
4. C++ planner solves asynchronously and outputs fixed-layout `GoapPlanResult`.
5. Unity polls results and writes plans back for execution systems.

## ABI v1 (Core API)

Primary API endpoints:

- `Goap_CreateContext`
- `Goap_DestroyContext`
- `Goap_UploadGraph`
- `Goap_SubmitRequestsV1`
- `Goap_PollResultsV1`
- `Goap_GetLastError`

Legacy compatibility endpoints:

- `Goap_Init`
- `Goap_SubmitRequests`
- `Goap_PollResults`
- `Goap_Shutdown`

## Build (Windows)

### Option A: Batch Script (Fastest)

```bat
cd Native\FastGoapMiddleware
build_win64.bat
```

The script builds `FastGoapMiddleware.dll` with MSVC and tries to copy it to `..\..\Assets\Plugins\x86_64\`.
If your Unity project path differs, update the destination path in `build_win64.bat`.

### Option B: CMake

```bash
cd Native/FastGoapMiddleware
cmake -S . -B build
cmake --build build --config Release
```

## Unity Integration (Current Adapter)

1. Add `GoapMiddlewareConfigAuthoring` in your scene:
	 - `MiddlewareUnity/Authoring/GoapMiddlewareConfigAuthoring.cs`
2. Set `PlanningPipeline` to `Middleware`.
3. Use `BackendMode = Auto` in most cases.
4. Tune runtime budget controls:
	 - `MaxSubmitPerFrame`
	 - `MaxProcessPerFrame`
	 - `MaxPollPerFrame`
	 - `ReplanIntervalSeconds`
	 - `RequestTimeoutSeconds`

## Why Middleware + Bridge

This split keeps responsibilities clear:

- Engine layer: data extraction, scheduling, execution integration.
- Planner core: solving performance, memory behavior, ABI stability.

Adding support for a new engine usually means adding a new bridge adapter while reusing the same planner core.

## Status and Roadmap

Current status:

- C++ planner: working.
- Unity bridge: implemented.
- Managed fallback: implemented.

Planned:

- More engine adapters (Unreal, Godot, custom engines).
- Better graph update tooling and debug visualization.
- More benchmark coverage and automated tests.

---

FastGOAP-Core is built for practical production use: stable frame-time under load, portable planner core, and predictable integration behavior.
