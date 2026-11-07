using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using Godot;

namespace GothicGodot.Spike
{
    /// <summary>
    /// Resolves ZenKit's and dmusic's native libraries. Exported builds find them next to the app (Android: the APK's
    /// lib/arm64-v8a, desktop: next to the executable); the editor loads them from res://lib/linux-x64.
    /// </summary>
    public static class NativeLibraries
    {
        private static bool _isRegistered;

        public static void Register()
        {
            if (_isRegistered)
                return;
            _isRegistered = true;

            NativeLibrary.SetDllImportResolver(typeof(ZenKit.Vfs).Assembly, Resolve);
            NativeLibrary.SetDllImportResolver(typeof(DirectMusic.Performance).Assembly, Resolve);
        }

        private static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? searchPath)
        {
            var fileName = name.StartsWith("lib") ? $"{name}.so" : $"lib{name}.so";

            if (OS.HasFeature("editor"))
            {
                var path = ProjectSettings.GlobalizePath($"res://lib/linux-x64/{fileName}");
                if (File.Exists(path))
                    return NativeLibrary.Load(path);
            }

            // Android's linker finds libraries of the APK by their file name.
            return NativeLibrary.TryLoad(fileName, out var handle) ? handle : IntPtr.Zero;
        }
    }
}
