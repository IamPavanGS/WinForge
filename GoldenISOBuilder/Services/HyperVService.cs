using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace GoldenISOBuilder.Services;

public enum VmState { Idle, Creating, Running, Error }

public record VmConfig(
    string Name,
    string IsoPath,
    int    CpuCount,
    long   RamBytes,
    bool   EnableVtpm,
    bool   EnableSecureBoot);

public record VmMetrics(int CpuPercent, long RamUsedMb);
public record HvResult(bool Success, string? ErrorMessage);

public sealed class HyperVService : IDisposable
{
    public static HyperVService Instance { get; } = new();

    public string? ActiveVmName  { get; private set; }
    public VmState State         { get; private set; } = VmState.Idle;

    // Tracks the vmconnect window we launched so subsequent LaunchVmConnect()
    // calls focus the existing window instead of spawning a duplicate.
    private Process? _vmConnectProc;

    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    private const int SW_RESTORE = 9;

    // T3-A: cache the WMI scope so we don't reconnect on every 300 ms screenshot.
    // Auto-reconnects if the connection drops.
    private ManagementScope? _cachedScope;
    private readonly object _scopeLock = new();

    private ManagementScope GetScope()
    {
        lock (_scopeLock)
        {
            if (_cachedScope == null || !_cachedScope.IsConnected)
            {
                _cachedScope = new ManagementScope(@"\\.\root\virtualization\v2");
                _cachedScope.Connect();
            }
            return _cachedScope;
        }
    }

    private static readonly string VmRoot =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                     "GoldenISOBuilder", "VMs");

    // ── Hyper-V availability ──────────────────────────────────────────────────

    public bool IsHyperVAvailable()
    {
        try
        {
            var scope = new ManagementScope(@"\\.\root\virtualization\v2");
            scope.Connect();

            // Namespace alone isn't enough — confirm the management service exists.
            // It only registers when the Hyper-V role is actually enabled.
            using var searcher = new ManagementObjectSearcher(scope,
                new ObjectQuery("SELECT * FROM Msvm_VirtualSystemManagementService"));
            return searcher.Get().Cast<ManagementObject>().Any();
        }
        catch { return false; }
    }

    // ── VM creation & start ───────────────────────────────────────────────────

    public async Task<HvResult> CreateAndStartAsync(VmConfig config)
    {
        State         = VmState.Creating;
        ActiveVmName  = config.Name;

        var script = BuildCreationScript(config);
        var (ok, err) = await RunPowerShellAsync(script);

        if (!ok)
        {
            State = VmState.Error;
            return new HvResult(false, err);
        }

        State = VmState.Running;
        return new HvResult(true, null);
    }

    private string BuildCreationScript(VmConfig c)
    {
        // T3-B: escape every embedded value (defends against quotes/spaces/$)
        var vhdPath  = Path.Combine(VmRoot, c.Name, $"{c.Name}.vhdx")
                           .Replace("'", "''");
        var isoPath  = c.IsoPath.Replace("'", "''");
        var vmRoot   = VmRoot.Replace("'", "''");
        var vmName   = c.Name.Replace("'", "''");
        var vtpm     = c.EnableVtpm       ? "$true" : "$false";
        var sb_flag  = c.EnableSecureBoot ? "$true" : "$false";

        return $@"
$ErrorActionPreference = 'Stop'
$vmName  = '{vmName}'
$isoPath = '{isoPath}'
$vmRoot  = '{vmRoot}'
$vhdPath = '{vhdPath}'

# Clean up any stale VM with the same name
if (Get-VM -Name $vmName -ErrorAction SilentlyContinue) {{
    Stop-VM $vmName -Force -TurnOff -ErrorAction SilentlyContinue
    Remove-VM $vmName -Force
}}
if (Test-Path $vhdPath) {{ Remove-Item $vhdPath -Force }}

New-Item -ItemType Directory -Force -Path (Split-Path $vhdPath) | Out-Null
New-VHD  -Path $vhdPath -SizeBytes 60GB -Dynamic | Out-Null
New-VM   -Name $vmName -Generation 2 -MemoryStartupBytes {c.RamBytes} -VHDPath $vhdPath -Path $vmRoot | Out-Null
Set-VM   -Name $vmName -ProcessorCount {c.CpuCount} -AutomaticCheckpointsEnabled $false -CheckpointType Disabled

Add-VMDvdDrive -VMName $vmName -Path $isoPath

# T1-B: connect the default NIC to the Default Switch (or first available switch)
$sw = Get-VMSwitch -Name 'Default Switch' -ErrorAction SilentlyContinue
if (-not $sw) {{ $sw = Get-VMSwitch | Select-Object -First 1 }}
if ($sw) {{
    Get-VMNetworkAdapter -VMName $vmName | Connect-VMNetworkAdapter -SwitchName $sw.Name
}} else {{
    Write-Warning 'No Hyper-V switch found — VM will have no network'
}}

# Secure Boot first (must be applied before vTPM)
if ({sb_flag}) {{
    Set-VMFirmware -VMName $vmName -EnableSecureBoot On -SecureBootTemplate MicrosoftWindows
}} else {{
    Set-VMFirmware -VMName $vmName -EnableSecureBoot Off
}}

# T1-C: enable vTPM with a local Untrusted key protector (created once per host)
if ({vtpm}) {{
    try {{
        if (-not (Get-HgsGuardian -Name UntrustedGuardian -ErrorAction SilentlyContinue)) {{
            New-HgsGuardian -Name UntrustedGuardian -GenerateCertificates | Out-Null
        }}
        $owner = Get-HgsGuardian -Name UntrustedGuardian -ErrorAction Stop
        $kp    = New-HgsKeyProtector -Owner $owner -AllowUntrustedRoot
        Set-VMKeyProtector -VMName $vmName -KeyProtector $kp.RawData
        Enable-VMTPM -VMName $vmName
    }} catch {{
        Write-Warning ""vTPM could not be enabled: $($_.Exception.Message)""
    }}
}}

Set-VMFirmware -VMName $vmName -FirstBootDevice (Get-VMDvdDrive -VMName $vmName)
Start-VM -Name $vmName
Write-Output 'GIB_VM_STARTED'
";
    }

    // ── Orphan-VM reaper (T1-E) ───────────────────────────────────────────────

    // Returns the VM's Hyper-V GUID (Msvm_ComputerSystem.Name) — required by
    // MsRdpClient's PCB cookie when connecting in Hyper-V Console (VMBus) mode.
    public Task<string?> GetVmGuidAsync(string vmName) => Task.Run<string?>(() =>
    {
        try
        {
            var scope = GetScope();
            using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery(
                $"SELECT Name FROM Msvm_ComputerSystem WHERE ElementName='{vmName.Replace("'", "''")}'"));
            using var vm = searcher.Get().Cast<ManagementObject>().FirstOrDefault();
            return vm?["Name"]?.ToString();
        }
        catch { return null; }
    });

    public Task ReapOrphanedVmsAsync()
    {
        const string script = @"
$ErrorActionPreference = 'SilentlyContinue'
Get-VM -Name 'GIB-Test-*' | ForEach-Object {
    Stop-VM   $_ -Force -TurnOff
    Remove-VM $_ -Force
}
$vmRoot = ""$env:LOCALAPPDATA\GoldenISOBuilder\VMs""
if (Test-Path $vmRoot) {
    Get-ChildItem -Path $vmRoot -Directory | ForEach-Object {
        Remove-Item $_.FullName -Recurse -Force
    }
}
";
        return RunPowerShellAsync(script).ContinueWith(_ => { });
    }

    // ── Screenshot capture ────────────────────────────────────────────────────

    // Cached WMI handles for the thumbnail path. The previous implementation
    // re-queried Msvm_VirtualSystemManagementService and Msvm_ComputerSystem
    // every frame (~80–150 ms per tick). Caching cuts per-tick overhead to
    // just the thumbnail call itself.
    private ManagementObject? _thumbSvc;
    private ManagementObject? _thumbVm;
    private readonly object _thumbLock = new();

    private void EnsureThumbCache()
    {
        if (_thumbSvc != null && _thumbVm != null) return;
        var scope = GetScope();
        if (_thumbSvc == null)
        {
            using var s = new ManagementObjectSearcher(
                scope, new ObjectQuery("SELECT * FROM Msvm_VirtualSystemManagementService"));
            _thumbSvc = s.Get().Cast<ManagementObject>().FirstOrDefault();
        }
        if (_thumbVm == null && ActiveVmName != null)
        {
            using var s = new ManagementObjectSearcher(
                scope, new ObjectQuery(
                    $"SELECT * FROM Msvm_ComputerSystem WHERE ElementName='{ActiveVmName.Replace("'", "''")}'"));
            _thumbVm = s.Get().Cast<ManagementObject>().FirstOrDefault();
        }
    }

    private void DisposeThumbCache()
    {
        lock (_thumbLock)
        {
            _thumbSvc?.Dispose(); _thumbSvc = null;
            _thumbVm?.Dispose();  _thumbVm  = null;
        }
    }

    /// <summary>
    /// Captures the VM thumbnail directly into <paramref name="buffer"/> as
    /// Bgr565 pixels (2 bytes per pixel). Returns true on success. Caller
    /// must ensure buffer.Length &gt;= w * h * 2.
    /// </summary>
    public bool TryCaptureBgr565(int w, int h, byte[] buffer)
    {
        if (ActiveVmName == null || State != VmState.Running) return false;
        // Hyper-V requires even numbers and clamps thumbnail size.
        w = Math.Clamp(w, 80, 1600);
        h = Math.Clamp(h, 60, 1200);
        if (w % 2 != 0) w--;
        if (h % 2 != 0) h--;
        int needed = w * h * 2;
        if (buffer.Length < needed) return false;

        try
        {
            lock (_thumbLock)
            {
                EnsureThumbCache();
                if (_thumbSvc == null || _thumbVm == null) return false;

                var inParams = _thumbSvc.GetMethodParameters("GetVirtualSystemThumbnailImage");
                inParams["TargetSystem"]  = _thumbVm.Path.Path;
                inParams["WidthPixels"]   = (ushort)w;
                inParams["HeightPixels"]  = (ushort)h;

                using var result = _thumbSvc.InvokeMethod("GetVirtualSystemThumbnailImage", inParams, null);
                if (result == null) return false;
                if (Convert.ToUInt32(result["ReturnValue"]) != 0) return false;

                var data = (byte[])result["ImageData"];
                if (data == null || data.Length < needed) return false;

                Buffer.BlockCopy(data, 0, buffer, 0, needed);
                return true;
            }
        }
        catch
        {
            // VM may have been recreated or scope dropped — invalidate so the
            // next call rebuilds the cache.
            DisposeThumbCache();
            return false;
        }
    }

    // ── Real telemetry metrics ────────────────────────────────────────────────

    public VmMetrics GetMetrics()
    {
        if (ActiveVmName == null || State != VmState.Running)
            return new VmMetrics(0, 0);

        try
        {
            var scope = GetScope();

            using var searcher = new ManagementObjectSearcher(
                scope, new ObjectQuery(
                    $"SELECT ProcessorLoad, MemoryUsage FROM Msvm_SummaryInformation " +
                    $"WHERE ElementName = '{ActiveVmName.Replace("'", "''")}'"));

            using var summary = searcher.Get().Cast<ManagementObject>().FirstOrDefault();
            if (summary == null) return new VmMetrics(0, 0);

            int  cpu = Convert.ToInt32(summary["ProcessorLoad"]);
            long ram = Convert.ToInt64(summary["MemoryUsage"]);   // MB
            return new VmMetrics(cpu, ram);
        }
        catch { return new VmMetrics(0, 0); }
    }

    // ── VM control actions ────────────────────────────────────────────────────

    public async Task ResetAsync()
    {
        if (ActiveVmName == null) return;
        await RunPowerShellAsync($"Restart-VM -Name '{ActiveVmName.Replace("'","''")}' -Force");
    }

    // T2-C: real Ctrl+Alt+Del via WMI keyboard injection
    public Task SendCtrlAltDelAsync() => Task.Run(() =>
    {
        if (ActiveVmName == null) return;
        try
        {
            var scope = GetScope();

            using var vmSearcher = new ManagementObjectSearcher(scope,
                new ObjectQuery(
                    $"SELECT * FROM Msvm_ComputerSystem WHERE ElementName = '{ActiveVmName.Replace("'","''")}'"));
            using var vm = vmSearcher.Get().Cast<ManagementObject>().FirstOrDefault();
            if (vm == null) return;

            using var kbSearcher = new ManagementObjectSearcher(scope,
                new RelatedObjectQuery(vm.Path.Path, "Msvm_Keyboard"));
            using var kb = kbSearcher.Get().Cast<ManagementObject>().FirstOrDefault();
            kb?.InvokeMethod("TypeCtrlAltDel", null);
        }
        catch { /* ignored — VM may not be in a state that accepts input yet */ }
    });

    public async Task<string> CreateSnapshotAsync()
    {
        if (ActiveVmName == null) return "No active VM";
        var snapName = $"Snapshot-{DateTime.Now:HHmmss}";
        var (ok, err) = await RunPowerShellAsync(
            $"Checkpoint-VM -Name '{ActiveVmName.Replace("'","''")}' -SnapshotName '{snapName}'");
        return ok ? snapName : (err ?? "Snapshot failed");
    }

    public void LaunchVmConnect()
    {
        if (ActiveVmName == null) return;

        // 1) Tracked process still alive → focus its window.
        if (TryFocusTrackedVmConnect()) return;

        // 2) Fallback — enumerate top-level windows for vmconnect's title pattern.
        //    Handles the case where the user launched vmconnect for the same VM
        //    from Hyper-V Manager outside our app.
        if (TryFocusVmConnectByTitle(ActiveVmName)) return;

        // 3) No existing window → launch a new vmconnect and track it.
        try
        {
            _vmConnectProc = Process.Start(new ProcessStartInfo
            {
                FileName        = "vmconnect.exe",
                Arguments       = $"localhost \"{ActiveVmName}\"",
                UseShellExecute = true
            });
        }
        catch
        {
            // vmconnect.exe not found — Hyper-V tools not installed
            _vmConnectProc = null;
        }
    }

    private bool TryFocusTrackedVmConnect()
    {
        try
        {
            if (_vmConnectProc == null || _vmConnectProc.HasExited) return false;
            _vmConnectProc.Refresh();
            var h = _vmConnectProc.MainWindowHandle;
            if (h == IntPtr.Zero || !IsWindow(h)) return false;
            if (IsIconic(h)) ShowWindow(h, SW_RESTORE);
            return SetForegroundWindow(h);
        }
        catch { return false; }
    }

    private static bool TryFocusVmConnectByTitle(string vmName)
    {
        IntPtr found = IntPtr.Zero;
        var needle = $"{vmName} on localhost";
        var sb = new StringBuilder(512);
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h)) return true;
            sb.Clear();
            if (GetWindowText(h, sb, sb.Capacity) <= 0) return true;
            if (sb.ToString().Contains(needle, StringComparison.OrdinalIgnoreCase))
            {
                found = h;
                return false;
            }
            return true;
        }, IntPtr.Zero);

        if (found == IntPtr.Zero) return false;
        if (IsIconic(found)) ShowWindow(found, SW_RESTORE);
        return SetForegroundWindow(found);
    }

    public void KillTrackedVmConnect()
    {
        try
        {
            if (_vmConnectProc != null && !_vmConnectProc.HasExited)
            {
                _vmConnectProc.Kill(entireProcessTree: true);
                _vmConnectProc.WaitForExit(2000);
            }
        }
        catch { }
        finally
        {
            _vmConnectProc?.Dispose();
            _vmConnectProc = null;
        }
    }

    // ── Cleanup ───────────────────────────────────────────────────────────────

    public async Task StopAndDeleteAsync()
    {
        if (ActiveVmName == null) return;

        // Close any vmconnect window we launched before tearing down the VM —
        // otherwise vmconnect pops a "VM is no longer available" dialog.
        KillTrackedVmConnect();

        // Invalidate cached thumbnail WMI handles — they reference the VM
        // that's about to disappear.
        DisposeThumbCache();

        var name    = ActiveVmName;
        var vhdDir  = Path.Combine(VmRoot, name);
        ActiveVmName = null;
        State        = VmState.Idle;

        // Best-effort: swallow all exceptions
        try
        {
            await RunPowerShellAsync($@"
$ErrorActionPreference = 'SilentlyContinue'
Stop-VM  -Name '{name.Replace("'","''")}' -Force -TurnOff
Remove-VM -Name '{name.Replace("'","''")}' -Force
");
        }
        catch { /* ignored */ }

        try
        {
            if (Directory.Exists(vhdDir))
                Directory.Delete(vhdDir, recursive: true);
        }
        catch { /* ignored */ }
    }

    // ── PowerShell runner ─────────────────────────────────────────────────────

    private static async Task<(bool ok, string? error)> RunPowerShellAsync(string script)
    {
        var tmpFile = Path.Combine(Path.GetTempPath(), $"gib_{Guid.NewGuid():N}.ps1");
        try
        {
            await File.WriteAllTextAsync(tmpFile, script, Encoding.UTF8);

            var psi = new ProcessStartInfo
            {
                FileName               = "powershell.exe",
                Arguments              = $"-NonInteractive -ExecutionPolicy Bypass -File \"{tmpFile}\"",
                UseShellExecute        = false,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                CreateNoWindow         = true
            };

            using var proc = Process.Start(psi)!;
            // Bind the powershell child to a job object that's killed when our
            // app exits — prevents orphan cleanup scripts surviving an app crash
            // and saturating disk IO on the next launch.
            try { AssignToHostJob(proc.Handle); } catch { /* best effort */ }

            var stdout = await proc.StandardOutput.ReadToEndAsync();
            var stderr = await proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync();

            if (proc.ExitCode != 0)
            {
                // T1-G: extract a meaningful one-paragraph error (last few non-empty lines)
                var combined = (string.IsNullOrWhiteSpace(stderr) ? stdout : stderr).Trim();
                if (combined.Length == 0) combined = "PowerShell exited with code " + proc.ExitCode;
                return (false, combined);
            }

            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
        finally
        {
            try { File.Delete(tmpFile); } catch { /* ignored */ }
        }
    }

    public void Dispose() { /* singleton — nothing to dispose */ }

    // ── Win32 job object: kill children when our app exits ───────────────────

    private static readonly IntPtr _hostJob = CreateHostJob();

    private static IntPtr CreateHostJob()
    {
        try
        {
            var job = CreateJobObject(IntPtr.Zero, null);
            if (job == IntPtr.Zero) return IntPtr.Zero;

            // Configure: kill all assigned processes when the job handle closes
            // (i.e. when our process exits, even on hard crash).
            var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
            {
                BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
                {
                    LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
                }
            };
            int size = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
            IntPtr ptr = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(info, ptr, false);
                SetInformationJobObject(job, JobObjectExtendedLimitInformation, ptr, (uint)size);
            }
            finally { Marshal.FreeHGlobal(ptr); }
            return job;
        }
        catch { return IntPtr.Zero; }
    }

    private static void AssignToHostJob(IntPtr processHandle)
    {
        if (_hostJob != IntPtr.Zero && processHandle != IntPtr.Zero)
            AssignProcessToJobObject(_hostJob, processHandle);
    }

    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;
    private const int  JobObjectExtendedLimitInformation = 9;

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
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
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(IntPtr hJob,
        int JobObjectInfoClass, IntPtr lpJobObjectInfo, uint cbJobObjectInfoLength);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);
}
