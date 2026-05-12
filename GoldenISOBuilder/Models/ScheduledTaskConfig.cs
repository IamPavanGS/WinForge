namespace GoldenISOBuilder.Models;

public enum TaskTriggerType
{
    Once,
    Daily,
    Weekly,
    AtLogon,
    AtStartup
}

/// <summary>
/// Describes a Windows Task Scheduler entry to be created on the deployed OS
/// via schtasks.exe inside SetupComplete.cmd.
/// </summary>
public class ScheduledTaskConfig
{
    // ── General ───────────────────────────────────────────────────────────────
    public string Name        { get; set; } = "";
    public string Description { get; set; } = "";

    // ── Trigger ───────────────────────────────────────────────────────────────
    public TaskTriggerType TriggerType { get; set; } = TaskTriggerType.Once;

    /// <summary>Start date + time used for Once / Daily / Weekly triggers.</summary>
    public DateTime StartTime { get; set; } = DateTime.Today.AddDays(1).AddHours(8);

    /// <summary>Days of week for Weekly trigger (0=Sun … 6=Sat).</summary>
    public List<int> WeekDays { get; set; } = [];

    // ── Action ────────────────────────────────────────────────────────────────
    public string ActionPath      { get; set; } = "";
    public string ActionArguments { get; set; } = "";
    public string StartInFolder   { get; set; } = "";

    // ── Security context ──────────────────────────────────────────────────────
    /// <summary>"SYSTEM" or "" (current-user / highest-privileges).</summary>
    public string RunAs { get; set; } = "SYSTEM";

    // ── Settings ──────────────────────────────────────────────────────────────
    /// <summary>Pass /Z to schtasks — deletes the task after its last scheduled run.</summary>
    public bool DeleteAfterRun            { get; set; } = false;
    public bool WakeToRun                 { get; set; } = false;
    public bool RunOnBattery              { get; set; } = true;
    public bool RunWithHighestPrivileges  { get; set; } = true;

    /// <summary>Maximum execution time in minutes. 0 = no limit.</summary>
    public int ExecutionTimeLimitMinutes  { get; set; } = 0;
}
