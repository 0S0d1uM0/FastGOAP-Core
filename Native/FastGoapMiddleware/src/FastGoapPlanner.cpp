#include "FastGoapPlanner.h"
#include <bit>
#include <algorithm>
#include <array>
#include <limits>
#include <vector>

namespace
{

// 神奇的C++20内置函数，能在常数时间内计算出一个64位整数中有多少位是1，效率远超传统的循环计数方法
int CountBits(uint64_t bits)
{
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

// A*搜索节点结构，包含状态位、G值、F值、父节点索引、导致该状态的动作索引以及开放/关闭标志
constexpr int kMaxPlannerStates = 256;
constexpr int kStateHashCapacity = 512;

struct OpenHeapEntry
{
    float F = 0.0f;
    int NodeIndex = -1;
};

// 固定容量的最小堆实现，用于管理A*搜索中的开放列表，避免动态内存分配带来的性能损失
struct FixedMinHeap
{
    std::array<OpenHeapEntry, kMaxPlannerStates> Data{};
    int Count = 0;

    void Clear()
    {
        Count = 0;
    }

    bool Empty() const
    {
        return Count <= 0;
    }

    void Push(float f, int nodeIndex)
    {
        if (Count >= static_cast<int>(Data.size()))
            return;

        int i = Count++;
        Data[i].F = f;
        Data[i].NodeIndex = nodeIndex;

        while (i > 0)
        {
            const int parent = (i - 1) / 2;
            if (Data[parent].F <= Data[i].F)
                break;
            std::swap(Data[parent], Data[i]);
            i = parent;
        }
    }

    OpenHeapEntry PopMin()
    {
        OpenHeapEntry out = Data[0];
        --Count;
        Data[0] = Data[Count];

        int i = 0;
        for (;;)
        {
            const int left = i * 2 + 1;
            const int right = left + 1;

            if (left >= Count)
                break;

            int best = left;
            if (right < Count && Data[right].F < Data[left].F)
                best = right;

            if (Data[i].F <= Data[best].F)
                break;

            std::swap(Data[i], Data[best]);
            i = best;
        }

        return out;
    }
};

// 固定容量的状态哈希表实现，用于快速查找A*搜索中已访问的状态，避免动态内存分配和复杂的数据结构带来的性能损失
struct FixedStateHash
{
    std::array<uint64_t, kStateHashCapacity> Keys{};
    std::array<int16_t, kStateHashCapacity> Values{};
    std::array<uint8_t, kStateHashCapacity> Used{};

    void Clear()
    {
        Used.fill(0);
    }

    static uint32_t Hash64(uint64_t x)
    {
        x ^= (x >> 30);
        x *= 0xbf58476d1ce4e5b9ULL;
        x ^= (x >> 27);
        x *= 0x94d049bb133111ebULL;
        x ^= (x >> 31);
        return static_cast<uint32_t>(x & (kStateHashCapacity - 1));
    }

    int Find(uint64_t bits) const
    {
        uint32_t slot = Hash64(bits);
        for (int probe = 0; probe < kStateHashCapacity; ++probe)
        {
            if (!Used[slot])
                return -1;

            if (Keys[slot] == bits)
                return static_cast<int>(Values[slot]);

            slot = (slot + 1) & (kStateHashCapacity - 1);
        }

        return -1;
    }

    void InsertOrAssign(uint64_t bits, int nodeIndex)
    {
        uint32_t slot = Hash64(bits);
        for (int probe = 0; probe < kStateHashCapacity; ++probe)
        {
            if (!Used[slot] || Keys[slot] == bits)
            {
                Used[slot] = 1;
                Keys[slot] = bits;
                Values[slot] = static_cast<int16_t>(nodeIndex);
                return;
            }

            slot = (slot + 1) & (kStateHashCapacity - 1);
        }
    }
};

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
    start.Closed = false;
    nodes.push_back(start);

    FixedMinHeap openHeap;
    openHeap.Clear();
    openHeap.Push(start.F, 0);

    FixedStateHash stateHash;
    stateHash.Clear();
    stateHash.InsertOrAssign(start.Bits, 0);

    int goalIdx = IsGoalState(start.Bits, goal->RequireTrueBits, goal->RequireFalseBits) ? 0 : -1;

    for (int expansion = 0; expansion < maxExpansions; ++expansion)
    {
        int current = -1;
        while (!openHeap.Empty())
        {
            const OpenHeapEntry top = openHeap.PopMin();
            if (top.NodeIndex < 0 || top.NodeIndex >= static_cast<int>(nodes.size()))
                continue;

            const PlannerNode& candidate = nodes[top.NodeIndex];
            if (!candidate.Open)
                continue;

            if (top.F > candidate.F)
                continue;

            current = top.NodeIndex;
            break;
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

            const int existing = stateHash.Find(nextBits);

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
                const int newIndex = static_cast<int>(nodes.size()) - 1;
                stateHash.InsertOrAssign(nextBits, newIndex);
                openHeap.Push(n.F, newIndex);
            }
            else if (tentativeG < nodes[existing].G)
            {
                nodes[existing].G = tentativeG;
                nodes[existing].F = tentativeG + Heuristic(nextBits, goal->RequireTrueBits, goal->RequireFalseBits);
                nodes[existing].Parent = current;
                nodes[existing].Action = static_cast<int16_t>(action.ActionId);
                nodes[existing].Open = true;
                nodes[existing].Closed = false;
                openHeap.Push(nodes[existing].F, existing);
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
