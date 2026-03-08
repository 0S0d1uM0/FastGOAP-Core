#include "FastGoapPlanner.h"
#include <bit>
#include <algorithm>
#include <array>
#include <limits>
#include <vector>

namespace
{

// 神奇的C++20内置函数，能在常数时间内计算出一个64位整数中有多少位是1，效率远超传统的循环计数方法
int CountBits(uint64_t bits) {
    return std::popcount(bits); 
}

// 启发式函数，简单地计算当前状态与目标状态之间的差距，作为A*搜索的评估函数
float Heuristic(uint64_t bits, uint64_t goalTrue, uint64_t goalFalse)
{
    const uint64_t missTrue = goalTrue & (~bits);
    const uint64_t missFalse = goalFalse & bits;
    return static_cast<float>(CountBits(missTrue) + CountBits(missFalse));
}

// 判断当前状态是否满足目标条件
bool IsGoalState(uint64_t bits, uint64_t goalTrue, uint64_t goalFalse)
{
    return (bits & goalTrue) == goalTrue && (bits & goalFalse) == 0;
}

// 判断一个动作的前置条件是否满足当前状态
bool PreconditionsMet(uint64_t bits, const GoapNativeActionRule& action)
{
    return (bits & action.RequireTrueBits) == action.RequireTrueBits && (bits & action.RequireFalseBits) == 0;
}

// 将一个动作的效果应用到当前状态，得到下一个状态
uint64_t ApplyEffects(uint64_t bits, const GoapNativeActionRule& action)
{
    return (bits | action.SetBits) & (~action.ClearBits);
}

// 换了一种无Switch（C++力大砖飞指针成员数组）的方式来设置GoapPlanResult中的ActionId字段，应该能快上不少
void SetResultAction(GoapPlanResult& r, int index, uint16_t value)
{
    static constexpr uint16_t GoapPlanResult::* kFields[16] = {
        &GoapPlanResult::ActionId0, &GoapPlanResult::ActionId1,
        &GoapPlanResult::ActionId2, &GoapPlanResult::ActionId3,
        &GoapPlanResult::ActionId4, &GoapPlanResult::ActionId5,
        &GoapPlanResult::ActionId6, &GoapPlanResult::ActionId7,
        &GoapPlanResult::ActionId8, &GoapPlanResult::ActionId9,
        &GoapPlanResult::ActionId10, &GoapPlanResult::ActionId11,
        &GoapPlanResult::ActionId12, &GoapPlanResult::ActionId13,
        &GoapPlanResult::ActionId14, &GoapPlanResult::ActionId15
    };

    if (index < 0 || index >= 16) return;
    r.*kFields[index] = value;
}

} // namespace

namespace fastgoap
{

// 使用A*算法在状态空间中搜索满足目标条件的动作序列
GoapPlanResult SolveOne(
    const GoapNativeConfig& cfg,
    const std::vector<GoapNativeActionRule>& actions,
    const std::vector<GoalRule>& goals,
    const GoapPlanRequest& req,
    PlannerWorkingBuffer& workingBuffer)
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

    // 使用workingBuffer来完全优化内存分配，避免在搜索过程中频繁分配和释放内存
    std::vector<PlannerNode>& nodes = workingBuffer.Nodes;
    nodes.clear();
    if (nodes.capacity() < static_cast<size_t>(maxStates))
        nodes.reserve(static_cast<size_t>(maxStates));

    PlannerNode start;
    start.Bits = req.WorldStateBits;
    start.G = 0.0f;
    start.F = Heuristic(start.Bits, goal->RequireTrueBits, goal->RequireFalseBits);
    start.Parent = -1;
    start.Action = -1;
    start.Open = true;
    nodes.push_back(start);

    int goalIdx = IsGoalState(start.Bits, goal->RequireTrueBits, goal->RequireFalseBits) ? 0 : -1;

    // 为了可预测的开销，直接线性扫描 open 集合而不引入更重的数据结构，后面再考虑增加一个优先队列来优化
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

                PlannerNode n;
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
