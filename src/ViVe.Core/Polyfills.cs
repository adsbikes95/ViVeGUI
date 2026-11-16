// SPDX-License-Identifier: GPL-3.0-or-later
// Polyfills that let C# records/init accessors compile for net481.
#if NETFRAMEWORK
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit { }
}
#endif
