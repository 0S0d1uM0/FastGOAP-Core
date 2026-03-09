using System;
using System.Runtime.InteropServices;

namespace SeventhSequence.ECS.GOAP
{
    public enum GoapPlanStatus : uint
    {
        Success = 0,
        Timeout = 1,
        NoPlan = 2,
        InternalError = 3,
    }

    /// <summary>
    /// 引擎无关的中间件初始化参数
    /// 不依赖 Unity ECS 类型 可被任意桥接层复用
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct GoapNativeConfig
    {
        public uint Version;
        public uint WorkerThreads;
        public uint MaxAgents;
        public uint MaxQueuedRequests;
        public uint MaxPlanSteps;
        public uint SearchMaxExpansions;
        public uint SearchMaxStates;
        public uint Reserved0;
    }

    /// <summary>
    /// 静态图头信息
    /// 用于上传 Action 与 Goal 规则到原生中间件
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct GoapNativeGraphHeader
    {
        public uint Version;
        public uint ActionCount;
        public uint GoalCount;
        public uint WorldBitWidth;
    }

    /// <summary>
    /// 动作规则 位图语义
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct GoapNativeActionRule
    {
        public ushort ActionId;
        public ushort Reserved;
        public float BaseCost;
        public ulong RequireTrueBits;
        public ulong RequireFalseBits;
        public ulong SetBits;
        public ulong ClearBits;
    }

    /// <summary>
    /// 目标规则 位图语义
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct GoapNativeGoalRule
    {
        public int GoalId;
        public uint Reserved;
        public ulong RequireTrueBits;
        public ulong RequireFalseBits;
    }

    /// <summary>
    /// 发往中间件的请求包
    /// 使用顺序布局 便于对接 C++ DLL
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct GoapPlanRequest
    {
        public uint AgentId;
        public int CurrentGoalId;
        public float PositionX;
        public float PositionY;
        public float PositionZ;

        // 64 位世界状态位图 可按需扩展到更高位宽
        public ulong WorldStateBits;

        // 动作能力位图 1 表示启用或可执行 bit i 对应 ActionIndex i
        public ulong EnabledActionBits;
        public ulong ExecutableActionBits;

        public uint Tick;
    }

    /// <summary>
    /// 中间件返回包
    /// 使用固定 16 槽动作 ID 以避免热路径动态分配
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct GoapPlanResult
    {
        public uint AgentId;
        public uint Tick;
        public GoapPlanStatus Status;
        public byte StepCount;

        public ushort ActionId0;
        public ushort ActionId1;
        public ushort ActionId2;
        public ushort ActionId3;
        public ushort ActionId4;
        public ushort ActionId5;
        public ushort ActionId6;
        public ushort ActionId7;
        public ushort ActionId8;
        public ushort ActionId9;
        public ushort ActionId10;
        public ushort ActionId11;
        public ushort ActionId12;
        public ushort ActionId13;
        public ushort ActionId14;
        public ushort ActionId15;

        public ushort GetActionId(int index)
        {
            switch (index)
            {
                case 0: return ActionId0;
                case 1: return ActionId1;
                case 2: return ActionId2;
                case 3: return ActionId3;
                case 4: return ActionId4;
                case 5: return ActionId5;
                case 6: return ActionId6;
                case 7: return ActionId7;
                case 8: return ActionId8;
                case 9: return ActionId9;
                case 10: return ActionId10;
                case 11: return ActionId11;
                case 12: return ActionId12;
                case 13: return ActionId13;
                case 14: return ActionId14;
                case 15: return ActionId15;
                default: return 0;
            }
        }

        public void SetActionId(int index, ushort value)
        {
            switch (index)
            {
                case 0: ActionId0 = value; break;
                case 1: ActionId1 = value; break;
                case 2: ActionId2 = value; break;
                case 3: ActionId3 = value; break;
                case 4: ActionId4 = value; break;
                case 5: ActionId5 = value; break;
                case 6: ActionId6 = value; break;
                case 7: ActionId7 = value; break;
                case 8: ActionId8 = value; break;
                case 9: ActionId9 = value; break;
                case 10: ActionId10 = value; break;
                case 11: ActionId11 = value; break;
                case 12: ActionId12 = value; break;
                case 13: ActionId13 = value; break;
                case 14: ActionId14 = value; break;
                case 15: ActionId15 = value; break;
            }
        }
    }

    /// <summary>
    /// 原生接口签名声明
    /// 当前版本可回退到纯 C# 运行时
    /// </summary>
    public static class GoapNativeInterop
    {
        private const string DllName = "FastGoapMiddleware";

        // 旧版最小接口区域 用于向后兼容

        [DllImport(DllName, EntryPoint = "Goap_Init", CallingConvention = CallingConvention.Cdecl)]
        public static extern int Init(ref GoapMiddlewareConfig config);

        [DllImport(DllName, EntryPoint = "Goap_SubmitRequests", CallingConvention = CallingConvention.Cdecl)]
        public static extern int SubmitRequests([In] GoapPlanRequest[] requests, int count);

        [DllImport(DllName, EntryPoint = "Goap_PollResults", CallingConvention = CallingConvention.Cdecl)]
        public static extern int PollResults([Out] GoapPlanResult[] results, int maxCount);

        [DllImport(DllName, EntryPoint = "Goap_Shutdown", CallingConvention = CallingConvention.Cdecl)]
        public static extern void Shutdown();

        // 引擎无关 C ABI v1 接口区域 推荐接入方式

        [DllImport(DllName, EntryPoint = "Goap_CreateContext", CallingConvention = CallingConvention.Cdecl)]
        public static extern int CreateContext(ref GoapNativeConfig config, out ulong contextHandle);

        [DllImport(DllName, EntryPoint = "Goap_DestroyContext", CallingConvention = CallingConvention.Cdecl)]
        public static extern int DestroyContext(ulong contextHandle);

        [DllImport(DllName, EntryPoint = "Goap_UploadGraph", CallingConvention = CallingConvention.Cdecl)]
        public static extern int UploadGraph(
            ulong contextHandle,
            ref GoapNativeGraphHeader header,
            [In] GoapNativeActionRule[] actions,
            int actionCount,
            [In] GoapNativeGoalRule[] goals,
            int goalCount);

        [DllImport(DllName, EntryPoint = "Goap_SubmitRequestsV1", CallingConvention = CallingConvention.Cdecl)]
        public static extern int SubmitRequestsV1(ulong contextHandle, [In] GoapPlanRequest[] requests, int count);

        [DllImport(DllName, EntryPoint = "Goap_PollResultsV1", CallingConvention = CallingConvention.Cdecl)]
        public static extern int PollResultsV1(ulong contextHandle, [Out] GoapPlanResult[] results, int maxCount);

        [DllImport(DllName, EntryPoint = "Goap_GetLastError", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr GetLastErrorPtr(ulong contextHandle);

        public static string GetLastError(ulong contextHandle)
        {
            IntPtr ptr = GetLastErrorPtr(contextHandle);
            if (ptr == IntPtr.Zero)
                return string.Empty;

            return Marshal.PtrToStringAnsi(ptr) ?? string.Empty;
        }
    }
}
