using Content.Shared.CCVar;
using Content.Shared.Friction;
using Content.Shared.Gravity;
using Content.Shared.Inventory;
using Content.Shared.Maps;
using Content.Shared.Movement.Components;
using Content.Shared.Movement.Events;
using Content.Shared.Pulling.Components;
using Content.Shared.Tag;
using Robust.Shared.Audio;
using Robust.Shared.Configuration;
using Robust.Shared.Containers;
using Robust.Shared.Map;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Controllers;
using Robust.Shared.Timing;
using Robust.Shared.Utility;
using System.Diagnostics.CodeAnalysis;
using Content.Shared.Mobs.Systems;
using Content.Shared.Mech.Components;
using Content.Shared.Parallax.Biomes;
using Robust.Shared.Map.Components;
using Robust.Shared.Noise;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Systems;

namespace Content.Shared.Movement.Systems
{
    /// <summary>
    ///     Handles player and NPC mob movement.
    ///     NPCs are handled server-side only.
    /// </summary>
    public abstract partial class SharedMoverController : VirtualController
    {
        [Dependency] private readonly IConfigurationManager _configManager = default!;
        [Dependency] protected readonly IGameTiming Timing = default!;
        [Dependency] private readonly IMapManager _mapManager = default!;
        [Dependency] private readonly ITileDefinitionManager _tileDefinitionManager = default!;
        [Dependency] private readonly InventorySystem _inventory = default!;
        [Dependency] private readonly SharedContainerSystem _container = default!;
        [Dependency] private readonly EntityLookupSystem _lookup = default!;
        [Dependency] private readonly SharedGravitySystem _gravity = default!;
        [Dependency] private readonly MobStateSystem _mobState = default!;
        [Dependency] private readonly SharedAudioSystem _audio = default!;
        [Dependency] private readonly SharedTransformSystem _transform = default!;
        [Dependency] private readonly TagSystem _tags = default!;

        private const float StepSoundMoveDistanceRunning = 2;
        private const float StepSoundMoveDistanceWalking = 1.5f;

        private const float FootstepVariation = 0f;
        private const float FootstepVolume = 3f;
        private const float FootstepWalkingAddedVolumeMultiplier = 0f;

        protected ISawmill Sawmill = default!;

        /// <summary>
        /// <see cref="CCVars.StopSpeed"/>
        /// </summary>
        private float _stopSpeed;

        private bool _relativeMovement;

        /// <summary>
        /// Cache the mob movement calculation to re-use elsewhere.
        /// </summary>
        public Dictionary<EntityUid, bool> UsedMobMovement = new();

        public override void Initialize()
        {
            base.Initialize();
            Sawmill = Logger.GetSawmill("mover");
            InitializeFootsteps();
            InitializeInput();
            InitializeMob();
            InitializeRelay();
            _configManager.OnValueChanged(CCVars.RelativeMovement, SetRelativeMovement, true);
            _configManager.OnValueChanged(CCVars.StopSpeed, SetStopSpeed, true);
            UpdatesBefore.Add(typeof(TileFrictionController));
        }

        private void SetRelativeMovement(bool value) => _relativeMovement = value;
        private void SetStopSpeed(float value) => _stopSpeed = value;

        public override void Shutdown()
        {
            base.Shutdown();
            ShutdownInput();
            _configManager.UnsubValueChanged(CCVars.RelativeMovement, SetRelativeMovement);
            _configManager.UnsubValueChanged(CCVars.StopSpeed, SetStopSpeed);
        }

        public override void UpdateAfterSolve(bool prediction, float frameTime)
        {
            base.UpdateAfterSolve(prediction, frameTime);
            UsedMobMovement.Clear();
        }

        /// <summary>
        ///     Movement while considering actionblockers, weightlessness, etc.
        /// </summary>
        protected void HandleMobMovement(
            EntityUid uid,
            InputMoverComponent mover,
            EntityUid physicsUid,
            PhysicsComponent physicsComponent,
            TransformComponent xform,
            float frameTime,
            EntityQuery<TransformComponent> xformQuery,
            EntityQuery<InputMoverComponent> moverQuery,
            EntityQuery<MobMoverComponent> mobMoverQuery,
            EntityQuery<MovementRelayTargetComponent> relayTargetQuery,
            EntityQuery<SharedPullableComponent> pullableQuery,
            EntityQuery<MovementSpeedModifierComponent> modifierQuery)
        {
            bool canMove = mover.CanMove;
            if (relayTargetQuery.TryGetComponent(uid, out var relayTarget) && relayTarget.Entities.Count > 0)
            {
                DebugTools.Assert(relayTarget.Entities.Count <= 1, "Multiple relayed movers are not supported at the moment");

                bool found = false;
                foreach (var ent in relayTarget.Entities)
                {
                    if (_mobState.IsIncapacitated(ent) || !moverQuery.TryGetComponent(ent, out var relayedMover))
                        continue;

                    found = true;
                    mover.RelativeEntity = relayedMover.RelativeEntity;
                    mover.RelativeRotation = relayedMover.RelativeRotation;
                    mover.TargetRelativeRotation = relayedMover.TargetRelativeRotation;
                    break;
                }

                // lets just hope that this is the same entity that set the movement keys/direction.
                canMove &= found;
            }

            // Update relative movement
            if (mover.LerpTarget < Timing.CurTime)
            {
                if (TryUpdateRelative(mover, xform, xformQuery))
                {
                    Dirty(mover);
                }
            }

            var angleDiff = Angle.ShortestDistance(mover.RelativeRotation, mover.TargetRelativeRotation);

            // if we've just traversed then lerp to our target rotation.
            if (!angleDiff.EqualsApprox(Angle.Zero, 0.001))
            {
                var adjustment = angleDiff * 5f * frameTime;
                var minAdjustment = 0.01 * frameTime;

                if (angleDiff < 0)
                {
                    adjustment = Math.Min(adjustment, -minAdjustment);
                    adjustment = Math.Clamp(adjustment, angleDiff, -angleDiff);
                }
                else
                {
                    adjustment = Math.Max(adjustment, minAdjustment);
                    adjustment = Math.Clamp(adjustment, -angleDiff, angleDiff);
                }

                mover.RelativeRotation += adjustment;
                mover.RelativeRotation.FlipPositive();
                Dirty(mover);
            }
            else if (!angleDiff.Equals(Angle.Zero))
            {
                mover.TargetRelativeRotation.FlipPositive();
                mover.RelativeRotation = mover.TargetRelativeRotation;
                Dirty(mover);
            }

            if (!canMove
                || physicsComponent.BodyStatus != BodyStatus.OnGround
                || pullableQuery.TryGetComponent(uid, out var pullable) && pullable.BeingPulled)
            {
                UsedMobMovement[uid] = false;
                return;
            }

            UsedMobMovement[uid] = true;
            // Specifically don't use mover.Owner because that may be different to the actual physics body being moved.
            var weightless = _gravity.IsWeightless(physicsUid, physicsComponent, xform);
            var (walkDir, sprintDir) = GetVelocityInput(mover);
            var touching = false;

            // Handle wall-pushes.
            if (weightless)
            {
                if (xform.GridUid != null)
                    touching = true;

                if (!touching)
                {
                    var ev = new CanWeightlessMoveEvent();
                    RaiseLocalEvent(uid, ref ev);
                    // No gravity: is our entity touching anything?
                    touching = ev.CanMove;

                    if (!touching && TryComp<MobMoverComponent>(uid, out var mobMover))
                        touching |= IsAroundCollider(PhysicsSystem, xform, mobMover, physicsUid, physicsComponent);
                }
            }

            // Regular movement.
            // Target velocity.
            // This is relative to the map / grid we're on.
            var moveSpeedComponent = modifierQuery.CompOrNull(uid);

            var walkSpeed = moveSpeedComponent?.CurrentWalkSpeed ?? MovementSpeedModifierComponent.DefaultBaseWalkSpeed;
            var sprintSpeed = moveSpeedComponent?.CurrentSprintSpeed ?? MovementSpeedModifierComponent.DefaultBaseSprintSpeed;

            var total = walkDir * walkSpeed + sprintDir * sprintSpeed;

            var parentRotation = GetParentGridAngle(mover, xformQuery);
            var worldTotal = _relativeMovement ? parentRotation.RotateVec(total) : total;

            DebugTools.Assert(MathHelper.CloseToPercent(total.Length, worldTotal.Length));

            var velocity = physicsComponent.LinearVelocity;
            float friction;
            float weightlessModifier;
            float accel;

            if (weightless)
            {
                if (worldTotal != Vector2.Zero && touching)
                    friction = moveSpeedComponent?.WeightlessFriction ?? MovementSpeedModifierComponent.DefaultWeightlessFriction;
                else
                    friction = moveSpeedComponent?.WeightlessFrictionNoInput ?? MovementSpeedModifierComponent.DefaultWeightlessFrictionNoInput;

                weightlessModifier = moveSpeedComponent?.WeightlessModifier ?? MovementSpeedModifierComponent.DefaultWeightlessModifier;
                accel = moveSpeedComponent?.WeightlessAcceleration ?? MovementSpeedModifierComponent.DefaultWeightlessAcceleration;
            }
            else
            {
                if (worldTotal != Vector2.Zero || moveSpeedComponent?.FrictionNoInput == null)
                {
                    friction = moveSpeedComponent?.Friction ?? MovementSpeedModifierComponent.DefaultFriction;
                }
                else
                {
                    friction = moveSpeedComponent.FrictionNoInput ?? MovementSpeedModifierComponent.DefaultFrictionNoInput;
                }

                weightlessModifier = 1f;
                accel = moveSpeedComponent?.Acceleration ?? MovementSpeedModifierComponent.DefaultAcceleration;
            }

            var minimumFrictionSpeed = moveSpeedComponent?.MinimumFrictionSpeed ?? MovementSpeedModifierComponent.DefaultMinimumFrictionSpeed;
            Friction(minimumFrictionSpeed, frameTime, friction, ref velocity);

            if (worldTotal != Vector2.Zero)
            {
                var worldRot = _transform.GetWorldRotation(xform);
                _transform.SetLocalRotation(xform, xform.LocalRotation + worldTotal.ToWorldAngle() - worldRot);
                // TODO apparently this results in a duplicate move event because "This should have its event run during
                // island solver"??. So maybe SetRotation needs an argument to avoid raising an event?

                if (!weightless && mobMoverQuery.TryGetComponent(uid, out var mobMover) &&
                    TryGetSound(weightless, uid, mover, mobMover, xform, out var sound))
                {
                    var soundModifier = mover.Sprinting ? 1.0f : FootstepWalkingAddedVolumeMultiplier;

                    var audioParams = sound.Params
                        .WithVolume(FootstepVolume * soundModifier)
                        .WithVariation(sound.Params.Variation ?? FootstepVariation);

                    // If we're a relay target then predict the sound for all relays.
                    if (relayTarget != null)
                    {
                        foreach (var ent in relayTarget.Entities)
                        {
                            _audio.PlayPredicted(sound, uid, ent, audioParams);
                        }
                    }
                    else
                    {
                        _audio.PlayPredicted(sound, uid, uid, audioParams);
                    }
                }
            }

            worldTotal *= weightlessModifier;

            if (!weightless || touching)
                Accelerate(ref velocity, in worldTotal, accel, frameTime);

            PhysicsSystem.SetLinearVelocity(physicsUid, velocity, body: physicsComponent);

            // Ensures that players do not spiiiiiiin
            PhysicsSystem.SetAngularVelocity(physicsUid, 0, body: physicsComponent);
        }

        private void Friction(float minimumFrictionSpeed, float frameTime, float friction, ref Vector2 velocity)
        {
            var speed = velocity.Length;

            if (speed < minimumFrictionSpeed)
                return;

            var drop = 0f;

            var control = MathF.Max(_stopSpeed, speed);
            drop += control * friction * frameTime;

            var newSpeed = MathF.Max(0f, speed - drop);

            if (newSpeed.Equals(speed))
                return;

            newSpeed /= speed;
            velocity *= newSpeed;
        }

        private void Accelerate(ref Vector2 currentVelocity, in Vector2 velocity, float accel, float frameTime)
        {
            var wishDir = velocity != Vector2.Zero ? velocity.Normalized : Vector2.Zero;
            var wishSpeed = velocity.Length;

            var currentSpeed = Vector2.Dot(currentVelocity, wishDir);
            var addSpeed = wishSpeed - currentSpeed;

            if (addSpeed <= 0f)
                return;

            var accelSpeed = accel * frameTime * wishSpeed;
            accelSpeed = MathF.Min(accelSpeed, addSpeed);

            currentVelocity += wishDir * accelSpeed;
        }

        public bool UseMobMovement(EntityUid uid)
        {
            return UsedMobMovement.TryGetValue(uid, out var used) && used;
        }

        /// <summary>
        ///     Used for weightlessness to determine if we are near a wall.
        /// </summary>
        private bool IsAroundCollider(SharedPhysicsSystem broadPhaseSystem, TransformComponent transform, MobMoverComponent mover, EntityUid physicsUid, PhysicsComponent collider)
        {
            var enlargedAABB = _lookup.GetWorldAABB(physicsUid, transform).Enlarged(mover.GrabRangeVV);

            foreach (var otherCollider in broadPhaseSystem.GetCollidingEntities(transform.MapID, enlargedAABB))
            {
                if (otherCollider == collider) continue; // Don't try to push off of yourself!

                // Only allow pushing off of anchored things that have collision.
                if (otherCollider.BodyType != BodyType.Static ||
                    !otherCollider.CanCollide ||
                    ((collider.CollisionMask & otherCollider.CollisionLayer) == 0 &&
                    (otherCollider.CollisionMask & collider.CollisionLayer) == 0) ||
                    (TryComp(otherCollider.Owner, out SharedPullableComponent? pullable) && pullable.BeingPulled))
                {
                    continue;
                }

                return true;
            }

            return false;
        }

        protected abstract bool CanSound();

        private bool TryGetSound(bool weightless, EntityUid uid, InputMoverComponent mover, MobMoverComponent mobMover, TransformComponent xform, [NotNullWhen(true)] out SoundSpecifier? sound)
        {
            sound = null;

            if (!CanSound() || !_tags.HasTag(uid, "FootstepSound"))
                return false;

            var coordinates = xform.Coordinates;
            var distanceNeeded = mover.Sprinting ? StepSoundMoveDistanceRunning : StepSoundMoveDistanceWalking;

            // Handle footsteps.
            if (!weightless)
            {
                // Can happen when teleporting between grids.
                if (!coordinates.TryDistance(EntityManager, mobMover.LastPosition, out var distance) ||
                    distance > distanceNeeded)
                {
                    mobMover.StepSoundDistance = distanceNeeded;
                }
                else
                {
                    mobMover.StepSoundDistance += distance;
                }
            }
            else
            {
                // In space no one can hear you squeak
                return false;
            }

            mobMover.LastPosition = coordinates;

            if (mobMover.StepSoundDistance < distanceNeeded)
                return false;

            mobMover.StepSoundDistance -= distanceNeeded;

            if (TryComp<FootstepModifierComponent>(uid, out var moverModifier))
            {
                sound = moverModifier.Sound;
                return true;
            }

            if (_inventory.TryGetSlotEntity(uid, "shoes", out var shoes) &&
                TryComp<FootstepModifierComponent>(shoes, out var modifier))
            {
                sound = modifier.Sound;
                return true;
            }

            return TryGetFootstepSound(uid, xform, shoes != null, out sound);
        }

        private bool TryGetFootstepSound(EntityUid uid, TransformComponent xform, bool haveShoes, [NotNullWhen(true)] out SoundSpecifier? sound)
        {
            sound = null;

            // Fallback to the map?
            if (!_mapManager.TryGetGrid(xform.GridUid, out var grid))
            {
                if (TryComp<FootstepModifierComponent>(xform.MapUid, out var modifier))
                {
                    sound = modifier.Sound;
                    return true;
                }

                return false;
            }

            var position = grid.LocalToTile(xform.Coordinates);
            var soundEv = new GetFootstepSoundEvent(uid);

            // If the coordinates have a FootstepModifier component
            // i.e. component that emit sound on footsteps emit that sound
            var anchored = grid.GetAnchoredEntitiesEnumerator(position);

            while (anchored.MoveNext(out var maybeFootstep))
            {
                RaiseLocalEvent(maybeFootstep.Value, ref soundEv);

                if (soundEv.Sound != null)
                {
                    sound = soundEv.Sound;
                    return true;
                }

                if (TryComp<FootstepModifierComponent>(maybeFootstep, out var footstep))
                {
                    sound = footstep.Sound;
                    return true;
                }
            }

            if (!grid.TryGetTileRef(position, out var tileRef))
            {
                sound = null;
                return false;
            }

            // Walking on a tile.
            var def = (ContentTileDefinition) _tileDefinitionManager[tileRef.Tile.TypeId];
            sound = haveShoes ? def.FootstepSounds : def.BarestepSounds;
            return sound != null;
        }
    }
}
