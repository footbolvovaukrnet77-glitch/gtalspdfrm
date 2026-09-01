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
    /// <b>The names are no longer guesses.</b> Every probe here was checked against
    /// <c>LSPD_First_Response.XML</c>, the API documentation LSPDFR ships beside its
    /// assembly. That check deleted four of the six original probes —
    /// <c>IsPlayerAvailable</c>, <c>GetCurrentCallout</c>, <c>GetCurrentPullover</c>
    /// and <c>GetPlayerState</c> do not exist in the documented API at all, so they
    /// could only ever have landed in <see cref="MissingProbes"/>.
    /// </para>
    /// <para>
    /// <b>What the documentation does not settle.</b> It lists only members carrying
    /// doc comments, so absence from it is not proof of absence from the assembly —
    /// which is why an unverified name is still allowed to bind if this build happens
    /// to have it, and why binding failure stays an ordinary, reported outcome rather
    /// than an error.
    /// </para>
    /// <para>
    /// This type lives in the shared assembly rather than in the RPH bridge because it
    /// touches no <c>Rage</c> type — only <c>System.Reflection</c>. That is what lets
    /// it be tested against a stand-in <c>Functions</c> type with no game, no RPH and
    /// no LSPDFR present.
    /// </para>
    /// </summary>
    public sealed class LspdfrObserver
    {
        /// <summary>The simple assembly name LSPDFR loads under.</summary>
        public const string LspdfrAssemblyName = "LSPD First Response";

        /// <summary>The static API surface every probe binds against.</summary>
        public const string FunctionsTypeName = "LSPD_First_Response.Mod.API.Functions";

        private readonly List<Probe> _probes = new List<Probe>();
        private readonly List<string> _missing = new List<string>();
        private readonly Dictionary<string, string> _lastValues = new Dictionary<string, string>(StringComparer.Ordinal);

        private Type? _functions;

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

            // ---- Parameterless probes, all three verified against the shipped XML ----
            TryBind("callout.running", "IsCalloutRunning");
            TryBind("pullover.active", "IsPlayerPerformingPullover");
            TryBind("pursuit.active", "GetActivePursuit");

            // ---- Derived probes: one parameterless call yields an opaque LHandle,
            // which is then handed straight back to LSPDFR. The handle is never
            // inspected — it cannot be, and it does not need to be. This is what turns
            // "a pursuit is happening" into something another player can act on.
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
                return text.Length == 0 ? "none" : text;
            }

            if (value.GetType().IsEnum)
            {
                return value.ToString() ?? "none";
            }

            // A handle. Its type name is stable and says "there is one"; its contents
            // are LSPDFR's business.
            return "handle";
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
