using System.Reflection;

namespace Gtamp.Server.Core
{
    public static class BuildInfo
    {
        public static string Version =>
            Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

        public static string Describe() => $"GTAMP Server {Version}";
    }
}
