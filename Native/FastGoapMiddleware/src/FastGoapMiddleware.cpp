#include "FastGoapRuntime.h"

#include <algorithm>

using fastgoap::Context;

// FastGoapMiddleware的C接口，负责管理Context生命周期、处理图上传、请求提交和结果轮询等桥接功能
FASTGOAP_API int FASTGOAP_CALL Goap_CreateContext(const GoapNativeConfig* config, uint64_t* outContext)
{
    if (config == nullptr || outContext == nullptr)
        return fastgoap::kInvalidArg;

    if (config->Version != 1)
        return fastgoap::kInvalidArg;

    // 自动指针管理Context生命周期
    auto ctx = std::make_unique<Context>();
    ctx->Config = *config;
    ctx->LastError.clear();

    if (ctx->Config.MaxQueuedRequests == 0)
        ctx->Config.MaxQueuedRequests = 8192;
    if (ctx->Config.MaxPlanSteps == 0)
        ctx->Config.MaxPlanSteps = 16;
    if (ctx->Config.SearchMaxExpansions == 0)
        ctx->Config.SearchMaxExpansions = 64;
    if (ctx->Config.SearchMaxStates == 0)
        ctx->Config.SearchMaxStates = 96;

    fastgoap::StartWorkers(*ctx);

    *outContext = fastgoap::RegisterContext(std::move(ctx));
    return fastgoap::kOk;
}

// 销毁Context并停止工作线程
FASTGOAP_API int FASTGOAP_CALL Goap_DestroyContext(uint64_t context)
{
    std::unique_ptr<Context> ctx = fastgoap::UnregisterContext(context);
    if (!ctx)
        return fastgoap::kInvalidContext;

    fastgoap::StopWorkers(*ctx);
    return fastgoap::kOk;
}

// 上传图数据，包括动作规则和目标规则
FASTGOAP_API int FASTGOAP_CALL Goap_UploadGraph(
    uint64_t context,
    const GoapNativeGraphHeader* header,
    const GoapNativeActionRule* actions,
    int actionCount,
    const GoapNativeGoalRule* goals,
    int goalCount)
{
    Context* ctx = fastgoap::GetContext(context);
    if (ctx == nullptr)
        return fastgoap::kInvalidContext;

    if (header == nullptr || actions == nullptr || goals == nullptr || actionCount < 0 || goalCount < 0)
        return fastgoap::kInvalidArg;

    if (header->Version != 1 || header->WorldBitWidth != 64)
        return fastgoap::kInvalidArg;

    try
    {
        auto graph = std::make_shared<fastgoap::GraphData>();
        graph->Actions.assign(actions, actions + actionCount);
        graph->Goals.reserve(static_cast<size_t>(goalCount));
        for (int i = 0; i < goalCount; ++i)
        {
            fastgoap::GoalRule g;
            g.GoalId = goals[i].GoalId;
            g.RequireTrueBits = goals[i].RequireTrueBits;
            g.RequireFalseBits = goals[i].RequireFalseBits;
            graph->Goals.push_back(g);
        }

        std::lock_guard<std::mutex> stateLock(ctx->StateMutex);
        ctx->Graph = graph;
        ctx->LastError.clear();
    }
    catch (...)
    {
        std::lock_guard<std::mutex> stateLock(ctx->StateMutex);
        ctx->LastError = "UploadGraph allocation failed";
        return fastgoap::kInternalError;
    }

    return fastgoap::kOk;
}

// 提交规划请求，工作线程会异步处理并将结果放入结果队列
FASTGOAP_API int FASTGOAP_CALL Goap_SubmitRequestsV1(uint64_t context, const GoapPlanRequest* requests, int count)
{
    Context* ctx = fastgoap::GetContext(context);
    if (ctx == nullptr)
        return fastgoap::kInvalidContext;

    if (requests == nullptr || count < 0)
        return fastgoap::kInvalidArg;

    {
        std::lock_guard<std::mutex> stateLock(ctx->StateMutex);
        if (!ctx->Graph)
        {
            ctx->LastError = "Graph not uploaded";
            return fastgoap::kGraphNotUploaded;
        }
    }

    {
        std::scoped_lock<std::mutex, std::mutex> lock(ctx->RequestMutex, ctx->ResultMutex);

        const size_t capacity = static_cast<size_t>(std::max<uint32_t>(1, ctx->Config.MaxQueuedRequests));
        const size_t pending = ctx->RequestQueue.size() + ctx->ResultQueue.size();
        if (pending + static_cast<size_t>(count) > capacity)
        {
            std::lock_guard<std::mutex> stateLock(ctx->StateMutex);
            ctx->LastError = "Queue full";
            return fastgoap::kQueueFull;
        }

        try
        {
            for (int i = 0; i < count; ++i)
            {
                ctx->RequestQueue.push(requests[i]);
            }
        }
        catch (...)
        {
            std::lock_guard<std::mutex> stateLock(ctx->StateMutex);
            ctx->LastError = "Submit processing failed";
            return fastgoap::kInternalError;
        }
    }

    {
        std::lock_guard<std::mutex> stateLock(ctx->StateMutex);
        ctx->LastError.clear();
    }

    ctx->RequestCv.notify_all();
    return fastgoap::kOk;
}

// 轮询结果队列，返回已完成的规划结果
FASTGOAP_API int FASTGOAP_CALL Goap_PollResultsV1(uint64_t context, GoapPlanResult* results, int maxCount)
{
    Context* ctx = fastgoap::GetContext(context);
    if (ctx == nullptr)
        return fastgoap::kInvalidContext;

    if (results == nullptr || maxCount < 0)
        return fastgoap::kInvalidArg;

    std::lock_guard<std::mutex> lock(ctx->ResultMutex);

    int written = 0;
    while (written < maxCount && !ctx->ResultQueue.empty())
    {
        results[written] = ctx->ResultQueue.front();
        ctx->ResultQueue.pop();
        ++written;
    }

    return written;
}

// 获取最后一次错误信息，返回字符串指针，如果没有错误则返回空
FASTGOAP_API const char* FASTGOAP_CALL Goap_GetLastError(uint64_t context)
{
    Context* ctx = fastgoap::GetContext(context);
    if (ctx == nullptr)
        return "Invalid context";

    std::lock_guard<std::mutex> lock(ctx->StateMutex);
    return ctx->LastError.c_str();
}

// 兼容旧版入口：暂时保持可调用，方便老桥接逐步迁移
FASTGOAP_API int FASTGOAP_CALL Goap_Init(const GoapNativeConfig* /*unused*/)
{
    return fastgoap::kOk;
}

FASTGOAP_API int FASTGOAP_CALL Goap_SubmitRequests(const GoapPlanRequest* /*requests*/, int /*count*/)
{
    return fastgoap::kOk;
}

FASTGOAP_API int FASTGOAP_CALL Goap_PollResults(GoapPlanResult* /*results*/, int /*maxCount*/)
{
    return 0;
}

FASTGOAP_API void FASTGOAP_CALL Goap_Shutdown()
{
}
