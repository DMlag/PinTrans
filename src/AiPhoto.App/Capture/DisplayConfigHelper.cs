using System.Runtime.InteropServices;
using AiPhoto.App.Native;
using static AiPhoto.App.Native.NativeMethods;

namespace AiPhoto.App.Capture;

/// <summary>Reads the per-monitor "SDR content brightness" that Windows applies on HDR displays.</summary>
internal static class DisplayConfigHelper
{
    /// <summary>Maps GDI device names (e.g. <c>\\.\DISPLAY1</c>) to their SDR white level in nits.</summary>
    public static Dictionary<string, float> GetSdrWhiteLevels()
    {
        var result = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out var pathCount, out var modeCount) != 0) return result;
            var paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
            var modes = new DISPLAYCONFIG_MODE_INFO[modeCount];
            if (QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero) != 0) return result;

            for (var i = 0; i < pathCount; i++)
            {
                var path = paths[i];
                var source = new DISPLAYCONFIG_SOURCE_DEVICE_NAME
                {
                    header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
                    {
                        type = DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME,
                        size = Marshal.SizeOf<DISPLAYCONFIG_SOURCE_DEVICE_NAME>(),
                        adapterId = path.sourceInfo.adapterId,
                        id = path.sourceInfo.id,
                    },
                };
                if (DisplayConfigGetDeviceInfo(ref source) != 0) continue;

                var white = new DISPLAYCONFIG_SDR_WHITE_LEVEL
                {
                    header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
                    {
                        type = DISPLAYCONFIG_DEVICE_INFO_GET_SDR_WHITE_LEVEL,
                        size = Marshal.SizeOf<DISPLAYCONFIG_SDR_WHITE_LEVEL>(),
                        adapterId = path.targetInfo.adapterId,
                        id = path.targetInfo.id,
                    },
                };
                if (DisplayConfigGetDeviceInfo(ref white) != 0 || white.SDRWhiteLevel == 0) continue;

                result[source.viewGdiDeviceName] = white.SDRWhiteLevel / 1000f * 80f;
            }
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            // Very old Windows builds: fall back to defaults.
        }
        return result;
    }
}
