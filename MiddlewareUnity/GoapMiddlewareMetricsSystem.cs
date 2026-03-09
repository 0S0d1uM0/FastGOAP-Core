using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using Unity.Collections;

namespace SeventhSequence.ECS.GOAP
{
    /// <summary>
    /// 按固定间隔输出中间件累计统计
    /// 用于压测时观测稳定性
    /// </summary>
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(GoapMiddlewareSchedulerSystem))]
    public partial struct GoapMiddlewareMetricsSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<GoapMiddlewareConfig>();
            state.RequireForUpdate<GoapMiddlewareMetrics>();
            state.RequireForUpdate<GoapMiddlewareMetricsRuntime>();
        }

        public void OnUpdate(ref SystemState state)
        {
            if (!TryGetConfig(ref state, out var config))
                return;

            if (!config.Enabled ||
                config.PlanningPipeline != GoapPlanningPipelineMode.Middleware ||
                !config.MetricsLogEnabled)
                return;

            var runtimeEntity = SystemAPI.GetSingletonEntity<GoapMiddlewareMetricsRuntime>();
            var runtime = SystemAPI.GetComponent<GoapMiddlewareMetricsRuntime>(runtimeEntity);
            double now = SystemAPI.Time.ElapsedTime;
            if (now < runtime.NextLogTime)
                return;

            runtime.NextLogTime = now + math.max(0.5f, config.MetricsLogIntervalSeconds);
            SystemAPI.SetComponent(runtimeEntity, runtime);

            var m = SystemAPI.GetSingleton<GoapMiddlewareMetrics>();
            string backend = GoapMiddlewareBackend.IsUsingNative ? "Native" : "Managed";
            Debug.Log(
                "[GOAP-MW] " +
                "Backend=" + backend +
                "Submitted=" + m.Submitted +
                " Rejected=" + m.SubmitRejected +
                " Processed=" + m.Processed +
                " Polled=" + m.Polled +
                " Applied=" + m.Applied +
                " Failed=" + m.Failed +
                " TimedOut=" + m.TimedOut +
                " PeakReqQ=" + m.QueueRequestPeak +
                " PeakResQ=" + m.QueueResultPeak +
                " PeakInFlight=" + m.InFlightPeak);
        }

        private static bool TryGetConfig(ref SystemState state, out GoapMiddlewareConfig config)
        {
            var entityManager = state.EntityManager;
            using var query = entityManager.CreateEntityQuery(ComponentType.ReadOnly<GoapMiddlewareConfig>());
            using var entities = query.ToEntityArray(Allocator.Temp);
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
    }
}
