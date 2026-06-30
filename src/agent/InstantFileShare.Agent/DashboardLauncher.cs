using InstantFileShare.Core;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace InstantFileShare.Agent;

public sealed partial class DashboardLauncher(ILogger<DashboardLauncher> logger) : IUiLauncher, IDisposable
{
    private Process? _process;
    private nint _jobHandle;
    private readonly object _sync = new();

    public Task OpenDashboardAsync(CancellationToken cancellationToken)
    {
        return Launch(AgentPaths.GetRepositoryRoot());
    }

    public Task Launch(string repositoryRoot)
    {
        lock (_sync)
        {
            if (_process is { HasExited: false })
            {
                return Task.CompletedTask;
            }

            DisposeTrackedProcess();
            ReleaseJobHandle();

            var startInfo = CreateStartInfo(repositoryRoot);
            if (startInfo is null)
            {
                logger.LogWarning("Skipping dashboard launch because no installed dashboard executable or UI package.json was found under {RepositoryRoot}.", repositoryRoot);
                return Task.CompletedTask;
            }

            try
            {
                var process = Process.Start(startInfo);
                if (process is null)
                {
                    logger.LogWarning("Skipping dashboard launch because the dashboard process did not start.");
                    return Task.CompletedTask;
                }

                RegisterOutputLogging(process, startInfo);

                process.EnableRaisingEvents = true;
                process.Exited += (_, _) =>
                {
                    lock (_sync)
                    {
                        if (ReferenceEquals(_process, process))
                        {
                            DisposeTrackedProcess();
                            ReleaseJobHandle();
                        }
                    }
                };

                _process = process;
                AttachToKillOnCloseJob(process);
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Failed to start the Electron dashboard.");
                DisposeTrackedProcess();
                ReleaseJobHandle();
            }
        }

        return Task.CompletedTask;
    }

    internal static ProcessStartInfo? CreateStartInfo(string repositoryRoot)
    {
        var installedUiExe = ResolveInstalledUiExecutable();
        if (installedUiExe is not null)
        {
            return new ProcessStartInfo
            {
                FileName = installedUiExe,
                WorkingDirectory = Path.GetDirectoryName(installedUiExe)!,
                UseShellExecute = true,
            };
        }

        var uiPath = Path.Combine(repositoryRoot, "src", "ui");
        var packageJson = Path.Combine(uiPath, "package.json");
        if (!File.Exists(packageJson))
        {
            return null;
        }

        return new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = "/c npm run electron:dev",
            WorkingDirectory = uiPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
    }

    private void RegisterOutputLogging(Process process, ProcessStartInfo startInfo)
    {
        if (startInfo.RedirectStandardOutput)
        {
            process.OutputDataReceived += (_, args) => LogDashboardOutput(args.Data, LogLevel.Information);
            process.BeginOutputReadLine();
        }

        if (startInfo.RedirectStandardError)
        {
            process.ErrorDataReceived += (_, args) => LogDashboardOutput(args.Data, LogLevel.Warning);
            process.BeginErrorReadLine();
        }
    }

    private void LogDashboardOutput(string? line, LogLevel logLevel)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        logger.Log(logLevel, "Dashboard process: {Line}", line);
    }

    private static string? ResolveInstalledUiExecutable()
    {
        var baseDirectory = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(baseDirectory, "ui", "Instant File Share.exe"),
            Path.Combine(baseDirectory, "ui", "InstantFileShare.UI.exe"),
        };

        return candidates.FirstOrDefault(File.Exists);
    }

    public void Dispose()
    {
        lock (_sync)
        {
            try
            {
                _process?.Kill(entireProcessTree: true);
            }
            catch
            {
            }

            DisposeTrackedProcess();
            ReleaseJobHandle();
        }

        GC.SuppressFinalize(this);
    }

    private void AttachToKillOnCloseJob(Process process)
    {
        var jobHandle = CreateJobObject(nint.Zero, null);
        if (jobHandle == nint.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to create the dashboard job object.");
        }

        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
            {
                LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE,
            },
        };

        if (!SetInformationJobObject(
                jobHandle,
                JobObjectInfoType.ExtendedLimitInformation,
                ref info,
                Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>()))
        {
            CloseHandle(jobHandle);
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to configure the dashboard job object.");
        }

        if (!AssignProcessToJobObject(jobHandle, process.Handle))
        {
            var errorCode = Marshal.GetLastWin32Error();
            CloseHandle(jobHandle);
            throw new Win32Exception(errorCode, "Failed to attach the Electron dashboard to the agent job object.");
        }

        _jobHandle = jobHandle;
    }

    private void DisposeTrackedProcess()
    {
        _process?.Dispose();
        _process = null;
    }

    private void ReleaseJobHandle()
    {
        if (_jobHandle == nint.Zero)
        {
            return;
        }

        CloseHandle(_jobHandle);
        _jobHandle = nint.Zero;
    }

    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;

    private enum JobObjectInfoType
    {
        ExtendedLimitInformation = 9,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateJobObjectW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern nint CreateJobObject(nint jobAttributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(
        nint job,
        JobObjectInfoType jobObjectInfoClass,
        ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION jobObjectInfo,
        int cbJobObjectInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(nint job, nint process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);
}
