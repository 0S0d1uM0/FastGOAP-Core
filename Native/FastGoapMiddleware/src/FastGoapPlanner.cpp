#include "FastGoapPlanner.h"

#include <algorithm>
#include <array>
#include <limits>
#include <vector>

namespace
{

int CountBits(uint64_t bits)
{
    int c = 0;
    while (bits != 0)
    {
        bits &= (bits - 1);
        ++c;
    }
    return c;
}

float Heuristic(uint64_t bits, uint64_t goalTrue, uint64_t goalFalse)
{
    const uint64_t missTrue = goalTrue & (~bits);
    const uint64_t missFalse = goalFalse & bits;
    return static_cast<float>(CountBits(missTrue) + CountBits(missFalse));
}

bool IsGoalState(uint64_t bits, uint64_t goalTrue, uint64_t goalFalse)
{
    return (bits & goalTrue) == goalTrue && (bits & goalFalse) == 0;
}

bool PreconditionsMet(uint64_t bits, const GoapNativeActionRule& action)
{
    return (bits & action.RequireTrueBits) == action.RequireTrueBits && (bits & action.RequireFalseBits) == 0;
}

uint64_t ApplyEffects(uint64_t bits, const GoapNativeActionRule& action)
{
    return (bits | action.SetBits) & (~action.ClearBits);
}

void SetResultAction(GoapPlanResult& r, int index, uint16_t value)
{
    switch (index)
    {
    case 0: r.ActionId0 = value; break;
    case 1: r.ActionId1 = value; break;
    case 2: r.ActionId2 = value; break;
    case 3: r.ActionId3 = value; break;
    case 4: r.ActionId4 = value; break;
    case 5: r.ActionId5 = value; break;
    case 6: r.ActionId6 = value; break;
    case 7: r.ActionId7 = value; break;
    case 8: r.ActionId8 = value; break;
    case 9: r.ActionId9 = value; break;
    case 10: r.ActionId10 = value; break;
    case 11: r.ActionId11 = value; break;
    case 12: r.ActionId12 = value; break;
    case 13: r.ActionId13 = value; break;
    case 14: r.ActionId14 = value; break;
    case 15: r.ActionId15 = value; break;
    default: break;
    }
}

} // namespace

namespace fastgoap
{

GoapPlanResult SolveOne(
    const GoapNativeConfig& cfg,
    const std::vector<GoapNativeActionRule>& actions,
    const std::vector<GoalRule>& goals,
    const GoapPlanRequest& req)
{
    GoapPlanResult out{};
    out.AgentId = req.AgentId;
    out.Tick = req.Tick;
    out.Status = GoapPlanStatus_NoPlan;
    out.StepCount = 0;

    // 当图数据不可用或目标缺失时，尽量返回一个可执行的单步动作，避免上层卡死
    auto applySingleStepFallback = [&out, &req]() -> bool
    {
        int chosen = -1;
        for (int i = 0; i < 64; ++i)
        {
            if ((req.ExecutableActionBits >> i) & 1ULL) { chosen = i; break; }
        }
        if (chosen < 0)
        {
            for (int i = 0; i < 64; ++i)
            {
                if ((req.EnabledActionBits >> i) & 1ULL) { chosen = i; break; }
            }
        }

        if (chosen < 0)
            return false;

        out.Status = GoapPlanStatus_Success;
        out.StepCount = 1;
        SetResultAction(out, 0, static_cast<uint16_t>(chosen));
        return true;
    };

    if (actions.empty())
    {
        applySingleStepFallback();
        return out;
    }

    const GoalRule* goal = nullptr;
    for (const auto& g : goals)
    {
        if (g.GoalId == req.CurrentGoalId)
        {
            goal = &g;
            break;
        }
    }

    if (goal == nullptr)
    {
        applySingleStepFallback();
        return out;
    }

    const int maxStates = static_cast<int>(std::max<uint32_t>(16, std::min<uint32_t>(cfg.SearchMaxStates, 256)));
    const int maxExpansions = static_cast<int>(std::max<uint32_t>(8, std::min<uint32_t>(cfg.SearchMaxExpansions, 4096)));

    struct Node
    {
        uint64_t Bits = 0;
        float G = 0;
        float F = 0;
        int Parent = -1;
        int16_t Action = -1;
        bool Open = false;
        bool Closed = false;
    };

    std::vector<Node> nodes;
    nodes.reserve(static_cast<size_t>(maxStates));

    Node start;
    start.Bits = req.WorldStateBits;
    start.G = 0.0f;
    start.F = Heuristic(start.Bits, goal->RequireTrueBits, goal->RequireFalseBits);
    start.Parent = -1;
    start.Action = -1;
    start.Open = true;
    nodes.push_back(start);

    int goalIdx = IsGoalState(start.Bits, goal->RequireTrueBits, goal->RequireFalseBits) ? 0 : -1;

    // 简化 A*：为了可预测的开销，直接线性扫描 open 集合而不引入更重的数据结构，后面再考虑增加一个优先队列来优化
    for (int expansion = 0; expansion < maxExpansions; ++expansion)
    {
        int current = -1;
        float bestF = std::numeric_limits<float>::max();
        for (int i = 0; i < static_cast<int>(nodes.size()); ++i)
        {
            if (!nodes[i].Open) continue;
            if (nodes[i].F < bestF)
            {
                bestF = nodes[i].F;
                current = i;
            }
        }

        if (current < 0)
            break;

        nodes[current].Open = false;
        nodes[current].Closed = true;

        if (IsGoalState(nodes[current].Bits, goal->RequireTrueBits, goal->RequireFalseBits))
        {
            goalIdx = current;
            break;
        }

        for (int a = 0; a < static_cast<int>(actions.size()); ++a)
        {
            const auto& action = actions[a];
            const uint64_t actionMask = (a < 64) ? (1ULL << a) : 0ULL;
            if (actionMask == 0ULL)
                continue;

            if ((req.EnabledActionBits & actionMask) == 0ULL)
                continue;

            if (!PreconditionsMet(nodes[current].Bits, action))
                continue;

            const uint64_t nextBits = ApplyEffects(nodes[current].Bits, action);
            const float tentativeG = nodes[current].G + std::max(0.01f, action.BaseCost);

            int existing = -1;
            for (int i = 0; i < static_cast<int>(nodes.size()); ++i)
            {
                if (nodes[i].Bits == nextBits)
                {
                    existing = i;
                    break;
                }
            }

            if (existing < 0)
            {
                if (static_cast<int>(nodes.size()) >= maxStates)
                    continue;

                Node n;
                n.Bits = nextBits;
                n.G = tentativeG;
                n.F = tentativeG + Heuristic(nextBits, goal->RequireTrueBits, goal->RequireFalseBits);
                n.Parent = current;
                n.Action = static_cast<int16_t>(action.ActionId);
                n.Open = true;
                n.Closed = false;
                nodes.push_back(n);
            }
            else if (tentativeG < nodes[existing].G)
            {
                nodes[existing].G = tentativeG;
                nodes[existing].F = tentativeG + Heuristic(nextBits, goal->RequireTrueBits, goal->RequireFalseBits);
                nodes[existing].Parent = current;
                nodes[existing].Action = static_cast<int16_t>(action.ActionId);
                nodes[existing].Open = true;
                nodes[existing].Closed = false;
            }
        }
    }

    if (goalIdx < 0)
    {
        applySingleStepFallback();
        return out;
    }

    std::array<uint16_t, 16> reversed{};
    int stepCount = 0;
    for (int cursor = goalIdx; cursor >= 0 && stepCount < 16; cursor = nodes[cursor].Parent)
    {
        const int16_t action = nodes[cursor].Action;
        if (action < 0)
            break;

        reversed[stepCount++] = static_cast<uint16_t>(action);
    }

    if (stepCount <= 0)
    {
        // 起始状态已满足目标时，也返回一个稳定可预测的执行步，便于调度层处理
        applySingleStepFallback();
        return out;
    }

    out.Status = GoapPlanStatus_Success;
    out.StepCount = static_cast<uint8_t>(stepCount);

    for (int i = 0; i < stepCount; ++i)
    {
        SetResultAction(out, stepCount - 1 - i, reversed[i]);
    }

    return out;
}

} // namespace fastgoap
