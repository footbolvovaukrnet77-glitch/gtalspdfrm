using System;
using System.Collections.Generic;
using System.Reflection;

namespace Gtamp.Client.Mods
{
    /// <summary>
    /// Late-bound access to a mod that this build has no compile-time reference to.
    /// <para>
    /// Every optional integration is written against this rather than against the
    /// mod's own assembly. That is the difference between "RAGE Plugin Hook is
    /// optional" as a claim and as a fact: with no reference there is nothing for
    /// the loader to fail to resolve when RPH is absent.
    /// </para>
    /// <para>
    /// The cost is real and worth stating: reflection binds by name, so a rename in
    /// the target mod turns into a runtime miss instead of a compile error. Every
    /// probe here therefore reports what it could not find rather than throwing, and
    /// the adapters surface that through /diagnostics.
    /// </para>
    /// </summary>
    public sealed class ReflectionProbe
    {
        private readonly List<string> _misses = new List<string>();

        public ReflectionProbe(Assembly assembly)
        {
            Assembly = assembly ?? throw new ArgumentNullException(nameof(assembly));
        }

        public Assembly Assembly { get; }

        /// <summary>Members the probe looked for and did not find. Shown in diagnostics.</summary>
        public IReadOnlyList<string> Misses => _misses;

        public string Version => Assembly.GetName().Version?.ToString() ?? "unknown";

        /// <summary>Finds an already-loaded assembly by simple name, or null.</summary>
        public static Assembly? FindLoadedAssembly(string simpleName)
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

        public Type? FindType(string fullName)
        {
            Type? type = Assembly.GetType(fullName, throwOnError: false, ignoreCase: false);
            if (type == null)
            {
                _misses.Add("type " + fullName);
            }

            return type;
        }

        public object? GetStaticProperty(string typeName, string propertyName)
        {
            Type? type = FindType(typeName);
            if (type == null)
            {
                return null;
            }

            PropertyInfo? property = type.GetProperty(propertyName, BindingFlags.Public | BindingFlags.Static);
            if (property == null)
            {
                _misses.Add($"{typeName}.{propertyName}");
                return null;
            }

            try
            {
                return property.GetValue(null);
            }
            catch (Exception exception)
            {
                _misses.Add($"{typeName}.{propertyName} threw {exception.GetType().Name}");
                return null;
            }
        }

        public object? InvokeStatic(string typeName, string methodName, params object[] arguments)
        {
            Type? type = FindType(typeName);
            if (type == null)
            {
                return null;
            }

            var argumentTypes = new Type[arguments.Length];
            for (int i = 0; i < arguments.Length; i++)
            {
                argumentTypes[i] = arguments[i]?.GetType() ?? typeof(object);
            }

            MethodInfo? method = type.GetMethod(
                methodName, BindingFlags.Public | BindingFlags.Static, null, argumentTypes, null);

            if (method == null)
            {
                _misses.Add($"{typeName}.{methodName}({argumentTypes.Length} args)");
                return null;
            }

            try
            {
                return method.Invoke(null, arguments);
            }
            catch (TargetInvocationException exception)
            {
                _misses.Add($"{typeName}.{methodName} threw {exception.InnerException?.GetType().Name ?? "?"}");
                return null;
            }
        }
    }
}
