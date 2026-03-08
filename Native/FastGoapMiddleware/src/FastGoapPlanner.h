#pragma once

#include <vector>

#include "FastGoapMiddlewareTypes.h"

namespace fastgoap
{

struct GoalRule
{
    int32_t GoalId = -1;
    uint64_t RequireTrueBits = 0;
    uint64_t RequireFalseBits = 0;
};

struct PlannerNode
{
    uint64_t Bits = 0;
    float G = 0;
    float F = 0;
    int Parent = -1;
    int16_t Action = -1;
    bool Open = false;
    bool Closed = false;
};

struct PlannerWorkingBuffer
{
    std::vector<PlannerNode> Nodes;
};

GoapPlanResult SolveOne(
    const GoapNativeConfig& cfg,
    const std::vector<GoapNativeActionRule>& actions,
    const std::vector<GoalRule>& goals,
    const GoapPlanRequest& req,
    PlannerWorkingBuffer& workingBuffer);

} // namespace fastgoap
