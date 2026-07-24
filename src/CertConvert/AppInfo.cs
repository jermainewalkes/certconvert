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

    /// <summary>
    /// Printed by the store CLI when a file command is blocked by the sandbox.
    /// Deliberately does NOT send a paying customer to a download page: the store
    /// editions are the graphical app, and the command line exists only in a
    /// build compiled from source.
    /// </summary>
    public const string StoreCliPointer =
        "This edition of CertConvert is the graphical app. It runs in a sandbox that " +
        "cannot open files named on the command line, so no command-line tool is " +
        "included. Open CertConvert normally to inspect, convert, chain and generate.";
}
