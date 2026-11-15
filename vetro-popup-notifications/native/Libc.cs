using System;
using System.Runtime.InteropServices;

namespace PopupNotify.Native
{
    /// <summary>libc, for setting the environment before elm_init and for the command FIFO.</summary>
    internal static class Libc
    {
        private const string Lib = "libc.so.6";

        public const int O_RDONLY = 0;
        public const int O_WRONLY = 1;
        public const int O_RDWR = 2;
        public const int O_CREAT = 0x40;
        public const int O_NONBLOCK = 0x800;

        [DllImport(Lib, EntryPoint = "setenv")] public static extern int setenv(string name, string value, int overwrite);
        [DllImport(Lib)] public static extern uint getuid();
        [DllImport(Lib)] public static extern int getpid();
        [DllImport(Lib)] public static extern int kill(int pid, int sig);
        [DllImport(Lib, EntryPoint = "open", SetLastError = true)] public static extern int open(string path, int flags);
        [DllImport(Lib, EntryPoint = "open", SetLastError = true)] public static extern int openMode(string path, int flags, uint mode);
        [DllImport(Lib, SetLastError = true)] public static extern IntPtr read(int fd, byte[] buf, UIntPtr count);
        [DllImport(Lib)] public static extern IntPtr write(int fd, byte[] buf, UIntPtr count);
        [DllImport(Lib)] public static extern int close(int fd);
        [DllImport(Lib, SetLastError = true)] public static extern int ioctl(int fd, UIntPtr request, UIntPtr arg);
        [DllImport(Lib)] public static extern int mkfifo(string path, uint mode);
        [DllImport(Lib)] public static extern int chmod(string path, uint mode);
        [DllImport(Lib)] public static extern int unlink(string path);
        [DllImport(Lib)] public static extern int system(string command);
    }
}
