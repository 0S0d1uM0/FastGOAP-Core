#include "FastGoapRuntime.h"

#include <algorithm>
#include <utility>

namespace
{

// 根据配置自动决定工作线程数量，默认为CPU核心数减一，至少保留一个线程用于主线程调度
uint32_t ResolveWorkerCount(const GoapNativeConfig& cfg)
{
    if (cfg.WorkerThreads > 0)
        return cfg.WorkerThreads;

    const uint32_t hw = std::max(1u, std::thread::hardware_concurrency());
    return std::max(1u, hw > 1 ? hw - 1 : 1u);
}

// 工作线程主循环，等待请求并调用规划函数处理，最后将结果丢入结果队列
void WorkerLoop(fastgoap::Context* ctx)
{
    fastgoap::PlannerWorkingBuffer workingBuffer;

    for (;;)
    {
        GoapPlanRequest req{};
        std::shared_ptr<const fastgoap::GraphData> graph;
        GoapNativeConfig cfg{};

        {
            std::unique_lock<std::mutex> lock(ctx->RequestMutex);
            ctx->RequestCv.wait(lock, [ctx]
            {
                return ctx->StopWorkersFlag || !ctx->RequestQueue.empty();
            });

            if (ctx->StopWorkersFlag && ctx->RequestQueue.empty())
                return;

            req = ctx->RequestQueue.front();
            ctx->RequestQueue.pop();
        }

        {
            std::lock_guard<std::mutex> lock(ctx->StateMutex);
            graph = ctx->Graph;
            cfg = ctx->Config;
        }

        GoapPlanResult result{};
        try
        {
            if (!graph)
            {
                result = {};
                result.AgentId = req.AgentId;
                result.Tick = req.Tick;
                result.Status = GoapPlanStatus_InternalError;
                result.StepCount = 0;
            }
            else
            {
                result = fastgoap::SolveOne(cfg, graph->Actions, graph->Goals, req, workingBuffer);
            }
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
            std::lock_guard<std::mutex> lock(ctx->ResultMutex);
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
    GoapNativeConfig cfgCopy{};
    {
        std::lock_guard<std::mutex> stateLock(ctx.StateMutex);
        cfgCopy = ctx.Config;
    }

    const uint32_t workerCount = ResolveWorkerCount(cfgCopy);
    {
        std::lock_guard<std::mutex> requestLock(ctx.RequestMutex);
        ctx.StopWorkersFlag = false;
    }
    ctx.Workers.reserve(workerCount);
    for (uint32_t i = 0; i < workerCount; ++i)
    {
        ctx.Workers.emplace_back([&ctx]() { WorkerLoop(&ctx); });
    }
}

void StopWorkers(Context& ctx)
{
    {
        std::lock_guard<std::mutex> lock(ctx.RequestMutex);
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
