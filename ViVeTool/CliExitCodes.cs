/*
    ViVeTool - Windows feature configuration tool
    ViVeTool Studio addition. Licensed under the GNU General Public License v3.0 or later.
 */

namespace Albacore.ViVeTool
{
    /// <summary>
    /// Process exit codes. Upstream ViVeTool 0.3.4 always exited with 0; ViVeTool Studio's build
    /// reports failures so scripts can detect them. Successful runs still return 0.
    /// </summary>
    internal static class CliExitCodes
    {
        internal const int Success = 0;
        /// <summary>A Windows API call failed or reported an error.</summary>
        internal const int OperationFailed = 1;
        /// <summary>Unknown command, invalid argument value, or missing required argument.</summary>
        internal const int InvalidArguments = 2;
        /// <summary>Windows build older than 18963.</summary>
        internal const int UnsupportedPlatform = 3;
    }
}
