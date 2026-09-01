using System;
using System.Collections.Generic;
using Gtamp.Shared.Integration;
using Xunit;

namespace Gtamp.Tests
{
    /// <summary>
    /// The LSPDFR binding, tested for the first time.
    /// <para>
    /// It could not be tested while it lived in the RPH bridge, because that assembly
    /// references RAGE Plugin Hook. It touches no <c>Rage</c> type, only
    /// <c>System.Reflection</c>, so moving it into the shared assembly made it
    /// ordinary reflection over a type — and a type is something a test can supply.
    /// </para>
    /// <para>
    /// The stand-ins below mirror the signatures in <c>LSPD_First_Response.XML</c>,
    /// the documentation LSPDFR ships beside its assembly: <c>IsCalloutRunning()</c>
    /// and <c>IsPlayerPerformingPullover()</c> take nothing, <c>GetActivePursuit()</c>
    /// returns an opaque handle, and <c>IsPursuitCalledIn</c> /
    /// <c>IsPursuitStillRunning</c> take that handle back.
    /// </para>
    /// <para>
    /// What this does <em>not</em> prove is that the real LSPDFR behaves this way. It
    /// proves the binding, the handle plumbing and the degradation are right, which is
    /// everything on this side of the boundary; the other side needs Windows, GTA V,
    /// RPH and LSPDFR.
    /// </para>
    /// </summary>
    public class LspdfrBindingTests
    {
        /// <summary>Stands in for LSPDFR's opaque <c>LHandle</c>.</summary>
        public sealed class FakeHandle
        {
        }

        /// <summary>A build that has everything the observer looks for.</summary>
        public static class CompleteFunctions
        {
            public static bool CalloutRunning;
            public static bool PulloverActive;
            public static FakeHandle? Pursuit;
            public static bool PursuitCalledIn;
            public static bool PursuitRunning;

            public static string GetVersion() => "0.4.9";

            public static bool IsCalloutRunning() => CalloutRunning;

            public static bool IsPlayerPerformingPullover() => PulloverActive;

            public static FakeHandle? GetActivePursuit() => Pursuit;

            public static bool IsPursuitCalledIn(FakeHandle handle) => PursuitCalledIn;

            public static bool IsPursuitStillRunning(FakeHandle handle) => PursuitRunning;
        }

        /// <summary>An older build missing most of the surface.</summary>
        public static class SparseFunctions
        {
            public static bool IsCalloutRunning() => false;
        }

        /// <summary>A build whose getter throws, which is LSPDFR's prerogative.</summary>
        public static class ThrowingFunctions
        {
            public static bool IsCalloutRunning() => throw new InvalidOperationException("boom");
        }

        /// <summary>Two single-argument overloads of one name — genuinely ambiguous.</summary>
        public static class AmbiguousFunctions
        {
            public static FakeHandle? GetActivePursuit() => new FakeHandle();

            public static bool IsPursuitCalledIn(FakeHandle handle) => true;

            public static bool IsPursuitCalledIn(string handle) => false;
        }

        private static void Reset()
        {
            CompleteFunctions.CalloutRunning = false;
            CompleteFunctions.PulloverActive = false;
            CompleteFunctions.Pursuit = null;
            CompleteFunctions.PursuitCalledIn = false;
            CompleteFunctions.PursuitRunning = false;
        }

        [Fact]
        public void EveryProbeBindsAgainstABuildThatHasTheDocumentedApi()
        {
            Reset();
            var observer = new LspdfrObserver();
            observer.BindTo(typeof(CompleteFunctions), "assembly-version");

            Assert.True(observer.IsAvailable);
            Assert.Empty(observer.MissingProbes);
            Assert.Equal(5, observer.BoundProbeCount);

            // GetVersion() is preferred over the assembly version: the number a player
            // quotes from the LSPDFR menu is the one worth reporting.
            Assert.Equal("0.4.9", observer.Version);
        }

        [Fact]
        public void AHandleIsHandedStraightBackWithoutBeingInspected()
        {
            Reset();
            var observer = new LspdfrObserver();
            observer.BindTo(typeof(CompleteFunctions), "v");

            // No pursuit: the derived probes must not call through a null handle.
            string quiet = observer.Describe();
            Assert.Contains("pursuit.active=none", quiet);
            Assert.Contains("pursuit.calledIn=none", quiet);
            Assert.DoesNotContain("error:", quiet);

            CompleteFunctions.Pursuit = new FakeHandle();
            CompleteFunctions.PursuitCalledIn = true;
            CompleteFunctions.PursuitRunning = true;

            string active = observer.Describe();

            // The handle itself is opaque and stays that way -- only its presence is
            // reported. What crosses the wire is what LSPDFR says about it.
            Assert.Contains("pursuit.active=handle", active);
            Assert.Contains("pursuit.calledIn=true", active);
            Assert.Contains("pursuit.running=true", active);
        }

        [Fact]
        public void OnlyChangedProbesAreReported()
        {
            Reset();
            var observer = new LspdfrObserver();
            observer.BindTo(typeof(CompleteFunctions), "v");

            observer.Describe();
            Assert.Equal(string.Empty, observer.PollChanges());

            CompleteFunctions.CalloutRunning = true;
            Assert.Equal("callout.running=true", observer.PollChanges());
            Assert.Equal(string.Empty, observer.PollChanges());
        }

        [Fact]
        public void AMissingProbeIsNamedRatherThanSilentlyAbsent()
        {
            var observer = new LspdfrObserver();
            observer.BindTo(typeof(SparseFunctions), "v");

            Assert.True(observer.IsAvailable);
            Assert.Equal(1, observer.BoundProbeCount);

            // The whole point: an operator can see which probes this LSPDFR build did
            // not offer, instead of wondering why a field is always empty.
            Assert.Contains(observer.MissingProbes, m => m.Contains("IsPlayerPerformingPullover"));
            Assert.Contains(observer.MissingProbes, m => m.Contains("IsPursuitCalledIn"));
            Assert.Contains(observer.MissingProbes, m => m.Contains("GetVersion"));

            // And what did bind still works.
            Assert.Contains("callout.running=false", observer.Describe());
        }

        [Fact]
        public void AThrowingProbeIsReportedAndDoesNotTakeThePollLoopDown()
        {
            var observer = new LspdfrObserver();
            observer.BindTo(typeof(ThrowingFunctions), "v");

            string described = observer.Describe();

            Assert.Contains("callout.running=error:InvalidOperationException", described);
            Assert.Contains("available=true", described);
        }

        [Fact]
        public void AnAmbiguousOverloadIsLeftUnboundRatherThanGuessed()
        {
            // Binding by arity alone would pick one of two single-argument overloads at
            // random and hand LSPDFR the wrong argument type. Refusing to bind is the
            // answer that stays honest.
            var observer = new LspdfrObserver();
            observer.BindTo(typeof(AmbiguousFunctions), "v");

            Assert.Contains(observer.MissingProbes, m => m.Contains("IsPursuitCalledIn"));

            // The unambiguous sibling still binds, so refusing the ambiguous one costs
            // exactly that one probe rather than the whole pursuit group.
            Assert.Contains("pursuit.active=handle", observer.Describe());
            Assert.DoesNotContain("pursuit.calledIn", observer.Describe());
        }

        [Fact]
        public void AnAbsentLspdfrIsNotAnError()
        {
            // The configuration the framework must never break in: no LSPDFR at all.
            var observer = new LspdfrObserver();
            observer.Bind();

            Assert.False(observer.IsAvailable);
            Assert.Equal("absent", observer.Version);
            Assert.Equal("available=false", observer.Describe());
            Assert.Equal(string.Empty, observer.PollChanges());
        }

        [Fact]
        public void TheFourProbeNamesTheDocumentationDisprovedAreGone()
        {
            // IsPlayerAvailable, GetCurrentCallout, GetCurrentPullover and
            // GetPlayerState were bound by name before LSPD_First_Response.XML was
            // checked; none of them exists in the documented API, so all four could
            // only ever have landed in MissingProbes. This asserts they are not asked
            // for any more -- a probe that can never bind is noise in the one list an
            // operator reads to find out what is genuinely unavailable.
            var observer = new LspdfrObserver();
            observer.BindTo(typeof(CompleteFunctions), "v");

            var retired = new[]
            {
                "IsPlayerAvailable", "GetCurrentCallout", "GetCurrentPullover", "GetPlayerState",
            };

            foreach (string name in retired)
            {
                Assert.DoesNotContain(observer.MissingProbes, m => m.Contains(name));
            }

            Assert.Empty(observer.MissingProbes);
        }
    }
}
