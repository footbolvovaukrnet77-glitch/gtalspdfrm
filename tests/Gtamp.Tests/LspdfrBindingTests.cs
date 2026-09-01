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

        /// <summary>Mirrors LSPDFR's CalloutAcceptanceState.</summary>
        public enum FakeAcceptanceState
        {
            None,
            Pending,
            Running,
            Ended,
        }

        /// <summary>
        /// A build that has everything the observer looks for. Every signature here
        /// matches the public metadata of LSPD_First_Response.dll 0.4.9695.26411.
        /// </summary>
        public static class CompleteFunctions
        {
            public static bool OnDuty;
            public static bool CalloutRunning;
            public static bool PulloverActive;
            public static FakeHandle? Callout;
            public static string CalloutName = "Traffic Accident";
            public static FakeAcceptanceState AcceptanceState = FakeAcceptanceState.Running;
            public static FakeHandle? Pursuit;
            public static bool PursuitCalledIn;
            public static bool PursuitRunning;

            public static string GetVersion() => "0.4.9";

            public static bool IsPlayerAvailableForCalls() => OnDuty;

            /// <summary>Mirrors OnDutyStateChangedEventHandler: void(bool).</summary>
            public delegate void DutyHandler(bool onDuty);

            public static event DutyHandler? OnOnDutyStateChanged;

            public static void RaiseDuty(bool onDuty) => OnOnDutyStateChanged?.Invoke(onDuty);

            public static int SubscriberCount() => OnOnDutyStateChanged?.GetInvocationList().Length ?? 0;

            public static bool IsCalloutRunning() => CalloutRunning;

            public static bool IsPlayerPerformingPullover() => PulloverActive;

            public static FakeHandle? GetCurrentCallout() => Callout;

            public static string GetCalloutFriendlyName(FakeHandle handle) => CalloutName;

            public static FakeAcceptanceState GetCalloutAcceptanceState(FakeHandle handle) => AcceptanceState;

            public static FakeHandle? GetActivePursuit() => Pursuit;

            public static bool IsPursuitCalledIn(FakeHandle handle) => PursuitCalledIn;

            public static bool IsPursuitStillRunning(FakeHandle handle) => PursuitRunning;
        }

        /// <summary>
        /// A build whose on-duty event carries a different delegate. Binding by name
        /// alone would either throw or attach something that never fires.
        /// </summary>
        public static class WrongShapeEventFunctions
        {
            public delegate void OddHandler(string reason, int code);

            public static event OddHandler? OnOnDutyStateChanged;

            public static void Silence() => OnOnDutyStateChanged?.Invoke("x", 0);

            public static bool IsPlayerAvailableForCalls() => true;

            public static bool IsCalloutRunning() => false;
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
            CompleteFunctions.OnDuty = false;
            CompleteFunctions.CalloutRunning = false;
            CompleteFunctions.PulloverActive = false;
            CompleteFunctions.Callout = null;
            CompleteFunctions.CalloutName = "Traffic Accident";
            CompleteFunctions.AcceptanceState = FakeAcceptanceState.Running;
            CompleteFunctions.Pursuit = null;
            CompleteFunctions.PursuitCalledIn = false;
            CompleteFunctions.PursuitRunning = false;
        }

        [Fact]
        public void EveryProbeBindsAgainstABuildThatHasTheDocumentedApi()
        {
            Reset();
            using var observer = new LspdfrObserver();
            observer.BindTo(typeof(CompleteFunctions), "assembly-version");

            Assert.True(observer.IsAvailable);
            Assert.Empty(observer.MissingProbes);
            // onDuty is not in this count: it comes from the event, not a probe.
            Assert.Equal(7, observer.BoundProbeCount);

            // GetVersion() is preferred over the assembly version: the number a player
            // quotes from the LSPDFR menu is the one worth reporting.
            Assert.Equal("0.4.9", observer.Version);
        }

        [Fact]
        public void AHandleIsHandedStraightBackWithoutBeingInspected()
        {
            Reset();
            using var observer = new LspdfrObserver();
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
            using var observer = new LspdfrObserver();
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
            using var observer = new LspdfrObserver();
            observer.BindTo(typeof(SparseFunctions), "v");

            Assert.True(observer.IsAvailable);
            Assert.Equal(1, observer.BoundProbeCount);

            // The whole point: an operator can see which probes this LSPDFR build did
            // not offer, instead of wondering why a field is always empty.
            Assert.Contains(observer.MissingProbes, m => m.Contains("IsPlayerPerformingPullover"));
            Assert.Contains(observer.MissingProbes, m => m.Contains("IsPursuitCalledIn"));
            Assert.Contains(observer.MissingProbes, m => m.Contains("GetCalloutFriendlyName"));
            Assert.Contains(observer.MissingProbes, m => m.Contains("GetVersion"));

            // And what did bind still works.
            Assert.Contains("callout.running=false", observer.Describe());
        }

        [Fact]
        public void AThrowingProbeIsReportedAndDoesNotTakeThePollLoopDown()
        {
            using var observer = new LspdfrObserver();
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
            using var observer = new LspdfrObserver();
            observer.BindTo(typeof(AmbiguousFunctions), "v");

            Assert.Contains(observer.MissingProbes, m => m.Contains("IsPursuitCalledIn"));

            // The unambiguous sibling still binds, so refusing the ambiguous one costs
            // exactly that one probe rather than the whole pursuit group.
            Assert.Contains("pursuit.active=handle", observer.Describe());
            Assert.DoesNotContain("pursuit.calledIn", observer.Describe());
        }

        [Fact]
        public void TheCalloutNameCrossesTheWire()
        {
            // The capability this whole exercise was for. GetCurrentCallout() takes no
            // arguments and yields the handle GetCalloutFriendlyName wants, so the name
            // of the callout a player is on is readable by polling -- not just the fact
            // that some callout is running.
            Reset();
            using var observer = new LspdfrObserver();
            observer.BindTo(typeof(CompleteFunctions), "v");

            Assert.Contains("callout.name=none", observer.Describe());

            CompleteFunctions.Callout = new FakeHandle();
            CompleteFunctions.CalloutRunning = true;
            CompleteFunctions.CalloutName = "Traffic Accident";
            CompleteFunctions.AcceptanceState = FakeAcceptanceState.Running;

            string described = observer.Describe();
            Assert.Contains("callout.name=Traffic Accident", described);
            Assert.Contains("callout.state=Running", described);
        }

        [Theory]
        [InlineData("Shots Fired; Officer Down", "Shots Fired  Officer Down")]
        [InlineData("code=3 response", "code 3 response")]
        [InlineData("line\nbreak", "line break")]
        public void ACalloutNameCannotBreakTheWireFormat(string raw, string expected)
        {
            // A callout's friendly name is free text from whichever third-party callout
            // plugin the player installed, and the state string is key=value;key=value.
            // One semicolon would split a field on the far side; one equals sign would
            // move the key boundary. Every other probe returns a bool, an enum or a
            // handle, so this is the first value that could do it.
            Reset();
            CompleteFunctions.Callout = new FakeHandle();
            CompleteFunctions.CalloutName = raw.Replace("\\n", "\n");

            using var observer = new LspdfrObserver();
            observer.BindTo(typeof(CompleteFunctions), "v");

            string described = observer.Describe();
            Assert.Contains("callout.name=" + expected, described);

            // The field count must be exactly what the observer intended to send:
            // available, version, and the eight probes. A separator that survived
            // sanitisation would show up here as an extra field.
            Assert.Equal(10, described.Split(';').Length);
        }

        [Fact]
        public void AnAbsurdlyLongCalloutNameIsCapped()
        {
            Reset();
            CompleteFunctions.Callout = new FakeHandle();
            CompleteFunctions.CalloutName = new string('x', 500);

            using var observer = new LspdfrObserver();
            observer.BindTo(typeof(CompleteFunctions), "v");

            Assert.Contains("callout.name=" + new string('x', 64) + ";", observer.Describe());
        }

        [Fact]
        public void OnDutyComesFromTheEventWhenItBinds()
        {
            // The one LSPDFR event whose delegate names no LSPDFR type -- void(bool) --
            // so it can be bound with an ordinary delegate rather than one emitted at
            // runtime. That makes the value exact instead of sampled.
            Reset();
            using var observer = new LspdfrObserver();
            observer.BindTo(typeof(CompleteFunctions), "v");

            Assert.Contains("onDuty.source=event", observer.Describe());
            Assert.DoesNotContain(observer.MissingProbes, m => m.Contains("OnOnDutyStateChanged"));

            CompleteFunctions.RaiseDuty(true);
            Assert.Equal("onDuty=true", observer.PollChanges());

            CompleteFunctions.RaiseDuty(false);
            Assert.Equal("onDuty=false", observer.PollChanges());

            // And it stays quiet when nothing changed.
            Assert.Equal(string.Empty, observer.PollChanges());
        }

        [Fact]
        public void AnEventOfTheWrongShapeFallsBackToThePoll()
        {
            // A delegate bound by name alone either throws or, far worse, attaches and
            // never fires -- a subscription that looks successful and silently reports
            // nothing. The shape is checked, and the poll takes over.
            using var observer = new LspdfrObserver();
            observer.BindTo(typeof(WrongShapeEventFunctions), "v");

            string described = observer.Describe();
            Assert.Contains("onDuty.source=poll", described);
            Assert.Contains("onDuty=true", described);
            Assert.Contains(observer.MissingProbes, m => m.Contains("unexpected delegate shape"));

            // Raising it must not reach the observer at all.
            WrongShapeEventFunctions.Silence();
            Assert.Equal(string.Empty, observer.PollChanges());
        }

        [Fact]
        public void DisposeDetachesFromLspdfr()
        {
            // RPH reloads plugins. A handler left attached keeps the observer alive
            // inside LSPDFR and goes on being called after this assembly has stopped,
            // which is the shape of a crash on reload rather than a leak.
            Reset();
            int before = CompleteFunctions.SubscriberCount();

            var observer = new LspdfrObserver();
            observer.BindTo(typeof(CompleteFunctions), "v");
            Assert.Equal(before + 1, CompleteFunctions.SubscriberCount());

            observer.Dispose();
            Assert.Equal(before, CompleteFunctions.SubscriberCount());

            // Disposing twice is not an error, and neither is raising afterwards.
            observer.Dispose();
            CompleteFunctions.RaiseDuty(true);
        }

        [Fact]
        public void AnAbsentLspdfrIsNotAnError()
        {
            // The configuration the framework must never break in: no LSPDFR at all.
            using var observer = new LspdfrObserver();
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
            using var observer = new LspdfrObserver();
            observer.BindTo(typeof(CompleteFunctions), "v");

            // Only these two are genuinely absent from the assembly. GetCurrentCallout
            // and GetCurrentPullover were removed in an earlier pass on the strength of
            // the XML documentation alone and are back, because they exist -- they just
            // carry no doc comment.
            var retired = new[] { "IsPlayerAvailable", "GetPlayerState" };

            foreach (string name in retired)
            {
                Assert.DoesNotContain(observer.MissingProbes, m => m.Contains(name));
            }

            Assert.Empty(observer.MissingProbes);
        }
    }
}
