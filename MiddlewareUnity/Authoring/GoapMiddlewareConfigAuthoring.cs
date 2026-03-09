using Unity.Entities;
using UnityEngine;

namespace SeventhSequence.ECS.GOAP.Authoring
{
    /// <summary>
    /// 场景配置入口
    /// 用于切换 GOAP 规划管线与中间件预算参数
    /// 建议场景中仅保留一个实例
    /// </summary>
    public class GoapMiddlewareConfigAuthoring : MonoBehaviour
    {
        [Header("Switch")]
        public bool Enabled = true;
        public GoapPlanningPipelineMode PlanningPipeline = GoapPlanningPipelineMode.LegacyPlanner;
        public GoapMiddlewareBackendMode BackendMode = GoapMiddlewareBackendMode.Auto;

        [Header("Per-Frame Budget")]
        public int MaxSubmitPerFrame = 20;
        public int MaxProcessPerFrame = 20;
        public int MaxPollPerFrame = 20;

        [Header("Timing")]
        public float ReplanIntervalSeconds = 2f;
        public float RequestTimeoutSeconds = 0.25f;
        public float FailureBackoffSeconds = 0.5f;

        [Header("Search")]
        [Range(1, 16)] public int MaxPlanSteps = 16;
        public int SearchMaxExpansions = 64;
        public int SearchMaxStates = 96;

        [Header("Metrics")]
        public bool MetricsLogEnabled = true;
        public float MetricsLogIntervalSeconds = 2f;
    }

    /// <summary>
    /// Authoring 到 ECS 组件的烘焙器
    /// </summary>
    public class GoapMiddlewareConfigBaker : Baker<GoapMiddlewareConfigAuthoring>
    {
        /// <summary>
        /// 将 Inspector 配置写入 GoapMiddlewareConfig 组件
        /// </summary>
        public override void Bake(GoapMiddlewareConfigAuthoring authoring)
        {
            var entity = GetEntity(TransformUsageFlags.None);

            AddComponent(entity, new GoapMiddlewareConfig
            {
                Enabled = authoring.Enabled,
                PlanningPipeline = authoring.PlanningPipeline,
                BackendMode = authoring.BackendMode,
                MaxSubmitPerFrame = Mathf.Max(1, authoring.MaxSubmitPerFrame),
                MaxProcessPerFrame = Mathf.Max(1, authoring.MaxProcessPerFrame),
                MaxPollPerFrame = Mathf.Max(1, authoring.MaxPollPerFrame),
                ReplanIntervalSeconds = Mathf.Max(0f, authoring.ReplanIntervalSeconds),
                RequestTimeoutSeconds = Mathf.Max(0.01f, authoring.RequestTimeoutSeconds),
                FailureBackoffSeconds = Mathf.Max(0f, authoring.FailureBackoffSeconds),
                MaxPlanSteps = Mathf.Clamp(authoring.MaxPlanSteps, 1, 16),
                SearchMaxExpansions = Mathf.Max(8, authoring.SearchMaxExpansions),
                SearchMaxStates = Mathf.Max(16, authoring.SearchMaxStates),
                MetricsLogEnabled = authoring.MetricsLogEnabled,
                MetricsLogIntervalSeconds = Mathf.Max(0.5f, authoring.MetricsLogIntervalSeconds),
            });

            AddComponent<GoapMiddlewareConfigAuthoringTag>(entity);
        }
    }
}
