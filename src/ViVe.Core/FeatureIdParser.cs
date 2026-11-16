// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Globalization;

namespace ViVe.Core
{
    /// <summary>Result of parsing user-supplied feature IDs.</summary>
    public sealed record FeatureIdParseResult(IReadOnlyList<uint> Ids, IReadOnlyList<string> Errors)
    {
        public bool IsValid => Errors.Count == 0 && Ids.Count > 0;
    }

    /// <summary>
    /// Strict validation of feature IDs typed into the GUI. Accepts decimal or 0x-prefixed hexadecimal
    /// 32-bit IDs separated by commas, semicolons or whitespace. Feature ID 0 is rejected.
    /// (The CLI keeps its original, more permissive parser for backwards compatibility.)
    /// </summary>
    public static class FeatureIdParser
    {
        public const int DefaultMaxIds = 64;
        private const int MaxInputLength = 4096;
        private static readonly char[] Separators = { ',', ';', ' ', '\t', '\r', '\n' };

        public static bool TryParse(string? text, out uint featureId)
        {
            featureId = 0;
            if (string.IsNullOrWhiteSpace(text))
                return false;
            var s = text!.Trim();
            bool ok;
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                var hex = s.Substring(2);
                ok = hex.Length is > 0 and <= 8 && IsHex(hex) &&
                     uint.TryParse(hex, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out featureId);
            }
            else
            {
                ok = IsDigits(s) && uint.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out featureId);
            }
            return ok && featureId != 0;
        }

        public static FeatureIdParseResult ParseList(string? text, int maxIds = DefaultMaxIds)
        {
            var ids = new List<uint>();
            var errors = new List<string>();
            if (string.IsNullOrWhiteSpace(text))
            {
                errors.Add("Enter at least one feature ID.");
                return new FeatureIdParseResult(ids, errors);
            }
            if (text!.Length > MaxInputLength)
            {
                errors.Add($"Input is too long (maximum {MaxInputLength} characters).");
                return new FeatureIdParseResult(ids, errors);
            }

            var seen = new HashSet<uint>();
            foreach (var token in text.Split(Separators, StringSplitOptions.RemoveEmptyEntries))
            {
                if (!TryParse(token, out var id))
                {
                    errors.Add($"'{Truncate(token)}' is not a valid feature ID (use a non-zero decimal number or 0x-prefixed hexadecimal, up to 4294967295).");
                    continue;
                }
                if (seen.Add(id))
                    ids.Add(id);
            }
            if (ids.Count > maxIds)
                errors.Add($"Too many feature IDs ({ids.Count}); at most {maxIds} can be processed at once.");
            if (ids.Count == 0 && errors.Count == 0)
                errors.Add("Enter at least one feature ID.");
            return new FeatureIdParseResult(ids, errors);
        }

        private static bool IsDigits(string s)
        {
            if (s.Length == 0 || s.Length > 10) return false;
            foreach (var c in s) if (c < '0' || c > '9') return false;
            return true;
        }

        private static bool IsHex(string s)
        {
            foreach (var c in s)
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'))) return false;
            return true;
        }

        private static string Truncate(string s) => s.Length <= 32 ? s : s.Substring(0, 32) + "…";
    }
}
