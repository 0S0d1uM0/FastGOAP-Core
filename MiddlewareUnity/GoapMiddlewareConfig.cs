using Unity.Entities;

namespace SeventhSequence.ECS.GOAP
{
    /// <summary>
    /// GOAP 中间件调度配置
    /// 用于控制规划预算 时序 与统计输出
    /// </summary>
    public struct GoapMiddlewareConfig : IComponentData
    {
        public bool Enabled;

        /// <summary>规划管线选择 LegacyPlanner 或 Middleware</summary>
        public GoapPlanningPipelineMode PlanningPipeline;

        /// <summary>计算后端选择 Managed Native 或 Auto</summary>
        public GoapMiddlewareBackendMode BackendMode;

        /// <summary>每帧提交到规划队列的最大单位数</summary>
        public int MaxSubmitPerFrame;

        /// <summary>每帧处理规划请求的内部预算上限</summary>
        public int MaxProcessPerFrame;

        /// <summary>每帧轮询并应用规划结果的最大数量</summary>
        public int MaxPollPerFrame;

        /// <summary>同一单位两次重规划的最小间隔 秒</summary>
        public float ReplanIntervalSeconds;

        /// <summary>单次请求超时时间 秒 超时后执行回退重试</summary>
        public float RequestTimeoutSeconds;

        /// <summary>请求失败后的退避时长 秒</summary>
        public float FailureBackoffSeconds;

        /// <summary>结果计划最大步数 需不大于 PlanResult 的 16 槽</summary>
        public int MaxPlanSteps;

        /// <summary>单次规划的最大状态展开数 硬上限</summary>
        public int SearchMaxExpansions;

        /// <summary>单次规划的最大状态节点数 硬上限</summary>
        public int SearchMaxStates;

        /// <summary>是否输出中间件性能统计日志</summary>
        public bool MetricsLogEnabled;

        /// <summary>统计日志输出间隔 秒</summary>
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
    /// 中间件全局运行时状态 如 Tick 计数
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
    /// 标记由 Authoring 烘焙的配置实体 运行时优先使用
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
