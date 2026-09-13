using Gtamp.Client.Entities;
using Gtamp.Shared.Core;
using Gtamp.Shared.Entities;
using Xunit;

namespace Gtamp.Tests
{
    /// <summary>
    /// Section 11's behaviour list: fleeing, chasing, attacking, surrender, arrest.
    /// <para>
    /// Every one of them was on the wire from the entity's first version and applied by
    /// nothing — a suspect the server had running away stood still on every machine but
    /// the one that decided it. The reason recorded against them was that they are "the
    /// AI itself, not state about it", which was true of a design where the server
    /// would have had to move the ped, and is not true of this one: the server owns the
    /// intent and each client's own GTA V executes it, because GTA V is the thing that
    /// knows where the pavements are.
    /// </para>
    /// </summary>
    public class NpcIntentTests
    {
        private static PedEntity Ped(PedBehaviourFlags behaviour, uint scenario = 0, int health = 200)
        {
            var ped = new PedEntity(new EntityId(11))
            {
                Behaviour = behaviour,
                ScenarioHash = scenario,
                Health = health,
            };

            return ped;
        }

        [Fact]
        public void AnAttackingNpcIsToldToFight()
        {
            Assert.Equal(
                NpcIntent.Fight,
                NpcIntentDirector.Decide(Ped(PedBehaviourFlags.Attacking), targetResolved: true));
        }

        [Fact]
        public void ChasingAndInCombatAreAlsoAFight()
        {
            Assert.Equal(
                NpcIntent.Fight,
                NpcIntentDirector.Decide(Ped(PedBehaviourFlags.Chasing), targetResolved: true));

            Assert.Equal(
                NpcIntent.Fight,
                NpcIntentDirector.Decide(Ped(PedBehaviourFlags.InCombat), targetResolved: true));
        }

        /// <summary>
        /// The same refusal melee needed. A ped told to fight nobody attacks in a
        /// direction nobody chose, which looks far worse than a ped standing still — and
        /// the target genuinely may not exist here, because it can be a player too far
        /// away to have been built on this client.
        /// </summary>
        [Fact]
        public void NoFightIsIssuedWithoutATargetThisClientHasBuilt()
        {
            Assert.Equal(
                NpcIntent.None,
                NpcIntentDirector.Decide(Ped(PedBehaviourFlags.Attacking), targetResolved: false));

            Assert.Equal(
                NpcIntent.None,
                NpcIntentDirector.Decide(Ped(PedBehaviourFlags.Fleeing), targetResolved: false));
        }

        [Fact]
        public void AFleeingNpcRunsFromItsTarget()
        {
            Assert.Equal(
                NpcIntent.Flee,
                NpcIntentDirector.Decide(Ped(PedBehaviourFlags.Fleeing), targetResolved: true));
        }

        /// <summary>
        /// The priority order, and it is not tidiness: a ped in handcuffs that is also
        /// issued a combat task does neither, because the two fight each other for the
        /// ped's task slot every time the state is re-applied.
        /// </summary>
        [Fact]
        public void ArrestBeatsSurrenderBeatsFlightBeatsAFight()
        {
            PedBehaviourFlags everything = PedBehaviourFlags.Arrested
                | PedBehaviourFlags.Surrendered
                | PedBehaviourFlags.Fleeing
                | PedBehaviourFlags.Attacking;

            Assert.Equal(NpcIntent.Arrest, NpcIntentDirector.Decide(Ped(everything), true));

            Assert.Equal(
                NpcIntent.Surrender,
                NpcIntentDirector.Decide(
                    Ped(PedBehaviourFlags.Surrendered | PedBehaviourFlags.Fleeing | PedBehaviourFlags.Attacking),
                    true));

            Assert.Equal(
                NpcIntent.Flee,
                NpcIntentDirector.Decide(Ped(PedBehaviourFlags.Fleeing | PedBehaviourFlags.Attacking), true));
        }

        [Fact]
        public void CuffedCountsAsArrestedBecauseItLooksTheSameOnAPed()
        {
            Assert.Equal(NpcIntent.Arrest, NpcIntentDirector.Decide(Ped(PedBehaviourFlags.Cuffed), true));
        }

        [Fact]
        public void AnIdleNpcWithAScenarioPlaysIt()
        {
            Assert.Equal(
                NpcIntent.Scenario,
                NpcIntentDirector.Decide(Ped(PedBehaviourFlags.None, scenario: GameHash.Joaat("WORLD_HUMAN_SMOKING")), true));
        }

        [Fact]
        public void AnIdleNpcWithNothingToDoIsLeftToTheOrdinaryPedDriver()
        {
            Assert.Equal(NpcIntent.None, NpcIntentDirector.Decide(Ped(PedBehaviourFlags.None), true));
        }

        /// <summary>
        /// A corpse is not fleeing, surrendering or under arrest whatever its flags
        /// still say. The flags outlive the death by however long it takes the killer's
        /// client to notice, and a body that stands up to put its hands in the air is
        /// the sort of thing players remember.
        /// </summary>
        [Fact]
        public void ADeadNpcIsToldNothingAtAll()
        {
            Assert.Equal(
                NpcIntent.None,
                NpcIntentDirector.Decide(Ped(PedBehaviourFlags.Fleeing, health: 0), targetResolved: true));

            var dead = Ped(PedBehaviourFlags.Attacking);
            dead.SetFlag(PlayerFlags.Dead, true);
            Assert.Equal(NpcIntent.None, NpcIntentDirector.Decide(dead, targetResolved: true));
        }
    }
}
