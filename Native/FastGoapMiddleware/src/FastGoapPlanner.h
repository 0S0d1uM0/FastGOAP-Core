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

GoapPlanResult SolveOne(
    const GoapNativeConfig& cfg,
    const std::vector<GoapNativeActionRule>& actions,
    const std::vector<GoalRule>& goals,
    const GoapPlanRequest& req);

} // namespace fastgoap
