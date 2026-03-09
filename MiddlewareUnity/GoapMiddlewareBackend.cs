using System;
using CrashKonijn.Goap.Core;
using UnityEngine;

namespace SeventhSequence.ECS.GOAP
{
    public enum GoapMiddlewareBackendMode : byte
    {
        Managed = 0,
        Native = 1,
        Auto = 2,
    }

    /// <summary>
    /// 中间件后端桥接层
    /// 自动模式优先原生 不可用时回退托管实现
    /// </summary>
    public static class GoapMiddlewareBackend
    {
        private static bool s_Initialized;
        private static bool s_UseNative;
        private static ulong s_NativeContext;

        private static int s_LastActionCount = -1;
        private static int s_LastGoalCount = -1;

        public static bool IsUsingNative => s_UseNative;

        public static void Reset()
        {
            if (s_UseNative && s_NativeContext != 0)
            {
                try
                {
                    GoapNativeInterop.DestroyContext(s_NativeContext);
                }
                catch
                {
                    // 销毁阶段异常忽略以避免影响退出
                }
            }

            s_Initialized = false;
            s_UseNative = false;
            s_NativeContext = 0;
            s_LastActionCount = -1;
            s_LastGoalCount = -1;
            GoapManagedRuntime.Reset();
        }

        public static void EnsureInitialized(in GoapMiddlewareConfig config)
        {
            if (s_Initialized)
                return;

            s_Initialized = true;
            s_UseNative = false;

            if (config.BackendMode == GoapMiddlewareBackendMode.Managed)
                return;

            var nativeConfig = new GoapNativeConfig
            {
                Version = 1,
                WorkerThreads = 0,
                MaxAgents = 4096,
                MaxQueuedRequests = 8192,
                MaxPlanSteps = (uint)Mathf.Clamp(config.MaxPlanSteps, 1, 16),
                SearchMaxExpansions = (uint)Mathf.Clamp(config.SearchMaxExpansions, 8, 4096),
                SearchMaxStates = (uint)Mathf.Clamp(config.SearchMaxStates, 16, 4096),
                Reserved0 = 0,
            };

            try
            {
                int rc = GoapNativeInterop.CreateContext(ref nativeConfig, out s_NativeContext);
                if (rc == 0 && s_NativeContext != 0)
                {
                    s_UseNative = true;
                    return;
                }
            }
            catch (DllNotFoundException)
            {
                // 未找到原生插件
            }
            catch (EntryPointNotFoundException)
            {
                // 原生插件缺少 ABI v1 入口
            }
            catch
            {
                // 原生初始化异常时回退到托管后端
            }

            if (config.BackendMode == GoapMiddlewareBackendMode.Native)
            {
                Debug.LogWarning("[GOAP-MW] Native backend requested but unavailable. Falling back to managed backend.");
            }
        }

        public static void SetPlannerBudget(int maxExpansions, int maxStates)
        {
            if (!s_UseNative)
                GoapManagedRuntime.SetPlannerBudget(maxExpansions, maxStates);
        }

        public static void ConfigureGraph(ref GoapGraphBlob graph)
        {
            int actionCount = graph.Nodes.Length;
            int goalCount = graph.Goals.Length;

            if (!s_UseNative)
            {
                GoapManagedRuntime.ConfigureGraph(ref graph);
                return;
            }

            if (s_LastActionCount == actionCount && s_LastGoalCount == goalCount)
                return;

            var header = new GoapNativeGraphHeader
            {
                Version = 1,
                ActionCount = (uint)actionCount,
                GoalCount = (uint)goalCount,
                WorldBitWidth = 64,
            };

            var actions = new GoapNativeActionRule[actionCount];
            for (int i = 0; i < actionCount; i++)
            {
                ref var node = ref graph.Nodes[i];
                ulong reqTrue = 0UL;
                ulong reqFalse = 0UL;
                ulong setBits = 0UL;
                ulong clearBits = 0UL;

                for (int c = 0; c < node.Conditions.Length; c++)
                {
                    ref var cond = ref node.Conditions[c];
                    if (!TryKeyIdToBit(cond.ConditionId, out int bit))
                        continue;

                    if (IsTrueRequirement(cond.Comparison, cond.Value))
                        reqTrue |= 1UL << bit;
                    else if (IsFalseRequirement(cond.Comparison, cond.Value))
                        reqFalse |= 1UL << bit;
                }

                for (int e = 0; e < node.Effects.Length; e++)
                {
                    ref var eff = ref node.Effects[e];
                    if (!TryKeyIdToBit(eff.ConditionId, out int bit))
                        continue;

                    ulong mask = 1UL << bit;
                    if (eff.Type == EffectType.Increase)
                        setBits |= mask;
                    else if (eff.Type == EffectType.Decrease)
                        clearBits |= mask;
                }

                actions[i] = new GoapNativeActionRule
                {
                    ActionId = (ushort)i,
                    Reserved = 0,
                    BaseCost = Mathf.Max(0.01f, node.BaseCost),
                    RequireTrueBits = reqTrue,
                    RequireFalseBits = reqFalse,
                    SetBits = setBits,
                    ClearBits = clearBits,
                };
            }

            var goals = new GoapNativeGoalRule[goalCount];
            for (int g = 0; g < goalCount; g++)
            {
                ref var goal = ref graph.Goals[g];
                ulong reqTrue = 0UL;
                ulong reqFalse = 0UL;

                for (int c = 0; c < goal.Conditions.Length; c++)
                {
                    ref var cond = ref goal.Conditions[c];
                    if (!TryKeyIdToBit(cond.ConditionId, out int bit))
                        continue;

                    if (IsTrueRequirement(cond.Comparison, cond.Value))
                        reqTrue |= 1UL << bit;
                    else if (IsFalseRequirement(cond.Comparison, cond.Value))
                        reqFalse |= 1UL << bit;
                }

                goals[g] = new GoapNativeGoalRule
                {
                    GoalId = goal.GoalId,
                    Reserved = 0,
                    RequireTrueBits = reqTrue,
                    RequireFalseBits = reqFalse,
                };
            }

            try
            {
                int rc = GoapNativeInterop.UploadGraph(
                    s_NativeContext,
                    ref header,
                    actions,
                    actions.Length,
                    goals,
                    goals.Length);

                if (rc == 0)
                {
                    s_LastActionCount = actionCount;
                    s_LastGoalCount = goalCount;
                    return;
                }

                Debug.LogWarning("[GOAP-MW] Native UploadGraph failed, fallback to managed backend.");
                SwitchToManagedAndConfigure(ref graph);
            }
            catch
            {
                SwitchToManagedAndConfigure(ref graph);
            }
        }

        public static bool Submit(in GoapPlanRequest request)
        {
            if (!s_UseNative)
                return GoapManagedRuntime.Submit(in request);

            try
            {
                var reqs = new[] { request };
                int rc = GoapNativeInterop.SubmitRequestsV1(s_NativeContext, reqs, 1);
                return rc == 0;
            }
            catch
            {
                return false;
            }
        }

        public static int Process(int maxProcessPerFrame)
        {
            if (!s_UseNative)
                return GoapManagedRuntime.Process(maxProcessPerFrame);

            // 原生后端由内部线程推进 此处无需额外处理
            return 0;
        }

        public static int Poll(GoapPlanResult[] output, int maxCount)
        {
            if (!s_UseNative)
                return GoapManagedRuntime.Poll(output, maxCount);

            try
            {
                int rc = GoapNativeInterop.PollResultsV1(s_NativeContext, output, maxCount);
                if (rc < 0)
                    return 0;

                return rc;
            }
            catch
            {
                return 0;
            }
        }

        public static void GetQueueDepth(out int requestCount, out int resultCount)
        {
            if (!s_UseNative)
            {
                GoapManagedRuntime.GetQueueDepth(out requestCount, out resultCount);
                return;
            }

            // 当前 ABI 版本未提供原生队列深度
            requestCount = 0;
            resultCount = 0;
        }

        private static void SwitchToManagedAndConfigure(ref GoapGraphBlob graph)
        {
            s_UseNative = false;
            s_NativeContext = 0;
            s_LastActionCount = -1;
            s_LastGoalCount = -1;
            GoapManagedRuntime.ConfigureGraph(ref graph);
        }

        private static bool IsTrueRequirement(Comparison comparison, int value)
        {
            if (value <= 0)
                return false;

            return comparison == Comparison.GreaterThan ||
                   comparison == Comparison.GreaterThanOrEqual;
        }

        private static bool IsFalseRequirement(Comparison comparison, int value)
        {
            if (value > 0)
                return false;

            return comparison == Comparison.SmallerThan ||
                   comparison == Comparison.SmallerThanOrEqual;
        }

        private static bool TryKeyIdToBit(int keyId, out int bit)
        {
            if (keyId == GoapUtils.GetKeyId(GoapKnownNames.KeyHasTarget)) { bit = 0; return true; }
            if (keyId == GoapUtils.GetKeyId(GoapKnownNames.KeyHasAmmo)) { bit = 1; return true; }
            if (keyId == GoapUtils.GetKeyId(GoapKnownNames.KeyLowHealth)) { bit = 2; return true; }
            if (keyId == GoapUtils.GetKeyId(GoapKnownNames.KeyDamaged)) { bit = 3; return true; }
            if (keyId == GoapUtils.GetKeyId(GoapKnownNames.KeyUnderAttack)) { bit = 4; return true; }
            if (keyId == GoapUtils.GetKeyId(GoapKnownNames.KeyInCover)) { bit = 5; return true; }
            if (keyId == GoapUtils.GetKeyId(GoapKnownNames.KeyAtPoint)) { bit = 6; return true; }
            if (keyId == GoapUtils.GetKeyId(GoapKnownNames.KeyPointCaptured)) { bit = 7; return true; }
            if (keyId == GoapUtils.GetKeyId(GoapKnownNames.KeyAllyInjured)) { bit = 8; return true; }
            if (keyId == GoapUtils.GetKeyId(GoapKnownNames.KeyPatrolling)) { bit = 9; return true; }

            bit = -1;
            return false;
        }
    }
}
