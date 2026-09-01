using System;
using System.Collections.Generic;
using Gtamp.Shared.Protocol;
using Xunit;

namespace Gtamp.Tests
{
    /// <summary>
    /// Keeps <see cref="NetMessageType"/> honest about which ids are actually spoken.
    /// <para>
    /// A message id that is declared but never sent reads exactly like one that
    /// works, which is how <c>ModManifest</c> and <c>ModCompatibilityReport</c> sat
    /// in the table for twelve phases looking like a negotiation protocol. They are
    /// not sent and never were: negotiation happens inside the handshake, because it
    /// has to be settled before a client is admitted rather than after.
    /// </para>
    /// <para>
    /// Reserving ids ahead of use is right — renumbering later to close a gap breaks
    /// every client already speaking the format — but only while the distinction is
    /// visible. So the classification is enforced here rather than trusted, the way
    /// <see cref="EntityTypeTests"/> enforces the entity ids.
    /// </para>
    /// </summary>
    public class NetMessageTypeTests
    {
        /// <summary>Ids this build actually puts on the wire.</summary>
        private static readonly NetMessageType[] Spoken =
        {
            NetMessageType.ConnectRequest, NetMessageType.ConnectAccept, NetMessageType.ConnectReject,
            NetMessageType.Disconnect, NetMessageType.KeepAlive, NetMessageType.Fragment,
            NetMessageType.ConnectChallenge, NetMessageType.ConnectProof,
            NetMessageType.Ping, NetMessageType.Pong,
            NetMessageType.ClientStateUpdate, NetMessageType.Snapshot, NetMessageType.SnapshotAck,
            NetMessageType.ResyncRequest, NetMessageType.EntitySpawnRequest,
            NetMessageType.OwnedEntityUpdate, NetMessageType.EntityReleaseRequest,
            NetMessageType.DamageReport, NetMessageType.ModRpcRequest, NetMessageType.ModRpcResponse,
            NetMessageType.ModEvent, NetMessageType.EntityEvent, NetMessageType.ServerEvent,
            NetMessageType.ChatMessage, NetMessageType.WeaponShot,
            NetMessageType.AdminCommand, NetMessageType.SecurityNotice,
        };

        /// <summary>Ids held for later, or marking the bounds of a range. None is ever sent.</summary>
        private static readonly NetMessageType[] Reserved =
        {
            NetMessageType.None,
            NetMessageType.ModManifest,
            NetMessageType.ModCompatibilityReport,
            NetMessageType.ModMessageFirst,
            NetMessageType.ModMessageLast,
        };

        [Fact]
        public void EveryIdIsClassifiedExactlyOnce()
        {
            // The point of the test: a new message type has to be put in one list or
            // the other, and cannot quietly join the table looking supported.
            var spoken = new HashSet<NetMessageType>(Spoken);
            var reserved = new HashSet<NetMessageType>(Reserved);

            foreach (NetMessageType value in Enum.GetValues(typeof(NetMessageType)))
            {
                bool isSpoken = spoken.Contains(value);
                bool isReserved = reserved.Contains(value);

                Assert.True(
                    isSpoken || isReserved,
                    $"{value} is declared but classified neither as spoken nor as reserved. "
                    + "Add it to one list in NetMessageTypeTests, and say which it is in the enum.");

                Assert.False(isSpoken && isReserved, $"{value} is in both lists.");
            }
        }

        [Fact]
        public void NoTwoIdsShareAValue()
        {
            // Two names on one byte would make two different messages the same message,
            // and the receiver would dispatch whichever the switch reached first.
            var seen = new Dictionary<byte, string>();
            foreach (NetMessageType value in Enum.GetValues(typeof(NetMessageType)))
            {
                byte id = (byte)value;
                if (value == NetMessageType.ModMessageFirst || value == NetMessageType.ModMessageLast)
                {
                    // Range markers, not messages.
                    continue;
                }

                Assert.False(
                    seen.ContainsKey(id),
                    $"{value} and {(seen.TryGetValue(id, out string? other) ? other : "?")} share id 0x{id:X2}.");
                seen[id] = value.ToString();
            }
        }

        [Fact]
        public void TheModRangeIsAboveEverythingElse()
        {
            // Mods get the top of the byte so a new framework message can be added
            // without colliding with anything a third party already ships.
            foreach (NetMessageType value in Enum.GetValues(typeof(NetMessageType)))
            {
                if (value == NetMessageType.ModMessageFirst || value == NetMessageType.ModMessageLast)
                {
                    continue;
                }

                Assert.True(
                    (byte)value < (byte)NetMessageType.ModMessageFirst,
                    $"{value} = 0x{(byte)value:X2} is inside the range reserved for mods.");
            }
        }

        [Fact]
        public void TheReservedModNegotiationIdsAreNotSpoken()
        {
            // Stated as its own test so the reason survives: the manifest travels
            // inside ConnectRequest and the report inside ConnectAccept, because
            // negotiation has to be settled before a client is admitted.
            Assert.Contains(NetMessageType.ModManifest, Reserved);
            Assert.Contains(NetMessageType.ModCompatibilityReport, Reserved);
            Assert.DoesNotContain(NetMessageType.ModManifest, Spoken);
            Assert.DoesNotContain(NetMessageType.ModCompatibilityReport, Spoken);
        }
    }
}
