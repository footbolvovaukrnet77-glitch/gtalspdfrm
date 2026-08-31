using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;

namespace Gtamp.RphBridge
{
    /// <summary>
    /// Watches LSPDFR from inside the RPH host and reports what it sees.
    /// <para>
    /// <b>Why polling rather than events.</b> LSPDFR publishes events whose delegate
    /// signatures use its own types — <c>LHandle</c>, its callout classes — which
    /// cannot be referenced here: LSPDFR ships no redistributable assembly, and its
    /// licence does not permit shipping one. Subscribing anyway would mean
    /// synthesising delegates of arbitrary signatures at runtime, and that binds to
    /// the exact shape of each event: one signature change in an LSPDFR update and
    /// the subscription fails silently, mid-callout.
    /// </para>
    /// <para>
    /// Polling binds only to <em>names</em> of public, parameterless functions. A
    /// rename still breaks a probe, but it breaks one probe, visibly, and the rest
    /// keep working. Every probe that fails to bind is recorded and reported rather
    /// than being quietly absent.
    /// </para>
    /// <para>
    /// The cost is real and worth stating: transitions shorter than the poll interval
    /// are missed, and anything LSPDFR only exposes through an event argument cannot
    /// be seen at all.
    /// </para>
    /// </summary>
    public sealed class LspdfrObserver
    {
        private const string LspdfrAssemblyName = "LSPD First Response";
        private const string FunctionsTypeName = "LSPD_First_Response.Mod.API.Functions";

        private readonly List<Probe> _probes = new List<Probe>();
        private readonly List<string> _missing = new List<string>();
        private readonly Dictionary<string, string> _lastValues = new Dictionary<string, string>(StringComparer.Ordinal);

        private Type? _functions;

        public bool IsAvailable => _functions != null;

        public int BoundProbeCount => _probes.Count;

        public IReadOnlyList<string> MissingProbes => _missing;

        public string Version { get; private set; } = "absent";

        /// <summary>Binds to whatever of the probe set this LSPDFR build actually has.</summary>
        public void Bind()
        {
            Assembly? assembly = FindAssembly(LspdfrAssemblyName);
            if (assembly == null)
            {
                return;
            }

            Version = assembly.GetName().Version?.ToString() ?? "unknown";
            _functions = assembly.GetType(FunctionsTypeName, throwOnError: false);

            if (_functions == null)
            {
                _missing.Add(FunctionsTypeName);
                return;
            }

            // Each probe is a public, parameterless function whose result can be
            // reduced to a short string. Names only — no signatures beyond "takes
            // nothing", which is the least that can break.
            TryBind("onDuty", "IsPlayerAvailable");
            TryBind("callout.running", "IsCalloutRunning");
            TryBind("callout.current", "GetCurrentCallout");
            TryBind("pullover.current", "GetCurrentPullover");
            TryBind("pursuit.active", "GetActivePursuit");
            TryBind("player.state", "GetPlayerState");
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
                changes.Add($"{probe.Key}={value}");
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
                object? result = probe.Method.Invoke(null, null);
                return Summarise(result);
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
        /// handle is opaque, so only its presence and type name are reported — that is
        /// all that can be said about it without referencing LSPDFR's types.
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

            return value.GetType().Name;
        }

        private void TryBind(string key, string methodName)
        {
            MethodInfo? method = _functions!.GetMethod(
                methodName, BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);

            if (method == null)
            {
                _missing.Add($"{FunctionsTypeName}.{methodName}()");
                return;
            }

            _probes.Add(new Probe(key, method));
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
            public Probe(string key, MethodInfo method)
            {
                Key = key;
                Method = method;
            }

            public string Key { get; }

            public MethodInfo Method { get; }
        }
    }
}
