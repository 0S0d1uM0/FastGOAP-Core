#include <algorithm>
#include <array>
#include <condition_variable>
#include <cstdint>
#include <cstring>
#include <limits>
#include <memory>
#include <mutex>
#include <queue>
#include <string>
#include <thread>
#include <unordered_map>
#include <vector>

#if defined(_WIN32)
#define FASTGOAP_CALL __cdecl
#if defined(FASTGOAP_EXPORTS)
#define FASTGOAP_API extern "C" __declspec(dllexport)
#else
#define FASTGOAP_API extern "C" __declspec(dllimport)
#endif
#else
#define FASTGOAP_CALL
#define FASTGOAP_API extern "C"
#endif

#pragma pack(push, 4)
struct GoapNativeConfig
{
    uint32_t Version;
    uint32_t WorkerThreads;
    uint32_t MaxAgents;
    uint32_t MaxQueuedRequests;
    uint32_t MaxPlanSteps;
    uint32_t SearchMaxExpansions;
    uint32_t SearchMaxStates;
    uint32_t Reserved0;
};

struct GoapNativeGraphHeader
{
    uint32_t Version;
    uint32_t ActionCount;
    uint32_t GoalCount;
    uint32_t WorldBitWidth;
};

struct GoapNativeActionRule
{
    uint16_t ActionId;
    uint16_t Reserved;
    float BaseCost;
    uint64_t RequireTrueBits;
    uint64_t RequireFalseBits;
    uint64_t SetBits;
    uint64_t ClearBits;
};

struct GoapNativeGoalRule
{
    int32_t GoalId;
    uint32_t Reserved;
    uint64_t RequireTrueBits;
    uint64_t RequireFalseBits;
};

enum GoapPlanStatus : uint32_t
{
    GoapPlanStatus_Success = 0,
    GoapPlanStatus_Timeout = 1,
    GoapPlanStatus_NoPlan = 2,
    GoapPlanStatus_InternalError = 3,
};

struct GoapPlanRequest
{
    uint32_t AgentId;
    int32_t CurrentGoalId;
    float PositionX;
    float PositionY;
    float PositionZ;
    uint64_t WorldStateBits;
    uint64_t EnabledActionBits;
    uint64_t ExecutableActionBits;
    uint32_t Tick;
};

struct GoapPlanResult
{
    uint32_t AgentId;
    uint32_t Tick;
    uint32_t Status;
    uint8_t StepCount;
    uint16_t ActionId0;
    uint16_t ActionId1;
    uint16_t ActionId2;
    uint16_t ActionId3;
    uint16_t ActionId4;
    uint16_t ActionId5;
    uint16_t ActionId6;
    uint16_t ActionId7;
    uint16_t ActionId8;
    uint16_t ActionId9;
    uint16_t ActionId10;
    uint16_t ActionId11;
    uint16_t ActionId12;
    uint16_t ActionId13;
    uint16_t ActionId14;
    uint16_t ActionId15;
};
#pragma pack(pop)

namespace
{
constexpr int kOk = 0;
constexpr int kInvalidArg = 1;
constexpr int kInvalidContext = 2;
constexpr int kGraphNotUploaded = 3;
constexpr int kQueueFull = 4;
constexpr int kInternalError = 5;

struct GoalRule
{
    int32_t GoalId = -1;
    uint64_t RequireTrueBits = 0;
    uint64_t RequireFalseBits = 0;
};

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
    bool StopWorkers = false;

    std::string LastError;
    std::mutex Mutex;
};

std::unordered_map<uint64_t, std::unique_ptr<Context>> g_contexts;
std::mutex g_contextsMutex;

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
        // Keep deterministic fallback behavior similar to managed runtime.
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
        // Fallback single step as safety net.
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
        // Goal may already be satisfied at start state; return a deterministic executable step instead of NoPlan.
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

Context* GetContext(uint64_t handle)
{
    std::lock_guard<std::mutex> lock(g_contextsMutex);
    auto it = g_contexts.find(handle);
    if (it == g_contexts.end())
        return nullptr;
    return it->second.get();
}

uint64_t MakeHandle(Context* ctx)
{
    return static_cast<uint64_t>(reinterpret_cast<uintptr_t>(ctx));
}

uint32_t ResolveWorkerCount(const GoapNativeConfig& cfg)
{
    if (cfg.WorkerThreads > 0)
        return cfg.WorkerThreads;

    const uint32_t hw = std::max(1u, std::thread::hardware_concurrency());
    return std::max(1u, hw > 1 ? hw - 1 : 1u);
}

void WorkerLoop(Context* ctx)
{
    for (;;)
    {
        GoapPlanRequest req{};
        std::vector<GoapNativeActionRule> actions;
        std::vector<GoalRule> goals;
        GoapNativeConfig cfg{};

        {
            std::unique_lock<std::mutex> lock(ctx->Mutex);
            ctx->RequestCv.wait(lock, [ctx]
            {
                return ctx->StopWorkers || !ctx->RequestQueue.empty();
            });

            if (ctx->StopWorkers && ctx->RequestQueue.empty())
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
            result = SolveOne(cfg, actions, goals, req);
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

void StartWorkers(Context& ctx)
{
    const uint32_t workerCount = ResolveWorkerCount(ctx.Config);
    ctx.StopWorkers = false;
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
        ctx.StopWorkers = true;
    }
    ctx.RequestCv.notify_all();

    for (auto& w : ctx.Workers)
    {
        if (w.joinable())
            w.join();
    }
    ctx.Workers.clear();
}

} // namespace

FASTGOAP_API int FASTGOAP_CALL Goap_CreateContext(const GoapNativeConfig* config, uint64_t* outContext)
{
    if (config == nullptr || outContext == nullptr)
        return kInvalidArg;

    if (config->Version != 1)
        return kInvalidArg;

    auto ctx = std::make_unique<Context>();
    ctx->Config = *config;
    ctx->GraphUploaded = false;
    ctx->LastError.clear();

    if (ctx->Config.MaxQueuedRequests == 0)
        ctx->Config.MaxQueuedRequests = 8192;
    if (ctx->Config.MaxPlanSteps == 0)
        ctx->Config.MaxPlanSteps = 16;
    if (ctx->Config.SearchMaxExpansions == 0)
        ctx->Config.SearchMaxExpansions = 64;
    if (ctx->Config.SearchMaxStates == 0)
        ctx->Config.SearchMaxStates = 96;

    StartWorkers(*ctx);

    const uint64_t handle = MakeHandle(ctx.get());

    {
        std::lock_guard<std::mutex> lock(g_contextsMutex);
        g_contexts.emplace(handle, std::move(ctx));
    }

    *outContext = handle;
    return kOk;
}

FASTGOAP_API int FASTGOAP_CALL Goap_DestroyContext(uint64_t context)
{
    std::unique_ptr<Context> ctx;
    {
        std::lock_guard<std::mutex> lock(g_contextsMutex);
        auto it = g_contexts.find(context);
        if (it == g_contexts.end())
            return kInvalidContext;

        ctx = std::move(it->second);
        g_contexts.erase(it);
    }

    if (ctx)
        StopWorkers(*ctx);

    return kOk;
}

FASTGOAP_API int FASTGOAP_CALL Goap_UploadGraph(
    uint64_t context,
    const GoapNativeGraphHeader* header,
    const GoapNativeActionRule* actions,
    int actionCount,
    const GoapNativeGoalRule* goals,
    int goalCount)
{
    Context* ctx = GetContext(context);
    if (ctx == nullptr)
        return kInvalidContext;

    if (header == nullptr || actions == nullptr || goals == nullptr || actionCount < 0 || goalCount < 0)
        return kInvalidArg;

    if (header->Version != 1 || header->WorldBitWidth != 64)
        return kInvalidArg;

    std::lock_guard<std::mutex> lock(ctx->Mutex);

    try
    {
        ctx->Actions.assign(actions, actions + actionCount);
        ctx->Goals.clear();
        ctx->Goals.reserve(static_cast<size_t>(goalCount));
        for (int i = 0; i < goalCount; ++i)
        {
            GoalRule g;
            g.GoalId = goals[i].GoalId;
            g.RequireTrueBits = goals[i].RequireTrueBits;
            g.RequireFalseBits = goals[i].RequireFalseBits;
            ctx->Goals.push_back(g);
        }
        ctx->GraphUploaded = true;
        ctx->LastError.clear();
    }
    catch (...)
    {
        ctx->LastError = "UploadGraph allocation failed";
        return kInternalError;
    }

    return kOk;
}

FASTGOAP_API int FASTGOAP_CALL Goap_SubmitRequestsV1(uint64_t context, const GoapPlanRequest* requests, int count)
{
    Context* ctx = GetContext(context);
    if (ctx == nullptr)
        return kInvalidContext;

    if (requests == nullptr || count < 0)
        return kInvalidArg;

    std::lock_guard<std::mutex> lock(ctx->Mutex);

    if (!ctx->GraphUploaded)
    {
        ctx->LastError = "Graph not uploaded";
        return kGraphNotUploaded;
    }

    const size_t capacity = static_cast<size_t>(std::max<uint32_t>(1, ctx->Config.MaxQueuedRequests));
    const size_t pending = ctx->RequestQueue.size() + ctx->ResultQueue.size();
    if (pending + static_cast<size_t>(count) > capacity)
    {
        ctx->LastError = "Queue full";
        return kQueueFull;
    }

    try
    {
        for (int i = 0; i < count; ++i)
        {
            ctx->RequestQueue.push(requests[i]);
        }
        ctx->LastError.clear();
    }
    catch (...)
    {
        ctx->LastError = "Submit processing failed";
        return kInternalError;
    }

    ctx->RequestCv.notify_all();

    return kOk;
}

FASTGOAP_API int FASTGOAP_CALL Goap_PollResultsV1(uint64_t context, GoapPlanResult* results, int maxCount)
{
    Context* ctx = GetContext(context);
    if (ctx == nullptr)
        return kInvalidContext;

    if (results == nullptr || maxCount < 0)
        return kInvalidArg;

    std::lock_guard<std::mutex> lock(ctx->Mutex);

    int written = 0;
    while (written < maxCount && !ctx->ResultQueue.empty())
    {
        results[written] = ctx->ResultQueue.front();
        ctx->ResultQueue.pop();
        ++written;
    }

    return written;
}

FASTGOAP_API const char* FASTGOAP_CALL Goap_GetLastError(uint64_t context)
{
    Context* ctx = GetContext(context);
    if (ctx == nullptr)
        return "Invalid context";

    std::lock_guard<std::mutex> lock(ctx->Mutex);
    return ctx->LastError.c_str();
}

// Legacy compatibility exports (optional path).
FASTGOAP_API int FASTGOAP_CALL Goap_Init(const GoapNativeConfig* /*unused*/)
{
    return kOk;
}

FASTGOAP_API int FASTGOAP_CALL Goap_SubmitRequests(const GoapPlanRequest* /*requests*/, int /*count*/)
{
    return kOk;
}

FASTGOAP_API int FASTGOAP_CALL Goap_PollResults(GoapPlanResult* /*results*/, int /*maxCount*/)
{
    return 0;
}

FASTGOAP_API void FASTGOAP_CALL Goap_Shutdown()
{
}
