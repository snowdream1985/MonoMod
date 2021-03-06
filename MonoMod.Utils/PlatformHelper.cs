﻿using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace MonoMod.Utils {
    [MonoMod__OldName__("MonoMod.Helpers.PlatformHelper")]
    public static class PlatformHelper {

        static PlatformHelper() {
            Current = Platform.Unknown;

#if NETSTANDARD
            // RuntimeInformation.IsOSPlatform is lying: https://github.com/dotnet/corefx/issues/3032
            // Determine the platform based on the path.
            string windir = Environment.GetEnvironmentVariable("windir");
            if (!string.IsNullOrEmpty(windir) && windir.Contains(@"\") && Directory.Exists(windir)) {
                Current = Platform.Windows;

            } else if (File.Exists("/proc/sys/kernel/ostype")) {
                string osType = File.ReadAllText("/proc/sys/kernel/ostype");
                if (osType.StartsWith("Linux", StringComparison.OrdinalIgnoreCase)) {
                    Current = Platform.Linux;
                }

            } else if (File.Exists("/System/Library/CoreServices/SystemVersion.plist")) {
                Current = Platform.MacOS;
            }

#else
            // For old Mono, get from a private property to accurately get the platform.
            // static extern PlatformID Platform
            PropertyInfo property_platform = typeof(Environment).GetTypeInfo().GetProperty("Platform", BindingFlags.NonPublic | BindingFlags.Static);
            string platID;
            if (property_platform != null) {
                platID = property_platform.GetValue(null, new object[0]).ToString();
            } else {
                // For .NET and newer Mono, use the usual value.
                platID = Environment.OSVersion.Platform.ToString();
            }
            platID = platID.ToLowerInvariant();

            if (platID.Contains("win")) {
                Current = Platform.Windows;
            } else if (platID.Contains("mac") || platID.Contains("osx")) {
                Current = Platform.MacOS;
            } else if (platID.Contains("lin") || platID.Contains("unix")) {
                Current = Platform.Linux;
            }
#endif

            if (Directory.Exists("/data") && File.Exists("/system/build.prop")) {
                Current = Platform.Android;
            } else if (Directory.Exists("/Applications") && Directory.Exists("/System")) {
                Current = Platform.iOS;
            }

            Current |= (IntPtr.Size == 4 ? Platform.Bits32 : Platform.Bits64);

#if NETSTANDARD
            // Detect ARM based on RuntimeInformation.
            if (RuntimeInformation.ProcessArchitecture.HasFlag(Architecture.Arm) ||
                RuntimeInformation.OSArchitecture.HasFlag(Architecture.Arm))
                Current |= Platform.ARM;
#else
            // Detect ARM based on PE info.
            typeof(object).GetTypeInfo().Module.GetPEKind(out PortableExecutableKinds peKind, out ImageFileMachine machine);
            if (machine == (ImageFileMachine) 0x01C4 /* ARM, .NET 4.5 */)
                Current |= Platform.ARM;
#endif



        }

        public static Platform Current { get; private set; }

        public static bool Is(Platform platform)
            => (Current & platform) == platform;

    }
}
