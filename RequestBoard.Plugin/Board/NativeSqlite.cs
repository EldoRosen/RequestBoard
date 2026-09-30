using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace RequestBoard.Board
{
    public static class NativeSqlite
    {
        private const string FileName = "SQLite.Interop.dll";
        private static readonly object Gate = new object();
        private static bool _loaded;

        public static void EnsureLoaded(string storagePath)
        {
            lock (Gate)
            {
                if (_loaded) return;
                byte[] bytes;
                using (var resource = typeof(NativeSqlite).Assembly.GetManifestResourceStream(FileName))
                {
                    if (resource == null) throw new InvalidOperationException($"{FileName} is missing from the plugin.");
                    using (var buffer = new MemoryStream())
                    {
                        resource.CopyTo(buffer);
                        bytes = buffer.ToArray();
                    }
                }

                var version = typeof(System.Data.SQLite.SQLiteConnection).Assembly.GetName().Version;
                var dir = Path.Combine(storagePath, "RequestBoard", "native", version.ToString());
                var path = Path.Combine(dir, FileName);
                if (!File.Exists(path) || !File.ReadAllBytes(path).SequenceEqual(bytes))
                {
                    Directory.CreateDirectory(dir);
                    File.WriteAllBytes(path, bytes);
                }

                if (LoadLibrary(path) == IntPtr.Zero)
                    throw new Win32Exception(Marshal.GetLastWin32Error(), $"Could not load {path}");
                _loaded = true;
            }
        }

        [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibrary(string path);
    }
}
