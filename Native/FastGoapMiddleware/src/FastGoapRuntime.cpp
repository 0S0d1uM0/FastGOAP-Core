#include "FastGoapRuntime.h"

#include <algorithm>
#include <utility>

namespace
{

uint32_t ResolveWorkerCount(const GoapNativeConfig& cfg)
{
    if (cfg.WorkerThreads > 0)
        return cfg.WorkerThreads;

    const uint32_t hw = std::max(1u, std::thread::hardware_concurrency());
    return std::max(1u, hw > 1 ? hw - 1 : 1u);
}

void WorkerLoop(fastgoap::Context* ctx)
{
    for (;;)
    {
        GoapPlanRequest req{};
        std::vector<GoapNativeActionRule> actions;
        std::vector<fastgoap::GoalRule> goals;
        GoapNativeConfig cfg{};

        {
            std::unique_lock<std::mutex> lock(ctx->Mutex);
            ctx->RequestCv.wait(lock, [ctx]
            {
                return ctx->StopWorkersFlag || !ctx->RequestQueue.empty();
            });

            if (ctx->StopWorkersFlag && ctx->RequestQueue.empty())
                return;

            req = ctx->RequestQueue.front();
            ctx->RequestQueue.pop();
            actions = ctx->Actions;
            goals = ctx->Goals;
            cfg = ctx->Config;
        }

        GoapPlanResult result{};
        try
        {
            result = fastgoap::SolveOne(cfg, actions, goals, req);
        }
        catch (...)
        {
            result = {};
            result.AgentId = req.AgentId;
            result.Tick = req.Tick;
            result.Status = GoapPlanStatus_InternalError;
            result.StepCount = 0;
        }

        {
            std::lock_guard<std::mutex> lock(ctx->Mutex);
            ctx->ResultQueue.push(result);
        }
    }
}

} // namespace

namespace fastgoap
{

std::unordered_map<uint64_t, std::unique_ptr<Context>> g_contexts;
std::mutex g_contextsMutex;

Context* GetContext(uint64_t handle)
{
    std::lock_guard<std::mutex> lock(g_contextsMutex);
    auto it = g_contexts.find(handle);
    if (it == g_contexts.end())
        return nullptr;
    return it->second.get();
}

uint64_t RegisterContext(std::unique_ptr<Context> ctx)
{
    const uint64_t handle = static_cast<uint64_t>(reinterpret_cast<uintptr_t>(ctx.get()));
    std::lock_guard<std::mutex> lock(g_contextsMutex);
    g_contexts.emplace(handle, std::move(ctx));
    return handle;
}

std::unique_ptr<Context> UnregisterContext(uint64_t handle)
{
    std::lock_guard<std::mutex> lock(g_contextsMutex);
    auto it = g_contexts.find(handle);
    if (it == g_contexts.end())
        return nullptr;

    std::unique_ptr<Context> out = std::move(it->second);
    g_contexts.erase(it);
    return out;
}

void StartWorkers(Context& ctx)
{
    const uint32_t workerCount = ResolveWorkerCount(ctx.Config);
    ctx.StopWorkersFlag = false;
    ctx.Workers.reserve(workerCount);
    for (uint32_t i = 0; i < workerCount; ++i)
    {
        ctx.Workers.emplace_back([&ctx]() { WorkerLoop(&ctx); });
    }
}

void StopWorkers(Context& ctx)
{
    {
        std::lock_guard<std::mutex> lock(ctx.Mutex);
        ctx.StopWorkersFlag = true;
    }
    ctx.RequestCv.notify_all();

    for (auto& w : ctx.Workers)
    {
        if (w.joinable())
            w.join();
    }
    ctx.Workers.clear();
}

} // namespace fastgoap
