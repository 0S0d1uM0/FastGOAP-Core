#pragma once

#include <cstdint>

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
