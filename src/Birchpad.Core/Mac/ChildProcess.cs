using System.Collections;
using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;

namespace Birchpad.Core.Mac;

/// <summary>
/// A child process started as the leader of its own session, so a signal that it or anything it starts sends to its
/// process group never reaches the plugin service.
/// </summary>
/// <remarks>
/// <see cref="System.Diagnostics.Process"/> puts children in the caller's process group and ignores
/// <c>CreateNewProcessGroup</c> on macOS. A git run by <c>codex app-server</c> sent SIGHUP to that group, which is the
/// plugin service's, and the service died with it.
/// </remarks>
public sealed class ChildProcess : IDisposable
{
    private const short SpawnSetSigDefault = 0x0004;
    private const short SpawnSetSigMask = 0x0008;
    private const short SpawnSetSid = 0x0400;
    private const short SpawnCloexecDefault = 0x4000;
    private const int SigKill = 9;
    private const int WNoHang = 1;
    private const int EIntr = 4;
    private const int EChild = 10;
    private const int ORdOnly = 0;
    private const int OWrOnly = 1;

    private readonly object gate = new();
    private readonly AnonymousPipeServerStream? input;
    private readonly AnonymousPipeServerStream output;
    private readonly AnonymousPipeServerStream? error;
    private bool reaped;

    private ChildProcess(int id, AnonymousPipeServerStream? input, AnonymousPipeServerStream output, AnonymousPipeServerStream? error)
    {
        this.Id = id;
        this.input = input;
        this.output = output;
        this.error = error;
        this.StandardInput = input is null ? null : new StreamWriter(input, new UTF8Encoding(false));
        this.StandardOutput = new StreamReader(output, Encoding.UTF8);
        this.StandardError = error is null ? null : new StreamReader(error, Encoding.UTF8);
    }

    /// <summary>The child's pid, which is also its session and process group ID.</summary>
    public int Id { get; }

    /// <summary>Null unless started with <c>redirectInput</c>; otherwise the child reads /dev/null.</summary>
    public StreamWriter? StandardInput { get; }

    public StreamReader StandardOutput { get; }

    /// <summary>Null unless started with <c>captureError</c>; otherwise the child writes to /dev/null.</summary>
    public StreamReader? StandardError { get; }

    /// <summary>The exit code, or 128 plus the signal that ended the child, as Process reports it. Valid once it has exited.</summary>
    public int ExitCode { get; private set; }

    public bool HasExited => this.TryReap(block: false);

    /// <summary>Starts <paramref name="file"/>, searching PATH when it has no slash. Only its standard streams are inherited.</summary>
    public static ChildProcess Start(string file, IEnumerable<string> args, bool redirectInput = false, bool captureError = true, string? workingDirectory = null)
    {
        var input = redirectInput ? new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.None) : null;
        var output = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.None);
        var error = captureError ? new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.None) : null;
        var actions = IntPtr.Zero;
        var attributes = IntPtr.Zero;
        var strings = new List<IntPtr>();
        try
        {
            Check(posix_spawn_file_actions_init(ref actions));
            Check(posix_spawnattr_init(ref attributes));
            Redirect(ref actions, 0, input, ORdOnly);
            Redirect(ref actions, 1, output, OWrOnly);
            Redirect(ref actions, 2, error, OWrOnly);
            if (workingDirectory is not null)
            {
                Check(posix_spawn_file_actions_addchdir_np(ref actions, workingDirectory));
            }

            // As Process does: every signal back to its default and none blocked, since the runtime ignores SIGPIPE.
            var all = uint.MaxValue;
            var none = 0u;
            Check(posix_spawnattr_setsigdefault(ref attributes, ref all));
            Check(posix_spawnattr_setsigmask(ref attributes, ref none));
            Check(posix_spawnattr_setflags(ref attributes, (short)(SpawnSetSid | SpawnSetSigDefault | SpawnSetSigMask | SpawnCloexecDefault)));

            var argv = Terminated(strings, [file, .. args]);
            var envp = Terminated(strings, Environment.GetEnvironmentVariables().Cast<DictionaryEntry>().Select(e => $"{e.Key}={e.Value}"));
            var result = posix_spawnp(out var pid, file, ref actions, ref attributes, argv, envp);
            if (result != 0)
            {
                throw new Win32Exception(result, $"Could not start {file}: {new Win32Exception(result).Message}");
            }

            input?.DisposeLocalCopyOfClientHandle();
            output.DisposeLocalCopyOfClientHandle();
            error?.DisposeLocalCopyOfClientHandle();
            return new ChildProcess(pid, input, output, error);
        }
        catch
        {
            input?.Dispose();
            output.Dispose();
            error?.Dispose();
            throw;
        }
        finally
        {
            if (actions != IntPtr.Zero)
            {
                posix_spawn_file_actions_destroy(ref actions);
            }

            if (attributes != IntPtr.Zero)
            {
                posix_spawnattr_destroy(ref attributes);
            }

            strings.ForEach(Marshal.FreeCoTaskMem);
        }
    }

    /// <summary>Waits up to <paramref name="timeout"/> for the child to exit; true if it did.</summary>
    public bool WaitForExit(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!this.TryReap(block: false))
        {
            if (DateTime.UtcNow >= deadline)
            {
                return false;
            }

            Thread.Sleep(10);
        }

        return true;
    }

    /// <summary>Kills the child and everything left in its process group, then reaps it.</summary>
    public void Kill()
    {
        lock (this.gate)
        {
            if (this.reaped)
            {
                return;
            }

            kill(-this.Id, SigKill);
            this.TryReap(block: true);
        }
    }

    public void Dispose()
    {
        this.Kill();
        this.StandardInput?.Dispose();
        this.input?.Dispose();
        this.output.Dispose();
        this.error?.Dispose();
    }

    private bool TryReap(bool block)
    {
        lock (this.gate)
        {
            while (!this.reaped)
            {
                var pid = waitpid(this.Id, out var status, block ? 0 : WNoHang);
                var errno = Marshal.GetLastPInvokeError();
                if (pid == this.Id)
                {
                    var signal = status & 0x7f;
                    this.ExitCode = signal == 0 ? (status >> 8) & 0xff : 128 + signal;
                    this.reaped = true;
                }
                else if (pid == 0)
                {
                    return false;
                }
                else if (errno == EChild)
                {
                    // Someone else reaped it.
                    this.ExitCode = -1;
                    this.reaped = true;
                }
                else if (errno != EIntr)
                {
                    return false;
                }
            }

            return true;
        }
    }

    // The child's end of the pipe becomes fd; with no pipe, fd is /dev/null.
    private static void Redirect(ref IntPtr actions, int fd, AnonymousPipeServerStream? pipe, int mode)
    {
        if (pipe is null)
        {
            Check(posix_spawn_file_actions_addopen(ref actions, fd, "/dev/null", mode, 0));
        }
        else
        {
            Check(posix_spawn_file_actions_adddup2(ref actions, (int)pipe.ClientSafePipeHandle.DangerousGetHandle(), fd));
        }
    }

    private static IntPtr[] Terminated(List<IntPtr> owned, IEnumerable<string> values)
    {
        var pointers = values.Select(Marshal.StringToCoTaskMemUTF8).ToList();
        owned.AddRange(pointers);
        return [.. pointers, IntPtr.Zero];
    }

    private static void Check(int result)
    {
        if (result != 0)
        {
            throw new Win32Exception(result);
        }
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int posix_spawnp(out int pid, [MarshalAs(UnmanagedType.LPUTF8Str)] string file, ref IntPtr fileActions, ref IntPtr attributes, IntPtr[] argv, IntPtr[] envp);

    [DllImport("libc")]
    private static extern int posix_spawn_file_actions_init(ref IntPtr actions);

    [DllImport("libc")]
    private static extern int posix_spawn_file_actions_destroy(ref IntPtr actions);

    [DllImport("libc")]
    private static extern int posix_spawn_file_actions_adddup2(ref IntPtr actions, int fd, int newFd);

    [DllImport("libc")]
    private static extern int posix_spawn_file_actions_addopen(ref IntPtr actions, int fd, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, ushort mode);

    [DllImport("libc")]
    private static extern int posix_spawn_file_actions_addchdir_np(ref IntPtr actions, [MarshalAs(UnmanagedType.LPUTF8Str)] string path);

    [DllImport("libc")]
    private static extern int posix_spawnattr_init(ref IntPtr attributes);

    [DllImport("libc")]
    private static extern int posix_spawnattr_destroy(ref IntPtr attributes);

    [DllImport("libc")]
    private static extern int posix_spawnattr_setflags(ref IntPtr attributes, short flags);

    [DllImport("libc")]
    private static extern int posix_spawnattr_setsigdefault(ref IntPtr attributes, ref uint signals);

    [DllImport("libc")]
    private static extern int posix_spawnattr_setsigmask(ref IntPtr attributes, ref uint signals);

    [DllImport("libc", SetLastError = true)]
    private static extern int waitpid(int pid, out int status, int options);

    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int pid, int signal);
}
