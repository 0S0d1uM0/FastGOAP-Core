#pragma once

#include <condition_variable>
#include <cstdint>
#include <memory>
#include <mutex>
#include <queue>
#include <string>
#include <thread>
#include <unordered_map>
#include <vector>

#include "FastGoapMiddlewareTypes.h"
#include "FastGoapPlanner.h"

namespace fastgoap
{

constexpr int kOk = 0;
constexpr int kInvalidArg = 1;
constexpr int kInvalidContext = 2;
constexpr int kGraphNotUploaded = 3;
constexpr int kQueueFull = 4;
constexpr int kInternalError = 5;

struct Context
{
    GoapNativeConfig Config{};
    std::vector<GoapNativeActionRule> Actions;
    std::vector<GoalRule> Goals;
    bool GraphUploaded = false;

    std::queue<GoapPlanRequest> RequestQueue;
    std::queue<GoapPlanResult> ResultQueue;

    std::vector<std::thread> Workers;
    std::condition_variable RequestCv;
    bool StopWorkersFlag = false;

    std::string LastError;
    std::mutex Mutex;
};

Context* GetContext(uint64_t handle);
uint64_t RegisterContext(std::unique_ptr<Context> ctx);
std::unique_ptr<Context> UnregisterContext(uint64_t handle);

void StartWorkers(Context& ctx);
void StopWorkers(Context& ctx);

} // namespace fastgoap
