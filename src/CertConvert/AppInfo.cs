namespace CertConvert;

/// <summary>
/// Single source of truth for the build variant. STORE_BUILD is defined by the
/// csproj when published with -p:StoreBuild=true for the App Store / Microsoft
/// Store, where self-updating and donation links are not permitted and the
/// process is sandboxed away from arbitrary command-line file paths.
/// </summary>
public static class AppInfo
{
#if STORE_BUILD
    public const bool IsStoreBuild = true;
#else
    public const bool IsStoreBuild = false;
#endif

    /// <summary>Printed by the store CLI when a file command is blocked by the sandbox.</summary>
    public const string StoreCliPointer =
        "The command line requires the direct download: " +
        "https://github.com/jermainewalkes/certconvert/releases";
}
