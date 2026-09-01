using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;

namespace Gtamp.Shared.Integration
{
    /// <summary>
    /// Watches LSPDFR through reflection and reports what it sees.
    /// <para>
    /// <b>Why reflection.</b> LSPDFR ships no redistributable assembly and its licence
    /// does not permit shipping one, so there is nothing to reference at compile time.
    /// The probe set below is bound by name against whatever build is actually loaded,
    /// and every probe that fails to bind is recorded and reported rather than being
    /// quietly absent.
    /// </para>
    /// <para>
    /// <b>The names are not guesses.</b> Every probe here was checked against the
    /// public metadata of <c>LSPD_First_Response.dll</c> 0.4.9695.26411 — the same
    /// surface reflection sees at runtime, which is how this class binds anyway.
    /// </para>
    /// <para>
    /// <b>Checking the XML documentation first was not enough, and that is worth
    /// recording.</b> An earlier pass used only <c>LSPD_First_Response.XML</c> and
    /// concluded that four names did not exist. Two of those four —
    /// <c>GetCurrentCallout</c> and <c>GetCurrentPullover</c> — do exist; they simply
    /// carry no doc comment, and the XML lists only members that have one. The caveat
    /// had been written down and it still produced a wrong answer, because absence of
    /// evidence was read as evidence of absence. The other two,
    /// <c>IsPlayerAvailable</c> and <c>GetPlayerState</c>, are genuinely absent.
    /// </para>
    /// <para>
    /// <c>GetCurrentCallout()</c> is what makes the callout's name reachable: it takes
    /// no arguments and returns the handle that <c>GetCalloutFriendlyName</c> wants,
    /// and that method is LSPDFR's own name for what it calls LSPDFR Sync.
    /// </para>
    /// <para>
    /// This type lives in the shared assembly rather than in the RPH bridge because it
    /// touches no <c>Rage</c> type — only <c>System.Reflection</c>. That is what lets
    /// it be tested against a stand-in <c>Functions</c> type with no game, no RPH and
    /// no LSPDFR present.
    /// </para>
    /// </summary>
    public sealed class LspdfrObserver : IDisposable
    {
        /// <summary>The simple assembly name LSPDFR loads under.</summary>
        public const string LspdfrAssemblyName = "LSPD First Response";

        /// <summary>The static API surface every probe binds against.</summary>
        public const string FunctionsTypeName = "LSPD_First_Response.Mod.API.Functions";

        private readonly List<Probe> _probes = new List<Probe>();
        private readonly List<string> _missing = new List<string>();
        private readonly Dictionary<string, string> _lastValues = new Dictionary<string, string>(StringComparer.Ordinal);

        private Type? _functions;

        /// <summary>Values pushed by an event rather than read by a poll.</summary>
        private readonly Dictionary<string, string> _pushed = new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>Guards <see cref="_pushed"/>: the event and the poll are different callers.</summary>
        private readonly object _pushedLock = new object();

        private EventInfo? _dutyEvent;
        private Delegate? _dutyHandler;

        public bool IsAvailable => _functions != null;

        public int BoundProbeCount => _probes.Count;

        public IReadOnlyList<string> MissingProbes => _missing;

        public string Version { get; private set; } = "absent";

        /// <summary>Finds LSPDFR in the current AppDomain and binds to it.</summary>
        public void Bind()
        {
            Assembly? assembly = FindAssembly(LspdfrAssemblyName);
            if (assembly == null)
            {
                return;
            }

            Type? functions = assembly.GetType(FunctionsTypeName, throwOnError: false);
            if (functions == null)
            {
                _missing.Add(FunctionsTypeName);
                return;
            }

            BindTo(functions, assembly.GetName().Version?.ToString() ?? "unknown");
        }

        /// <summary>
        /// Binds to an already-resolved API type. The seam the tests use: everything
        /// below this point is ordinary reflection over a type, so it can be exercised
        /// against a stand-in without Windows, GTA V, RPH or LSPDFR.
        /// </summary>
        public void BindTo(Type functions, string version)
        {
            if (functions == null)
            {
                throw new ArgumentNullException(nameof(functions));
            }

            _functions = functions;
            Version = version ?? "unknown";

            // LSPDFR's own version, preferred over the assembly version when it binds:
            // the assembly version and the version players quote are not always equal.
            MethodInfo? getVersion = FindMethod("GetVersion", Type.EmptyTypes);
            if (getVersion != null)
            {
                try
                {
                    object? reported = getVersion.Invoke(null, null);
                    if (reported != null)
                    {
                        Version = reported.ToString() ?? Version;
                    }
                }
                catch (Exception)
                {
                    // A version that will not read is not worth failing the bind over.
                }
            }
            else
            {
                _missing.Add(FunctionsTypeName + ".GetVersion()");
            }

            // ---- On duty: the event when it binds, a poll when it does not ----
            // On-duty is published as an event, and unlike every other event on this
            // API its delegate is `void(bool)` — no LSPDFR type in the signature, so it
            // can be bound with an ordinary delegate instead of one emitted at runtime.
            // That makes the value exact rather than sampled, and catches a change that
            // happens and reverts inside one poll interval.
            //
            // IsPlayerAvailableForCalls is the fallback, and it answers a slightly
            // different question — can this officer take a call — so which one produced
            // the value is worth knowing. `onDuty.source` says which.
            if (TryBindDutyEvent())
            {
                Push("onDuty.source", "event");
            }
            else
            {
                Push("onDuty.source", "poll");
                TryBind("onDuty", "IsPlayerAvailableForCalls");
            }

            TryBind("callout.running", "IsCalloutRunning");
            TryBind("pullover.active", "IsPlayerPerformingPullover");
            TryBind("pursuit.active", "GetActivePursuit");

            // ---- Derived probes: one parameterless call yields an opaque LHandle,
            // which is then handed straight back to LSPDFR. The handle is never
            // inspected — it cannot be, and it does not need to be.
            //
            // callout.name is the one that makes this worth having. LSPDFR's own
            // documentation calls GetCalloutFriendlyName "the friendly name
            // representation of a callout that is used for LSPDFR Sync", so it is the
            // method meant for exactly this, and GetCurrentCallout() is the
            // parameterless source that makes it reachable without an event.
            TryBindDerived("callout.name", "GetCurrentCallout", "GetCalloutFriendlyName");
            TryBindDerived("callout.state", "GetCurrentCallout", "GetCalloutAcceptanceState");
            TryBindDerived("pursuit.calledIn", "GetActivePursuit", "IsPursuitCalledIn");
            TryBindDerived("pursuit.running", "GetActivePursuit", "IsPursuitStillRunning");
        }

        /// <summary>
        /// Returns the probes whose value changed since the last poll, as
        /// <c>key=value</c> pairs, or an empty string when nothing changed.
        /// </summary>
        public string PollChanges()
        {
            if (_functions == null)
            {
                return string.Empty;
            }

            var changes = new List<string>();

            foreach (KeyValuePair<string, string> entry in Snapshot())
            {
                if (_lastValues.TryGetValue(entry.Key, out string? was) && was == entry.Value)
                {
                    continue;
                }

                _lastValues[entry.Key] = entry.Value;
                changes.Add(entry.Key + "=" + entry.Value);
            }

            foreach (Probe probe in _probes)
            {
                string value = Read(probe);
                if (_lastValues.TryGetValue(probe.Key, out string? previous) && previous == value)
                {
                    continue;
                }

                _lastValues[probe.Key] = value;
                changes.Add(probe.Key + "=" + value);
            }

            return changes.Count == 0 ? string.Empty : string.Join(";", changes.ToArray());
        }

        /// <summary>Every current value, for a client that has just connected.</summary>
        public string Describe()
        {
            if (_functions == null)
            {
                return "available=false";
            }

            var builder = new StringBuilder();
            builder.Append("available=true;version=").Append(Version);

            foreach (KeyValuePair<string, string> entry in Snapshot())
            {
                _lastValues[entry.Key] = entry.Value;
                builder.Append(';').Append(entry.Key).Append('=').Append(entry.Value);
            }

            foreach (Probe probe in _probes)
            {
                string value = Read(probe);
                _lastValues[probe.Key] = value;
                builder.Append(';').Append(probe.Key).Append('=').Append(value);
            }

            return builder.ToString();
        }

        private string Read(Probe probe)
        {
            try
            {
                object? source = probe.Source.Invoke(null, null);

                if (probe.Derived == null)
                {
                    return Summarise(source);
                }

                // No handle means nothing to ask about — not an error, just "none".
                // Calling through a null handle is how a poll loop turns a quiet
                // moment into an exception every tick.
                if (source == null)
                {
                    return "none";
                }

                return Summarise(probe.Derived.Invoke(null, new[] { source }));
            }
            catch (TargetInvocationException exception)
            {
                // LSPDFR throwing from a getter is its business; the probe reports the
                // failure rather than taking the poll loop down.
                return "error:" + (exception.InnerException?.GetType().Name ?? "unknown");
            }
            catch (Exception exception)
            {
                return "error:" + exception.GetType().Name;
            }
        }

        /// <summary>
        /// Reduces a probe result to something that fits in a state string. An LSPDFR
        /// handle is opaque, so only its presence is reported — that is all that can be
        /// said about it without referencing LSPDFR's types.
        /// </summary>
        private static string Summarise(object? value)
        {
            if (value == null)
            {
                return "none";
            }

            if (value is bool flag)
            {
                return flag ? "true" : "false";
            }

            if (value is string text)
            {
                return text.Length == 0 ? "none" : Sanitise(text);
            }

            if (value.GetType().IsEnum)
            {
                return value.ToString() ?? "none";
            }

            // A handle. Its type name is stable and says "there is one"; its contents
            // are LSPDFR's business.
            return "handle";
        }

        /// <summary>
        /// Makes a free-text value safe to put in the state string.
        /// <para>
        /// Until <c>callout.name</c> existed every probe returned a bool, an enum or a
        /// handle, so the <c>key=value;key=value</c> format could not be broken by a
        /// value. A callout's friendly name is different: it is free text, and it
        /// comes from whichever third-party callout plugin the player installed. One
        /// semicolon in it would split a field in two on the far side; one equals sign
        /// would move the key boundary.
        /// </para>
        /// <para>
        /// Separators become spaces rather than being escaped, because an escape
        /// scheme needs the reader to agree with the writer and this format has
        /// readers that predate it. Control characters go for the same reason. The
        /// length cap is there because the name crosses the wire on every change and a
        /// plugin author is free to be as verbose as they like.
        /// </para>
        /// </summary>
        internal static string Sanitise(string text)
        {
            const int MaxLength = 64;

            var builder = new StringBuilder(Math.Min(text.Length, MaxLength));

            foreach (char c in text)
            {
                if (builder.Length == MaxLength)
                {
                    break;
                }

                builder.Append(c == ';' || c == '=' || char.IsControl(c) ? ' ' : c);
            }

            string cleaned = builder.ToString().Trim();
            return cleaned.Length == 0 ? "none" : cleaned;
        }

        /// <summary>
        /// Subscribes to <c>OnOnDutyStateChanged</c> when its delegate is the shape we
        /// expect, and reports false otherwise.
        /// <para>
        /// The shape is checked rather than assumed: a delegate is bound by exact
        /// signature, so attaching to something else either throws here or — worse —
        /// attaches and never fires. Verified against the assembly's metadata
        /// (<c>OnDutyStateChangedEventHandler.Invoke(Boolean)</c>) and against how a
        /// real plugin subscribes.
        /// </para>
        /// </summary>
        private bool TryBindDutyEvent()
        {
            const string EventName = "OnOnDutyStateChanged";

            EventInfo? info = _functions!.GetEvent(EventName, BindingFlags.Public | BindingFlags.Static);
            MethodInfo? invoke = info?.EventHandlerType?.GetMethod("Invoke");

            if (info == null || invoke == null)
            {
                _missing.Add(FunctionsTypeName + "." + EventName);
                return false;
            }

            ParameterInfo[] parameters = invoke.GetParameters();
            if (invoke.ReturnType != typeof(void) || parameters.Length != 1 || parameters[0].ParameterType != typeof(bool))
            {
                // A different shape than this build was written against. Falling back to
                // the poll is the honest answer; guessing at a signature is how a
                // subscription succeeds and then never fires.
                _missing.Add(FunctionsTypeName + "." + EventName + " (unexpected delegate shape)");
                return false;
            }

            try
            {
                MethodInfo handler = typeof(LspdfrObserver).GetMethod(
                    nameof(OnDutyStateChanged), BindingFlags.NonPublic | BindingFlags.Instance)!;

                _dutyHandler = Delegate.CreateDelegate(info.EventHandlerType!, this, handler);
                info.AddEventHandler(null, _dutyHandler);
                _dutyEvent = info;
                return true;
            }
            catch (Exception)
            {
                _dutyHandler = null;
                _missing.Add(FunctionsTypeName + "." + EventName + " (subscription failed)");
                return false;
            }
        }

        private void OnDutyStateChanged(bool onDuty)
        {
            Push("onDuty", onDuty ? "true" : "false");
        }

        /// <summary>
        /// A stable copy of the event-pushed values. Copied under the lock so a poll
        /// cannot read a dictionary an event is writing to.
        /// </summary>
        private List<KeyValuePair<string, string>> Snapshot()
        {
            lock (_pushedLock)
            {
                return new List<KeyValuePair<string, string>>(_pushed);
            }
        }

        private void Push(string key, string value)
        {
            lock (_pushedLock)
            {
                _pushed[key] = value;
            }
        }

        /// <summary>
        /// Detaches from LSPDFR. Not optional: an event handler left attached keeps
        /// this object alive inside LSPDFR and goes on being called after the bridge
        /// has stopped, which is exactly the shape of a crash on plugin reload — and
        /// RPH reloads plugins.
        /// </summary>
        public void Dispose()
        {
            if (_dutyEvent != null && _dutyHandler != null)
            {
                try
                {
                    _dutyEvent.RemoveEventHandler(null, _dutyHandler);
                }
                catch (Exception)
                {
                    // Nothing useful to do if LSPDFR has already gone.
                }
            }

            _dutyEvent = null;
            _dutyHandler = null;
        }

        private void TryBind(string key, string methodName)
        {
            MethodInfo? method = FindMethod(methodName, Type.EmptyTypes);
            if (method == null)
            {
                _missing.Add(FunctionsTypeName + "." + methodName + "()");
                return;
            }

            _probes.Add(new Probe(key, method, null));
        }

        /// <summary>
        /// Binds a probe that needs a handle: <paramref name="sourceName"/> supplies
        /// one and <paramref name="methodName"/> consumes it. Bound by arity rather
        /// than by parameter type, because the handle type cannot be named here.
        /// </summary>
        private void TryBindDerived(string key, string sourceName, string methodName)
        {
            MethodInfo? source = FindMethod(sourceName, Type.EmptyTypes);
            MethodInfo? derived = FindSingleParameterMethod(methodName);

            if (source == null || derived == null)
            {
                _missing.Add(FunctionsTypeName + "." + methodName + "(handle)");
                return;
            }

            _probes.Add(new Probe(key, source, derived));
        }

        private MethodInfo? FindMethod(string name, Type[] parameters) =>
            _functions!.GetMethod(name, BindingFlags.Public | BindingFlags.Static, null, parameters, null);

        /// <summary>
        /// The one-parameter overload of a name. <c>GetMethod</c> by name alone throws
        /// on an overloaded name, and several of these are overloaded, so the arity is
        /// filtered here instead.
        /// </summary>
        private MethodInfo? FindSingleParameterMethod(string name)
        {
            MethodInfo? match = null;

            foreach (MethodInfo candidate in _functions!.GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                if (candidate.Name != name || candidate.GetParameters().Length != 1)
                {
                    continue;
                }

                if (match != null)
                {
                    // Two single-parameter overloads of the same name: which one is
                    // meant is a guess, and a guess here calls into LSPDFR with the
                    // wrong argument type. Report it unbound instead.
                    return null;
                }

                match = candidate;
            }

            return match;
        }

        private static Assembly? FindAssembly(string simpleName)
        {
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (string.Equals(assembly.GetName().Name, simpleName, StringComparison.OrdinalIgnoreCase))
                {
                    return assembly;
                }
            }

            return null;
        }

        private readonly struct Probe
        {
            public Probe(string key, MethodInfo source, MethodInfo? derived)
            {
                Key = key;
                Source = source;
                Derived = derived;
            }

            public string Key { get; }

            /// <summary>The parameterless call. Its result is the value, or the handle.</summary>
            public MethodInfo Source { get; }

            /// <summary>Null for a direct probe; otherwise the call the handle feeds.</summary>
            public MethodInfo? Derived { get; }
        }
    }
}
