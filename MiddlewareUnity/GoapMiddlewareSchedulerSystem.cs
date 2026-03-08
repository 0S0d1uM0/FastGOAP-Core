using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using SeventhSequence.ECS.Systems;

namespace SeventhSequence.ECS.GOAP
{
    /// <summary>
    /// 中间件调度系统：
    /// 1) 每帧最多提交 20 个单位到规划运行时；
    /// 2) 每帧按预算处理请求；
    /// 3) 非阻塞读取结果并回写计划
    /// </summary>
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(GoapSensorSystem))]
    [UpdateAfter(typeof(GoapPersonalitySystem))]
    [UpdateBefore(typeof(GoapPlannerSystem))]
    [UpdateBefore(typeof(GoapExecutionSystem))]
    public partial struct GoapMiddlewareSchedulerSystem : ISystem
    {
        private int _keyHasTarget;
        private int _keyHasAmmo;
        private int _keyLowHealth;
        private int _keyDamaged;
        private int _keyUnderAttack;
        private int _keyInCover;
        private int _keyAtPoint;
        private int _keyPointCaptured;
        private int _keyAllyInjured;
        private int _keyPatrolling;
        private double _nextFailureDiagLogTime;

        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<GoapAgentComponent>();
            state.RequireForUpdate<GoapGraphData>();

            _keyHasTarget = GoapUtils.GetKeyId(GoapKnownNames.KeyHasTarget);
            _keyHasAmmo = GoapUtils.GetKeyId(GoapKnownNames.KeyHasAmmo);
            _keyLowHealth = GoapUtils.GetKeyId(GoapKnownNames.KeyLowHealth);
            _keyDamaged = GoapUtils.GetKeyId(GoapKnownNames.KeyDamaged);
            _keyUnderAttack = GoapUtils.GetKeyId(GoapKnownNames.KeyUnderAttack);
            _keyInCover = GoapUtils.GetKeyId(GoapKnownNames.KeyInCover);
            _keyAtPoint = GoapUtils.GetKeyId(GoapKnownNames.KeyAtPoint);
            _keyPointCaptured = GoapUtils.GetKeyId(GoapKnownNames.KeyPointCaptured);
            _keyAllyInjured = GoapUtils.GetKeyId(GoapKnownNames.KeyAllyInjured);
            _keyPatrolling = GoapUtils.GetKeyId(GoapKnownNames.KeyPatrolling);
            _nextFailureDiagLogTime = 0;

            EnsureSingletons(ref state);
        }

        public void OnDestroy(ref SystemState state)
        {
            GoapMiddlewareBackend.Reset();
        }

        public void OnUpdate(ref SystemState state)
        {
            EnsureSingletons(ref state);
            EnsureAgentMiddlewareState(ref state);

            if (!TryGetConfig(ref state, out var config))
                return;

            var runtimeEntity = SystemAPI.GetSingletonEntity<GoapMiddlewareRuntimeState>();
            var runtimeState = SystemAPI.GetComponent<GoapMiddlewareRuntimeState>(runtimeEntity);
            byte previousUseMiddleware = runtimeState.UseMiddlewarePipeline;
            runtimeState.UseMiddlewarePipeline = (byte)(config.Enabled && config.PlanningPipeline == GoapPlanningPipelineMode.Middleware ? 1 : 0);

            if (previousUseMiddleware == 0 && runtimeState.UseMiddlewarePipeline != 0)
            {
                ResetAllAgentMiddlewareState(ref state);
            }

            if (runtimeState.UseMiddlewarePipeline == 0)
            {
                SystemAPI.SetComponent(runtimeEntity, runtimeState);
                return;
            }

            GoapMiddlewareBackend.EnsureInitialized(in config);

            runtimeState.Tick++;
            SystemAPI.SetComponent(runtimeEntity, runtimeState);

            double now = SystemAPI.Time.ElapsedTime;
            ref var graph = ref SystemAPI.GetSingleton<GoapGraphData>().BlobRef.Value;
            var metricsEntity = SystemAPI.GetSingletonEntity<GoapMiddlewareMetrics>();
            var metrics = SystemAPI.GetComponent<GoapMiddlewareMetrics>(metricsEntity);

            GoapMiddlewareBackend.SetPlannerBudget(config.SearchMaxExpansions, config.SearchMaxStates);
            GoapMiddlewareBackend.ConfigureGraph(ref graph);

            SubmitRequests(
                ref state,
                in config,
                ref graph,
                runtimeState.Tick,
                now,
                out int submitted,
                out int submitRejected,
                out int candidateAgents,
                out int needPlanAgents,
                out int missingDataAgents,
                out int cooldownAgents,
                out int noGoalAgents,
                out int noActionAgents,
                out int inFlightAgents);
            metrics.Submitted += (ulong)submitted;
            metrics.SubmitRejected += (ulong)submitRejected;

            if (submitted == 0 && needPlanAgents > 0 && (runtimeState.Tick % 30u) == 0u)
            {
                UnityEngine.Debug.LogWarning(
                    "[GOAP-MW] Submit=0 " +
                    "Candidates=" + candidateAgents +
                    " NeedPlan=" + needPlanAgents +
                    " MissingData=" + missingDataAgents +
                    " Cooldown=" + cooldownAgents +
                    " InFlight=" + inFlightAgents +
                    " NoGoal=" + noGoalAgents +
                    " NoAction=" + noActionAgents +
                    " Rejected=" + submitRejected);
            }

            // 中间件按固定预算处理请求，保证不会无限占用本帧时间
            int processed = GoapMiddlewareBackend.Process(config.MaxProcessPerFrame);
            metrics.Processed += (ulong)processed;

            ApplyResults(
                ref state,
                in config,
                ref graph,
                now,
                out int polled,
                out int applied,
                out int failed,
                out int failedNoPlan,
                out int failedInternal,
                out int failedTimeout,
                out int failedZeroStep,
                out bool hasFailSample,
                out uint sampleAgentId,
                out int sampleGoalId,
                out ulong sampleWorldBits,
                out ulong sampleEnabledBits,
                out ulong sampleExecutableBits,
                out GoapPlanStatus sampleStatus,
                out byte sampleStepCount);
            metrics.Polled += (ulong)polled;
            metrics.Applied += (ulong)applied;
            metrics.Failed += (ulong)failed;

            if (failed > 0 && now >= _nextFailureDiagLogTime)
            {
                _nextFailureDiagLogTime = now + 1.0;
                if (hasFailSample)
                {
                    UnityEngine.Debug.LogWarning(
                        "[GOAP-MW][FailDiag] " +
                        "Failed=" + failed +
                        " NoPlan=" + failedNoPlan +
                        " Internal=" + failedInternal +
                        " Timeout=" + failedTimeout +
                        " ZeroStep=" + failedZeroStep +
                        " SampleAgent=" + sampleAgentId +
                        " SampleStatus=" + sampleStatus +
                        " SampleSteps=" + sampleStepCount +
                        " GoalId=" + sampleGoalId +
                        " WorldBits=0x" + sampleWorldBits.ToString("X16") +
                        " EnabledBits=0x" + sampleEnabledBits.ToString("X16") +
                        " ExecBits=0x" + sampleExecutableBits.ToString("X16"));
                }
                else
                {
                    UnityEngine.Debug.LogWarning(
                        "[GOAP-MW][FailDiag] " +
                        "Failed=" + failed +
                        " NoPlan=" + failedNoPlan +
                        " Internal=" + failedInternal +
                        " Timeout=" + failedTimeout +
                        " ZeroStep=" + failedZeroStep);
                }
            }

            int timedOut = HandleTimeouts(ref state, in config, now);
            metrics.TimedOut += (ulong)timedOut;

            GoapMiddlewareBackend.GetQueueDepth(out int reqDepth, out int resDepth);
            metrics.QueueRequestPeak = math.max(metrics.QueueRequestPeak, (ulong)reqDepth);
            metrics.QueueResultPeak = math.max(metrics.QueueResultPeak, (ulong)resDepth);
            metrics.InFlightPeak = math.max(metrics.InFlightPeak, (ulong)CountInFlight(ref state));

            SystemAPI.SetComponent(metricsEntity, metrics);
        }

        private void SubmitRequests(
            ref SystemState state,
            in GoapMiddlewareConfig config,
            ref GoapGraphBlob graph,
            uint tick,
            double now,
            out int submitted,
            out int submitRejected,
            out int candidateAgents,
            out int needPlanAgents,
            out int missingDataAgents,
            out int cooldownAgents,
            out int noGoalAgents,
            out int noActionAgents,
            out int inFlightAgents)
        {
            submitted = 0;
            submitRejected = 0;
            candidateAgents = 0;
            needPlanAgents = 0;
            missingDataAgents = 0;
            cooldownAgents = 0;
            noGoalAgents = 0;
            noActionAgents = 0;
            inFlightAgents = 0;

            var worldStateLookup = SystemAPI.GetBufferLookup<GoapWorldStateBuffer>(true);
            var goalRequestLookup = SystemAPI.GetBufferLookup<GoapGoalRequestBuffer>(true);
            var planLookup = SystemAPI.GetBufferLookup<GoapPlanBuffer>(true);
            var actionFlagsLookup = SystemAPI.GetBufferLookup<GoapActionFlagsBuffer>(true);
            var transformLookup = SystemAPI.GetComponentLookup<LocalTransform>(true);

            foreach (var (agent, middleware, entity) in
                SystemAPI.Query<RefRW<GoapAgentComponent>, RefRW<GoapMiddlewareAgentState>>().WithEntityAccess())
            {
                candidateAgents++;

                if (submitted >= config.MaxSubmitPerFrame)
                    break;

                if (!worldStateLookup.HasBuffer(entity) ||
                    !goalRequestLookup.HasBuffer(entity) ||
                    !planLookup.HasBuffer(entity) ||
                    !actionFlagsLookup.HasBuffer(entity) ||
                    !transformLookup.HasComponent(entity))
                {
                    missingDataAgents++;
                    continue;
                }

                var worldState = worldStateLookup[entity];
                var goalRequests = goalRequestLookup[entity];
                var plan = planLookup[entity];
                var actionFlags = actionFlagsLookup[entity];
                var transform = transformLookup[entity];

                bool forceImmediateSubmit = agent.ValueRO.NeedsPlanning || plan.IsEmpty;
                bool shouldPlan = forceImmediateSubmit || (plan.IsEmpty && middleware.ValueRO.InFlight == 0);
                if (!shouldPlan)
                    continue;

                needPlanAgents++;

                if (middleware.ValueRO.InFlight != 0)
                {
                    inFlightAgents++;
                    continue;
                }

                if (!forceImmediateSubmit && now < middleware.ValueRO.NextAllowedSubmitTime)
                {
                    double remaining = middleware.ValueRO.NextAllowedSubmitTime - now;
                    // 模式切换或旧存档可能带来脏冷却值，这里做一次兜底修正
                    if (!math.isfinite((float)remaining) || remaining > math.max(5f, config.ReplanIntervalSeconds * 4f))
                    {
                        middleware.ValueRW.NextAllowedSubmitTime = now;
                    }
                    else
                    {
                        cooldownAgents++;
                        continue;
                    }
                }

                if (!forceImmediateSubmit && now < middleware.ValueRO.NextAllowedSubmitTime)
                {
                    cooldownAgents++;
                    continue;
                }

                int winnerGoalId = SelectWinnerGoal(agent.ValueRO.CurrentGoalId, goalRequests);
                agent.ValueRW.CurrentGoalId = winnerGoalId;

                if (winnerGoalId == -1)
                {
                    // 当前没有可追求目标，不提交请求，避免失败风暴
                    middleware.ValueRW.NextAllowedSubmitTime = now + config.ReplanIntervalSeconds;
                    agent.ValueRW.NeedsPlanning = true;
                    noGoalAgents++;
                    continue;
                }

                ulong enabledBits = BuildEnabledBits(actionFlags, graph.Nodes.Length);
                ulong executableBits = BuildExecutableBits(actionFlags, graph.Nodes.Length);
                if ((enabledBits | executableBits) == 0UL)
                {
                    // 当前动作集合不可用，不提交请求，等待状态变化后再重试
                    middleware.ValueRW.NextAllowedSubmitTime = now + config.FailureBackoffSeconds;
                    agent.ValueRW.NeedsPlanning = true;
                    noActionAgents++;
                    continue;
                }

                EnsureRuntimeAgentId(ref middleware.ValueRW, entity);

                var req = new GoapPlanRequest
                {
                    AgentId = middleware.ValueRO.RuntimeAgentId,
                    CurrentGoalId = winnerGoalId,
                    PositionX = transform.Position.x,
                    PositionY = transform.Position.y,
                    PositionZ = transform.Position.z,
                    WorldStateBits = BuildWorldBits(worldState),
                    EnabledActionBits = enabledBits,
                    ExecutableActionBits = executableBits,
                    Tick = tick,
                };

                if (!GoapMiddlewareBackend.Submit(in req))
                {
                    submitRejected++;
                    continue;
                }

                middleware.ValueRW.InFlight = 1;
                middleware.ValueRW.LastSubmitTime = now;
                middleware.ValueRW.NextAllowedSubmitTime = now + config.ReplanIntervalSeconds;
                middleware.ValueRW.LastSubmitGoalId = winnerGoalId;
                middleware.ValueRW.LastSubmitWorldBits = req.WorldStateBits;
                middleware.ValueRW.LastSubmitEnabledBits = req.EnabledActionBits;
                middleware.ValueRW.LastSubmitExecutableBits = req.ExecutableActionBits;

                // 交由中间件接管本次规划，先清掉标记，避免旧 Planner 重复计算
                agent.ValueRW.NeedsPlanning = false;
                submitted++;
            }
        }

        private static int SelectWinnerGoal(int fallbackGoalId, DynamicBuffer<GoapGoalRequestBuffer> goalRequests)
        {
            int winnerGoalId = fallbackGoalId;
            float winnerPriority = float.MinValue;

            for (int i = 0; i < goalRequests.Length; i++)
            {
                var req = goalRequests[i];
                if (req.Priority > winnerPriority ||
                    (req.Priority == winnerPriority && req.GoalId < winnerGoalId))
                {
                    winnerPriority = req.Priority;
                    winnerGoalId = req.GoalId;
                }
            }

            return winnerGoalId;
        }

        private void ApplyResults(
            ref SystemState state,
            in GoapMiddlewareConfig config,
            ref GoapGraphBlob graph,
            double now,
            out int polled,
            out int applied,
            out int failed,
            out int failedNoPlan,
            out int failedInternal,
            out int failedTimeout,
            out int failedZeroStep,
            out bool hasFailSample,
            out uint sampleAgentId,
            out int sampleGoalId,
            out ulong sampleWorldBits,
            out ulong sampleEnabledBits,
            out ulong sampleExecutableBits,
            out GoapPlanStatus sampleStatus,
            out byte sampleStepCount)
        {
            var resultBuffer = new GoapPlanResult[math.max(1, config.MaxPollPerFrame)];
            int resultCount = GoapMiddlewareBackend.Poll(resultBuffer, config.MaxPollPerFrame);
            polled = resultCount;
            applied = 0;
            failed = 0;
            failedNoPlan = 0;
            failedInternal = 0;
            failedTimeout = 0;
            failedZeroStep = 0;
            hasFailSample = false;
            sampleAgentId = 0;
            sampleGoalId = -1;
            sampleWorldBits = 0;
            sampleEnabledBits = 0;
            sampleExecutableBits = 0;
            sampleStatus = GoapPlanStatus.NoPlan;
            sampleStepCount = 0;
            if (resultCount <= 0)
                return;

            var resultMap = new NativeHashMap<uint, GoapPlanResult>(resultCount, Allocator.Temp);
            for (int i = 0; i < resultCount; i++)
            {
                resultMap[resultBuffer[i].AgentId] = resultBuffer[i];
            }

            foreach (var (agent, middleware, plan) in
                SystemAPI.Query<RefRW<GoapAgentComponent>, RefRW<GoapMiddlewareAgentState>, DynamicBuffer<GoapPlanBuffer>>())
            {
                if (middleware.ValueRO.InFlight == 0)
                    continue;

                if (!resultMap.TryGetValue(middleware.ValueRO.RuntimeAgentId, out var result))
                    continue;

                middleware.ValueRW.InFlight = 0;

                if (result.Status != GoapPlanStatus.Success || result.StepCount == 0)
                {
                    // 失败时快速回退，稍后重试
                    agent.ValueRW.NeedsPlanning = true;
                    middleware.ValueRW.NextAllowedSubmitTime = now + math.max(config.FailureBackoffSeconds, config.ReplanIntervalSeconds * 0.5f);
                    failed++;

                    if (result.Status == GoapPlanStatus.NoPlan) failedNoPlan++;
                    else if (result.Status == GoapPlanStatus.InternalError) failedInternal++;
                    else if (result.Status == GoapPlanStatus.Timeout) failedTimeout++;

                    if (result.StepCount == 0) failedZeroStep++;

                    if (!hasFailSample)
                    {
                        hasFailSample = true;
                        sampleAgentId = result.AgentId;
                        sampleGoalId = middleware.ValueRO.LastSubmitGoalId;
                        sampleWorldBits = middleware.ValueRO.LastSubmitWorldBits;
                        sampleEnabledBits = middleware.ValueRO.LastSubmitEnabledBits;
                        sampleExecutableBits = middleware.ValueRO.LastSubmitExecutableBits;
                        sampleStatus = result.Status;
                        sampleStepCount = result.StepCount;
                    }
                    continue;
                }

                plan.Clear();
                int maxSteps = math.min(math.min((int)result.StepCount, (int)config.MaxPlanSteps), 16);
                for (int i = 0; i < maxSteps; i++)
                {
                    int actionId = result.GetActionId(i);
                    if (actionId < 0 || actionId >= graph.Nodes.Length)
                        continue;

                    ref var node = ref graph.Nodes[actionId];
                    plan.Add(new GoapPlanBuffer
                    {
                        ActionIndex = node.Index,
                        ActionGuid = node.ActionGuid,
                    });
                }

                agent.ValueRW.CurrentActionIndex = 0;
                agent.ValueRW.NeedsPlanning = false;
                applied++;
            }

            resultMap.Dispose();
        }

        private int HandleTimeouts(ref SystemState state, in GoapMiddlewareConfig config, double now)
        {
            int count = 0;
            foreach (var (agent, middleware) in SystemAPI.Query<RefRW<GoapAgentComponent>, RefRW<GoapMiddlewareAgentState>>())
            {
                if (middleware.ValueRO.InFlight == 0)
                    continue;

                if ((now - middleware.ValueRO.LastSubmitTime) < config.RequestTimeoutSeconds)
                    continue;

                middleware.ValueRW.InFlight = 0;
                middleware.ValueRW.NextAllowedSubmitTime = now + config.FailureBackoffSeconds;
                agent.ValueRW.NeedsPlanning = true;
                count++;
            }

            return count;
        }

        private int CountInFlight(ref SystemState state)
        {
            int count = 0;
            foreach (var middleware in SystemAPI.Query<RefRO<GoapMiddlewareAgentState>>())
            {
                if (middleware.ValueRO.InFlight != 0)
                    count++;
            }

            return count;
        }

        private static void EnsureRuntimeAgentId(ref GoapMiddlewareAgentState state, Entity entity)
        {
            if (state.RuntimeAgentId != 0)
                return;

            // 运行时 ID：Entity.Index + Entity.Version 组合，避免哈希碰撞
            state.RuntimeAgentId = ((uint)entity.Version << 20) | ((uint)entity.Index & 0x000FFFFFu);
            if (state.RuntimeAgentId == 0)
                state.RuntimeAgentId = 1;
        }

        private ulong BuildWorldBits(DynamicBuffer<GoapWorldStateBuffer> worldState)
        {
            ulong bits = 0UL;
            for (int i = 0; i < worldState.Length; i++)
            {
                if (worldState[i].Value <= 0)
                    continue;

                int key = worldState[i].KeyId;
                if (key == _keyHasTarget) bits |= 1UL << 0;
                else if (key == _keyHasAmmo) bits |= 1UL << 1;
                else if (key == _keyLowHealth) bits |= 1UL << 2;
                else if (key == _keyDamaged) bits |= 1UL << 3;
                else if (key == _keyUnderAttack) bits |= 1UL << 4;
                else if (key == _keyInCover) bits |= 1UL << 5;
                else if (key == _keyAtPoint) bits |= 1UL << 6;
                else if (key == _keyPointCaptured) bits |= 1UL << 7;
                else if (key == _keyAllyInjured) bits |= 1UL << 8;
                else if (key == _keyPatrolling) bits |= 1UL << 9;
            }

            return bits;
        }

        private static ulong BuildEnabledBits(DynamicBuffer<GoapActionFlagsBuffer> actionFlags, int nodeCount)
        {
            ulong bits = 0UL;
            int limit = math.min(nodeCount, 64);
            for (int i = 0; i < limit; i++)
                bits |= 1UL << i;

            for (int i = 0; i < actionFlags.Length; i++)
            {
                int idx = actionFlags[i].ActionIndex;
                if (idx < 0 || idx >= 64)
                    continue;

                ulong mask = 1UL << idx;
                if (actionFlags[i].IsEnabled)
                    bits |= mask;
                else
                    bits &= ~mask;
            }

            return bits;
        }

        private static ulong BuildExecutableBits(DynamicBuffer<GoapActionFlagsBuffer> actionFlags, int nodeCount)
        {
            ulong bits = 0UL;
            int limit = math.min(nodeCount, 64);
            for (int i = 0; i < actionFlags.Length; i++)
            {
                int idx = actionFlags[i].ActionIndex;
                if (idx < 0 || idx >= limit)
                    continue;

                if (actionFlags[i].IsExecutable)
                    bits |= 1UL << idx;
            }

            return bits;
        }

        private static void EnsureSingletons(ref SystemState state)
        {
            var entityManager = state.EntityManager;
            using var configQuery = entityManager.CreateEntityQuery(ComponentType.ReadOnly<GoapMiddlewareConfig>());
            if (configQuery.IsEmptyIgnoreFilter)
            {
                var e = entityManager.CreateEntity();
                entityManager.AddComponentData(e, GoapMiddlewareConfig.CreateDefault());
            }
            else
            {
                DeduplicateConfigEntities(entityManager, configQuery);
            }

            using var runtimeQuery = entityManager.CreateEntityQuery(ComponentType.ReadOnly<GoapMiddlewareRuntimeState>());
            if (runtimeQuery.IsEmptyIgnoreFilter)
            {
                var e = entityManager.CreateEntity();
                entityManager.AddComponentData(e, new GoapMiddlewareRuntimeState { Tick = 0, UseMiddlewarePipeline = 0 });
            }
            else
            {
                DeduplicateEntities(entityManager, runtimeQuery);
            }

            using var metricsQuery = entityManager.CreateEntityQuery(ComponentType.ReadOnly<GoapMiddlewareMetrics>());
            if (metricsQuery.IsEmptyIgnoreFilter)
            {
                var e = entityManager.CreateEntity();
                entityManager.AddComponentData(e, new GoapMiddlewareMetrics());
            }
            else
            {
                DeduplicateEntities(entityManager, metricsQuery);
            }

            using var metricsRtQuery = entityManager.CreateEntityQuery(ComponentType.ReadOnly<GoapMiddlewareMetricsRuntime>());
            if (metricsRtQuery.IsEmptyIgnoreFilter)
            {
                var e = entityManager.CreateEntity();
                entityManager.AddComponentData(e, new GoapMiddlewareMetricsRuntime { NextLogTime = 0 });
            }
            else
            {
                DeduplicateEntities(entityManager, metricsRtQuery);
            }
        }

        private static void EnsureAgentMiddlewareState(ref SystemState state)
        {
            var entityManager = state.EntityManager;
            using var missingQuery = entityManager.CreateEntityQuery(
                ComponentType.ReadOnly<GoapAgentComponent>(),
                ComponentType.Exclude<GoapMiddlewareAgentState>());

            if (missingQuery.IsEmptyIgnoreFilter)
                return;

            using var entities = missingQuery.ToEntityArray(Allocator.Temp);
            for (int i = 0; i < entities.Length; i++)
            {
                var e = entities[i];
                if (!entityManager.Exists(e))
                    continue;

                entityManager.AddComponentData(e, new GoapMiddlewareAgentState
                {
                    RuntimeAgentId = 0,
                    InFlight = 0,
                    LastSubmitTime = -1,
                    NextAllowedSubmitTime = 0,
                    LastSubmitGoalId = -1,
                    LastSubmitWorldBits = 0,
                    LastSubmitEnabledBits = 0,
                    LastSubmitExecutableBits = 0,
                });
            }
        }

        private void ResetAllAgentMiddlewareState(ref SystemState state)
        {
            foreach (var middleware in SystemAPI.Query<RefRW<GoapMiddlewareAgentState>>())
            {
                middleware.ValueRW.InFlight = 0;
                middleware.ValueRW.LastSubmitTime = -1;
                middleware.ValueRW.NextAllowedSubmitTime = 0;
                middleware.ValueRW.LastSubmitGoalId = -1;
                middleware.ValueRW.LastSubmitWorldBits = 0;
                middleware.ValueRW.LastSubmitEnabledBits = 0;
                middleware.ValueRW.LastSubmitExecutableBits = 0;
            }
        }

        private static bool TryGetConfig(ref SystemState state, out GoapMiddlewareConfig config)
        {
            var entityManager = state.EntityManager;
            using var configQuery = entityManager.CreateEntityQuery(ComponentType.ReadOnly<GoapMiddlewareConfig>());
            using var entities = configQuery.ToEntityArray(Allocator.Temp);

            if (entities.Length == 0)
            {
                config = default;
                return false;
            }

            Entity selected = entities[0];
            for (int i = 0; i < entities.Length; i++)
            {
                if (entityManager.HasComponent<GoapMiddlewareConfigAuthoringTag>(entities[i]))
                {
                    selected = entities[i];
                    break;
                }
            }

            config = entityManager.GetComponentData<GoapMiddlewareConfig>(selected);
            return true;
        }

        private static void DeduplicateConfigEntities(EntityManager entityManager, EntityQuery query)
        {
            using var entities = query.ToEntityArray(Allocator.Temp);
            if (entities.Length <= 1)
                return;

            int keepIndex = 0;
            for (int i = 0; i < entities.Length; i++)
            {
                if (entityManager.HasComponent<GoapMiddlewareConfigAuthoringTag>(entities[i]))
                {
                    keepIndex = i;
                    break;
                }
            }

            for (int i = 0; i < entities.Length; i++)
            {
                if (i == keepIndex)
                    continue;

                if (entityManager.Exists(entities[i]))
                    entityManager.DestroyEntity(entities[i]);
            }
        }

        private static void DeduplicateEntities(EntityManager entityManager, EntityQuery query)
        {
            using var entities = query.ToEntityArray(Allocator.Temp);
            if (entities.Length <= 1)
                return;

            for (int i = 1; i < entities.Length; i++)
            {
                if (entityManager.Exists(entities[i]))
                    entityManager.DestroyEntity(entities[i]);
            }
        }
    }
}
