using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using SeventhSequence.ECS.Components.Gameplay;

namespace SeventhSequence.ECS.GOAP
{
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateBefore(typeof(SeventhSequence.ECS.Systems.Simulation.Command.UnitCommandDispatchSystem))]
    public partial struct UnitTacticalOrderCommandBridgeSystem : ISystem
    {
        private ComponentLookup<UnitThreatMemoryComponent> _threatLookup;
        private ComponentLookup<GoapRetreatTacticalState> _retreatStateLookup;

        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<UnitTacticalOrder>();
            _threatLookup = state.GetComponentLookup<UnitThreatMemoryComponent>(true);
            _retreatStateLookup = state.GetComponentLookup<GoapRetreatTacticalState>(true);
        }

        public void OnUpdate(ref SystemState state)
        {
            _threatLookup.Update(ref state);
            _retreatStateLookup.Update(ref state);

            foreach (var (order, commandBuffer, transform, entity) in
                SystemAPI.Query<
                    RefRO<UnitTacticalOrder>,
                    DynamicBuffer<UnitCommandElement>,
                    RefRO<LocalTransform>>()
                .WithAll<UnitTacticalAgent>()
                .WithEntityAccess())
            {
                if (state.EntityManager.HasComponent<UnitDeadTag>(entity) && state.EntityManager.IsComponentEnabled<UnitDeadTag>(entity))
                    continue;

                bool retreatActive = _retreatStateLookup.HasComponent(entity) && _retreatStateLookup[entity].IsRetreatActive;
                if (retreatActive)
                {
                    float3 retreatTarget = ComputeRetreatTarget(entity, transform.ValueRO.Position, 18f);
                    if (math.all(math.isfinite(retreatTarget)))
                    {
                        commandBuffer.Clear();
                        commandBuffer.Add(new UnitCommandElement
                        {
                            Type = CommandType.Move,
                            TargetPosition = retreatTarget,
                            TargetEntity = Entity.Null,
                            StopDistance = 1.5f,
                            SkillID = 0,
                            IsQueued = false,
                        });
                    }
                    continue;
                }

                var tacticalOrder = order.ValueRO;
                if (!tacticalOrder.HasOrder)
                {
                    if (!commandBuffer.IsEmpty)
                        commandBuffer.Clear();
                    continue;
                }

                if (!math.all(math.isfinite(tacticalOrder.TargetPosition)))
                    continue;

                float dist = math.distance(transform.ValueRO.Position, tacticalOrder.TargetPosition);
                if (dist <= tacticalOrder.StopDistance)
                {
                    if (!commandBuffer.IsEmpty)
                        commandBuffer.Clear();
                    continue;
                }

                commandBuffer.Clear();
                commandBuffer.Add(new UnitCommandElement
                {
                    Type = CommandType.Move,
                    TargetPosition = tacticalOrder.TargetPosition,
                    TargetEntity = Entity.Null,
                    StopDistance = tacticalOrder.StopDistance,
                    SkillID = 0,
                    IsQueued = false,
                });
            }
        }

        private float3 ComputeRetreatTarget(Entity entity, float3 selfPos, float retreatDistance)
        {
            selfPos = SanitizePosition(selfPos, float3.zero);

            if (_threatLookup.HasComponent(entity))
            {
                var threat = _threatLookup[entity];
                if (threat.HasThreat)
                {
                    float3 threatPos = SanitizePosition(threat.LastDamageSourcePosition, selfPos - new float3(0f, 0f, 1f));
                    float3 away = selfPos - threatPos;
                    float lenSq = math.lengthsq(away);
                    if (lenSq < 0.001f || !math.isfinite(lenSq))
                        away = new float3(0f, 0f, 1f);
                    else
                        away = math.normalize(away);

                    return SanitizePosition(selfPos + away * retreatDistance, selfPos + new float3(0f, 0f, retreatDistance));
                }
            }

            return selfPos + new float3(0f, 0f, retreatDistance);
        }

        private static float3 SanitizePosition(float3 value, float3 fallback)
        {
            return math.all(math.isfinite(value)) ? value : fallback;
        }
    }
}
