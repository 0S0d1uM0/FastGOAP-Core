using System;
using System.Collections.Generic;
using CrashKonijn.Goap.Core;
using Unity.Mathematics;

namespace SeventhSequence.ECS.GOAP
{
    /// <summary>
    /// 纯 C# 的中间件运行时，用于先验证调度、预算和容错流程。
    /// 行为是非阻塞的：每帧只处理固定数量请求。
    /// </summary>
    public static class GoapManagedRuntime
    {
        private static readonly Queue<GoapPlanRequest> s_RequestQueue = new Queue<GoapPlanRequest>(4096);
        private static readonly Queue<GoapPlanResult> s_ResultQueue = new Queue<GoapPlanResult>(4096);

        private static ActionRule[] s_ActionRules = Array.Empty<ActionRule>();
        private static GoalRule[] s_GoalRules = Array.Empty<GoalRule>();
        private static bool s_GraphReady;
        private static int s_LastActionCount;
        private static int s_LastGoalCount;

        private static int s_MaxExpansions = 64;
        private static int s_MaxStates = 96;

        // 这些缓冲区用于单次求解，避免每帧频繁分配。
        private static ulong[] s_StateBits = new ulong[256];
        private static float[] s_GCost = new float[256];
        private static float[] s_FCost = new float[256];
        private static int[] s_ParentState = new int[256];
        private static short[] s_ParentAction = new short[256];
        private static bool[] s_InOpen = new bool[256];
        private static bool[] s_InClosed = new bool[256];

        private const int HardMaxStates = 256;

        public static void Reset()
        {
            s_RequestQueue.Clear();
            s_ResultQueue.Clear();
            s_GraphReady = false;
            s_LastActionCount = 0;
            s_LastGoalCount = 0;
            s_ActionRules = Array.Empty<ActionRule>();
            s_GoalRules = Array.Empty<GoalRule>();
        }

        public static void SetPlannerBudget(int maxExpansions, int maxStates)
        {
            s_MaxExpansions = math.clamp(maxExpansions, 8, 512);
            s_MaxStates = math.clamp(maxStates, 16, HardMaxStates);
        }

        public static void ConfigureGraph(ref GoapGraphBlob graph)
        {
            int actionCount = graph.Nodes.Length;
            int goalCount = graph.Goals.Length;

            if (s_GraphReady && s_LastActionCount == actionCount && s_LastGoalCount == goalCount)
                return;

            s_ActionRules = new ActionRule[actionCount];
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

                    // 这里只编码布尔语义：true/false。
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

                s_ActionRules[i] = new ActionRule
                {
                    ActionId = (ushort)i,
                    BaseCost = math.max(0.01f, node.BaseCost),
                    RequireTrueBits = reqTrue,
                    RequireFalseBits = reqFalse,
                    SetBits = setBits,
                    ClearBits = clearBits,
                };
            }

            s_GoalRules = new GoalRule[goalCount];
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

                s_GoalRules[g] = new GoalRule
                {
                    GoalId = goal.GoalId,
                    RequireTrueBits = reqTrue,
                    RequireFalseBits = reqFalse,
                };
            }

            s_LastActionCount = actionCount;
            s_LastGoalCount = goalCount;
            s_GraphReady = true;
        }

        public static bool Submit(in GoapPlanRequest request)
        {
            // 固定容量策略：满了就拒绝，避免无限增长导致主线程风险。
            if (s_RequestQueue.Count >= 8192)
                return false;

            s_RequestQueue.Enqueue(request);
            return true;
        }

        public static int Process(int maxProcessPerFrame)
        {
            int budget = math.max(0, maxProcessPerFrame);
            int processed = 0;
            for (int i = 0; i < budget && s_RequestQueue.Count > 0; i++)
            {
                var req = s_RequestQueue.Dequeue();
                var result = SolveOne(in req);
                s_ResultQueue.Enqueue(result);
                processed++;
            }

            return processed;
        }

        public static void GetQueueDepth(out int requestCount, out int resultCount)
        {
            requestCount = s_RequestQueue.Count;
            resultCount = s_ResultQueue.Count;
        }

        public static int Poll(GoapPlanResult[] output, int maxCount)
        {
            int count = 0;
            int limit = math.max(0, math.min(maxCount, output.Length));
            while (count < limit && s_ResultQueue.Count > 0)
            {
                output[count] = s_ResultQueue.Dequeue();
                count++;
            }

            return count;
        }

        private static GoapPlanResult SolveOne(in GoapPlanRequest req)
        {
            var res = new GoapPlanResult
            {
                AgentId = req.AgentId,
                Tick = req.Tick,
                Status = GoapPlanStatus.NoPlan,
                StepCount = 0,
            };

            if (!s_GraphReady || s_ActionRules.Length == 0)
                return FallbackSingleStep(in req, res);

            if (!TryGetGoalRule(req.CurrentGoalId, out var goalRule))
                return FallbackSingleStep(in req, res);

            if (TrySolveBoundedAStar(in req, in goalRule, ref res))
                return res;

            return FallbackSingleStep(in req, res);
        }

        private static bool TrySolveBoundedAStar(in GoapPlanRequest req, in GoalRule goal, ref GoapPlanResult result)
        {
            int maxStates = math.min(s_MaxStates, HardMaxStates);
            ClearSolveBuffers(maxStates);

            int stateCount = 1;
            int expansions = 0;

            s_StateBits[0] = req.WorldStateBits;
            s_GCost[0] = 0f;
            s_FCost[0] = Heuristic(req.WorldStateBits, goal.RequireTrueBits, goal.RequireFalseBits);
            s_ParentState[0] = -1;
            s_ParentAction[0] = -1;
            s_InOpen[0] = true;

            int goalStateIdx = IsGoalState(req.WorldStateBits, goal.RequireTrueBits, goal.RequireFalseBits) ? 0 : -1;

            while (expansions < s_MaxExpansions)
            {
                int current = PopBestOpenState(stateCount);
                if (current < 0)
                    break;

                s_InOpen[current] = false;
                s_InClosed[current] = true;
                expansions++;

                ulong currentBits = s_StateBits[current];
                if (IsGoalState(currentBits, goal.RequireTrueBits, goal.RequireFalseBits))
                {
                    goalStateIdx = current;
                    break;
                }

                for (int i = 0; i < s_ActionRules.Length; i++)
                {
                    ref var action = ref s_ActionRules[i];
                    ulong actionMask = 1UL << i;

                    if ((req.EnabledActionBits & actionMask) == 0UL)
                        continue;

                    if (!SatisfyPreconditions(currentBits, action.RequireTrueBits, action.RequireFalseBits))
                        continue;

                    ulong nextBits = ApplyEffects(currentBits, action.SetBits, action.ClearBits);
                    float tentativeG = s_GCost[current] + action.BaseCost;

                    int existing = FindStateIndex(nextBits, stateCount);
                    if (existing < 0)
                    {
                        if (stateCount >= maxStates)
                            continue;

                        int idx = stateCount++;
                        s_StateBits[idx] = nextBits;
                        s_GCost[idx] = tentativeG;
                        s_FCost[idx] = tentativeG + Heuristic(nextBits, goal.RequireTrueBits, goal.RequireFalseBits);
                        s_ParentState[idx] = current;
                        s_ParentAction[idx] = (short)action.ActionId;
                        s_InOpen[idx] = true;
                        s_InClosed[idx] = false;
                    }
                    else if (tentativeG < s_GCost[existing])
                    {
                        s_GCost[existing] = tentativeG;
                        s_FCost[existing] = tentativeG + Heuristic(nextBits, goal.RequireTrueBits, goal.RequireFalseBits);
                        s_ParentState[existing] = current;
                        s_ParentAction[existing] = (short)action.ActionId;
                        s_InOpen[existing] = true;
                        s_InClosed[existing] = false;
                    }
                }
            }

            if (goalStateIdx < 0)
                return false;

            int steps = 0;
            int cursor = goalStateIdx;
            while (cursor >= 0 && s_ParentAction[cursor] >= 0 && steps < 16)
            {
                steps++;
                cursor = s_ParentState[cursor];
            }

            if (steps == 0)
                return false;

            int write = steps - 1;
            cursor = goalStateIdx;
            while (cursor >= 0 && s_ParentAction[cursor] >= 0 && write >= 0)
            {
                result.SetActionId(write, (ushort)s_ParentAction[cursor]);
                write--;
                cursor = s_ParentState[cursor];
            }

            result.Status = GoapPlanStatus.Success;
            result.StepCount = (byte)steps;
            return true;
        }

        private static GoapPlanResult FallbackSingleStep(in GoapPlanRequest req, GoapPlanResult res)
        {
            int chosen = FirstSetBit(req.ExecutableActionBits);
            if (chosen < 0)
                chosen = FirstSetBit(req.EnabledActionBits);

            if (chosen < 0 || chosen > ushort.MaxValue)
                return res;

            res.Status = GoapPlanStatus.Success;
            res.StepCount = 1;
            res.SetActionId(0, (ushort)chosen);
            return res;
        }

        private static bool TryGetGoalRule(int goalId, out GoalRule rule)
        {
            for (int i = 0; i < s_GoalRules.Length; i++)
            {
                if (s_GoalRules[i].GoalId == goalId)
                {
                    rule = s_GoalRules[i];
                    return true;
                }
            }

            rule = default;
            return false;
        }

        private static void ClearSolveBuffers(int maxStates)
        {
            Array.Clear(s_InOpen, 0, maxStates);
            Array.Clear(s_InClosed, 0, maxStates);
            Array.Clear(s_ParentState, 0, maxStates);
            Array.Clear(s_ParentAction, 0, maxStates);
        }

        private static int PopBestOpenState(int stateCount)
        {
            int best = -1;
            float bestF = float.MaxValue;
            for (int i = 0; i < stateCount; i++)
            {
                if (!s_InOpen[i])
                    continue;

                if (s_FCost[i] < bestF)
                {
                    bestF = s_FCost[i];
                    best = i;
                }
            }

            return best;
        }

        private static int FindStateIndex(ulong bits, int stateCount)
        {
            for (int i = 0; i < stateCount; i++)
            {
                if (s_StateBits[i] == bits)
                    return i;
            }

            return -1;
        }

        private static bool SatisfyPreconditions(ulong bits, ulong requireTrue, ulong requireFalse)
        {
            return (bits & requireTrue) == requireTrue && (bits & requireFalse) == 0UL;
        }

        private static bool IsGoalState(ulong bits, ulong requireTrue, ulong requireFalse)
        {
            return (bits & requireTrue) == requireTrue && (bits & requireFalse) == 0UL;
        }

        private static ulong ApplyEffects(ulong bits, ulong setBits, ulong clearBits)
        {
            return (bits | setBits) & ~clearBits;
        }

        private static float Heuristic(ulong bits, ulong goalTrue, ulong goalFalse)
        {
            ulong missTrue = goalTrue & ~bits;
            ulong missFalse = goalFalse & bits;
            return CountBits(missTrue) + CountBits(missFalse);
        }

        private static int CountBits(ulong bits)
        {
            int count = 0;
            while (bits != 0UL)
            {
                bits &= bits - 1UL;
                count++;
            }

            return count;
        }

        private static int FirstSetBit(ulong bits)
        {
            if (bits == 0UL)
                return -1;

            for (int i = 0; i < 64; i++)
            {
                if (((bits >> i) & 1UL) != 0UL)
                    return i;
            }

            return -1;
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

        private struct ActionRule
        {
            public ushort ActionId;
            public float BaseCost;
            public ulong RequireTrueBits;
            public ulong RequireFalseBits;
            public ulong SetBits;
            public ulong ClearBits;
        }

        private struct GoalRule
        {
            public int GoalId;
            public ulong RequireTrueBits;
            public ulong RequireFalseBits;
        }
    }
}
