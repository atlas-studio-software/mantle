namespace Mantle.Core.Platform;

/// <summary>
/// Words the native build says where Electron says "run npm run build:sidecar" (deviation 6, revisited).
/// Native Mantle never builds the sidecar: it runs the Mantle.Sidecar.exe that sits next to
/// MantleNative.exe (the installed layout) or, in a checkout, sidecar\bin\Mantle.Sidecar.exe. So the fix
/// it names is getting a current exe into that place, in the user's terms.
/// </summary>
public static class PlatformWords
{
    public const string Rebuild =
        @"the Mantle.Sidecar.exe next to MantleNative.exe is older than this Mantle; reinstall native Mantle, or in a checkout rebuild the sidecar (sidecar\bin\Mantle.Sidecar.exe) and start again";

    public const string KeepsStopping =
        @"if it keeps stopping, reinstall native Mantle (or rebuild sidecar\bin\Mantle.Sidecar.exe in a checkout)";
}
