using System.Linq;
using System.Reflection;

namespace Multiplayer.Common
{
    public static class MpVersion
    {
        public const string SimpleVersion = "0.11.5";
        // Raised above upstream's 57: this integration build adds #927's ping/marker packets and must not
        // connect to official builds.
        public const int Protocol = 58;

        public static readonly string? GitHash = Assembly.GetExecutingAssembly()
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attr => attr.Key == "GitHash")?.Value;

        public static readonly string Version = SimpleVersion + (GitHash != null ? $"+{GitHash}" : "");

        public static readonly string? GitDescription = Assembly.GetExecutingAssembly()
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attr => attr.Key == "GitDescription")?.Value;

        public const string ApiAssemblyName = "0MultiplayerAPI";

#if DEBUG
        public const bool IsDebug = true;
#else
        public const bool IsDebug = false;
#endif
    }
}
