using Unity.Entities;

namespace SeventhSequence.ECS.GOAP
{
    /// <summary>
    /// GOAP 中间件调度配置
    /// 目标是把规划开销限制在固定预算内，避免影响主线程帧率
    /// </summary>
    public struct GoapMiddlewareConfig : IComponentData
    {
        public bool Enabled;

        /// <summary>规划管线选择：LegacyPlanner（原生 ECS Planner）/ Middleware（中间件调度器）</summary>
        public GoapPlanningPipelineMode PlanningPipeline;

        /// <summary>计算后端：Managed / Native / Auto</summary>
        public GoapMiddlewareBackendMode BackendMode;

        /// <summary>每帧最多提交多少个单位进入规划队列</summary>
        public int MaxSubmitPerFrame;

        /// <summary>每帧最多处理多少个规划任务（运行时内部预算）</summary>
        public int MaxProcessPerFrame;

        /// <summary>每帧最多读取多少个规划结果并应用到单位</summary>
        public int MaxPollPerFrame;

        /// <summary>同一单位最小重规划间隔（秒）</summary>
        public float ReplanIntervalSeconds;

        /// <summary>单次请求超时时间（秒），超时后回退并重试</summary>
        public float RequestTimeoutSeconds;

        /// <summary>请求失败后的退避时间（秒）</summary>
        public float FailureBackoffSeconds;

        /// <summary>结果计划最大步数和 PlanResult 的 16 槽保持一致或更小</summary>
        public int MaxPlanSteps;

        /// <summary>单次规划最大状态展开数（硬上限，防卡死）</summary>
        public int SearchMaxExpansions;

        /// <summary>单次规划最大状态节点数（硬上限，防内存膨胀）</summary>
        public int SearchMaxStates;

        /// <summary>是否输出中间件性能统计日志</summary>
        public bool MetricsLogEnabled;

        /// <summary>统计日志输出间隔（秒）</summary>
        public float MetricsLogIntervalSeconds;

        public static GoapMiddlewareConfig CreateDefault()
        {
            return new GoapMiddlewareConfig
            {
                Enabled = true,
                PlanningPipeline = GoapPlanningPipelineMode.LegacyPlanner,
                BackendMode = GoapMiddlewareBackendMode.Auto,
                MaxSubmitPerFrame = 20,
                MaxProcessPerFrame = 20,
                MaxPollPerFrame = 20,
                ReplanIntervalSeconds = 2f,
                RequestTimeoutSeconds = 0.25f,
                FailureBackoffSeconds = 0.5f,
                MaxPlanSteps = 16,
                SearchMaxExpansions = 64,
                SearchMaxStates = 96,
                MetricsLogEnabled = true,
                MetricsLogIntervalSeconds = 2f,
            };
        }
    }

    /// <summary>
    /// 每个 Agent 的中间件运行时状态
    /// </summary>
    public struct GoapMiddlewareAgentState : IComponentData
    {
        public uint RuntimeAgentId;
        public byte InFlight;
        public double LastSubmitTime;
        public double NextAllowedSubmitTime;
        public int LastSubmitGoalId;
        public ulong LastSubmitWorldBits;
        public ulong LastSubmitEnabledBits;
        public ulong LastSubmitExecutableBits;
    }

    /// <summary>
    /// 中间件全局运行时状态（tick 计数等）
    /// </summary>
    public struct GoapMiddlewareRuntimeState : IComponentData
    {
        public uint Tick;
        public byte UseMiddlewarePipeline;
    }

    /// <summary>
    /// 中间件累计统计
    /// </summary>
    public struct GoapMiddlewareMetrics : IComponentData
    {
        public ulong Submitted;
        public ulong SubmitRejected;
        public ulong Processed;
        public ulong Polled;
        public ulong Applied;
        public ulong Failed;
        public ulong TimedOut;
        public ulong QueueRequestPeak;
        public ulong QueueResultPeak;
        public ulong InFlightPeak;
    }

    /// <summary>
    /// 中间件统计日志节流状态
    /// </summary>
    public struct GoapMiddlewareMetricsRuntime : IComponentData
    {
        public double NextLogTime;
    }

    /// <summary>
    /// 标记由 Authoring 烘焙出的配置实体，运行时优先采用该配置
    /// </summary>
    public struct GoapMiddlewareConfigAuthoringTag : IComponentData
    {
    }

    /// <summary>
    /// GOAP 规划管线选择
    /// </summary>
    public enum GoapPlanningPipelineMode : byte
    {
        LegacyPlanner = 0,
        Middleware = 1,
    }
}
