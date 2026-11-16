// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace ViVe.Core
{
    /// <summary>Helpers for NTSTATUS codes returned by the Rtl*FeatureConfiguration APIs.</summary>
    public static class NtStatus
    {
        public const int Success = 0;
        public const int Unsuccessful = unchecked((int)0xC0000001);
        public const int AccessDenied = unchecked((int)0xC0000022);
        public const int ObjectNameNotFound = unchecked((int)0xC0000034);
        public const int InvalidParameter = unchecked((int)0xC000000D);
        public const int NotSupported = unchecked((int)0xC00000BB);

        private const int ERROR_MR_MID_NOT_FOUND = 0x13D;

        public static bool IsSuccess(int status) => status == Success;

        public static string ToHex(int status) => "0x" + unchecked((uint)status).ToString("X8");

        /// <summary>
        /// Human-readable description, matching ViVeTool's original GetHumanErrorDescription:
        /// NTSTATUS is translated to a Win32 error when possible, and <paramref name="noTranslate"/>
        /// treats the value as an HRESULT/Win32 code directly (used for registry exceptions).
        /// </summary>
        public static string Describe(int status, bool noTranslate = false)
        {
            if (!IsWindows())
                return $"Status {ToHex(status)}";
            var code = 0;
            if (!noTranslate)
            {
                try { code = RtlNtStatusToDosError(status); }
                catch (DllNotFoundException) { code = ERROR_MR_MID_NOT_FOUND; }
                catch (EntryPointNotFoundException) { code = ERROR_MR_MID_NOT_FOUND; }
            }
            if (noTranslate || code == ERROR_MR_MID_NOT_FOUND)
                code = status;
            return new Win32Exception(code).Message;
        }

        /// <summary>Description that always includes the raw code, for logs and history.</summary>
        public static string DescribeWithCode(int status, bool noTranslate = false) =>
            $"{Describe(status, noTranslate)} ({ToHex(status)})";

        internal static bool IsWindows() =>
#if NETFRAMEWORK
            Environment.OSVersion.Platform == PlatformID.Win32NT;
#else
            OperatingSystem.IsWindows();
#endif

        [DllImport("ntdll.dll")]
        private static extern int RtlNtStatusToDosError(int ntStatus);
    }
}
