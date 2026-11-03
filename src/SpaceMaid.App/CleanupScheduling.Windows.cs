using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Xml.Linq;
using SpaceMaid.Core.IO;
using Velopack.Locators;

namespace SpaceMaid.App;

internal sealed record CleanupTaskLaunch(string Executable, string ArgumentsPrefix, string WorkingDirectory)
{
    internal static CleanupTaskLaunch Resolve(DiskAccess files)
    {
        string executable = Environment.ProcessPath ?? throw new InvalidOperationException("The application launcher is unavailable.");
        IVelopackLocator? locator = null;
        try { locator = VelopackLocator.Current; }
        catch (InvalidOperationException) { /* Component tests and an unpackaged app have no locator. */ }
        if (locator is not null)
        {
            if (locator.CurrentlyInstalledVersion is not null && locator.RootAppDir is { Length: > 0 } root)
            {
                // Velopack's root execution stub is stable across current/ swaps.
                string? relative = locator.ThisExeRelativePath;
                if (!string.IsNullOrEmpty(relative) && relative.IndexOfAny(['\\', '/']) < 0)
                {
                    string stub = Path.Combine(root, relative);
                    if (files.FileExists(stub)) return new(stub, "", root);
                    if (locator.UpdateExePath is { Length: > 0 } update && files.FileExists(update))
                        return new(update, "start " + Quote(relative) + " -- ", root);
                }
                throw new InvalidOperationException("The installed app launcher is missing. Repair the installation before enabling schedules.");
            }
        }
        if (!files.FileExists(executable) || !Path.GetExtension(executable).Equals(".exe", StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileNameWithoutExtension(executable).Equals("SpaceMaid.App", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Open Space Maid from its executable before enabling schedules.");
        return new(executable, "", Path.GetDirectoryName(executable)!);
    }

    internal string Arguments(CleanupDefinition definition) => ArgumentsPrefix + "--cleanup-scheduled "
        + definition.Id.ToString("D") + " " + definition.Revision.ToString(CultureInfo.InvariantCulture);
    private static string Quote(string value) => "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
}

internal interface ICleanupTaskScheduler
{
    void Register(CleanupDefinition definition, CleanupTaskLaunch launch);
    bool Matches(CleanupDefinition definition, CleanupTaskLaunch launch);
    void Remove(Guid definitionId);
}

internal sealed class WindowsCleanupTaskScheduler : ICleanupTaskScheduler
{
    private readonly string _prefix;
    private readonly string _userSid;
    internal WindowsCleanupTaskScheduler(string profileDirectory)
    {
        _userSid = WindowsIdentity.GetCurrent().User?.Value ?? throw new InvalidOperationException("The current user identity is unavailable.");
        string profile = Path.GetFullPath(profileDirectory).TrimEnd('\\').ToUpperInvariant();
        _prefix = "SpaceMaid.Cleanup." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(_userSid + "|" + profile)))[..16] + ".";
    }

    internal string TaskName(Guid id) => _prefix + id.ToString("N");

    public void Register(CleanupDefinition definition, CleanupTaskLaunch launch)
    {
        string xml = CreateXml(definition, launch, _userSid, DateTimeOffset.UtcNow);
        WithRoot(root =>
        {
            object task = root.RegisterTask(TaskName(definition.Id), xml, 6, _userSid, null, 3,
                "D:P(A;;FA;;;SY)(A;;FA;;;" + _userSid + ")");
            Release(task);
        });
    }

    public bool Matches(CleanupDefinition definition, CleanupTaskLaunch launch)
    {
        bool matches = false;
        WithRoot(root =>
        {
            object? task = null, registeredDefinition = null, principal = null;
            try
            {
                task = root.GetTask(TaskName(definition.Id));
                dynamic registered = task;
                registeredDefinition = registered.Definition;
                dynamic details = registeredDefinition;
                principal = details.Principal;
                dynamic owner = principal;
                matches = (bool)registered.Enabled && (int)owner.RunLevel == 0
                    && XmlMatches((string)registered.Xml, definition, launch, _userSid);
            }
            catch (COMException error) when (error.HResult is unchecked((int)0x80070002) or unchecked((int)0x8004130F)) { }
            catch (FileNotFoundException) { }
            finally { Release(principal); Release(registeredDefinition); Release(task); }
        });
        return matches;
    }

    public void Remove(Guid definitionId) => WithRoot(root =>
    {
        try { root.DeleteTask(TaskName(definitionId), 0); }
        catch (COMException error) when (error.HResult is unchecked((int)0x80070002) or unchecked((int)0x8004130F)) { }
        catch (FileNotFoundException) { }
    });

    internal static string CreateXml(CleanupDefinition definition, CleanupTaskLaunch launch, string sid, DateTimeOffset now)
    {
        var schedule = definition.Schedule ?? throw new ArgumentException("A weekly schedule is required.");
        // Native weekly triggers use the PC's wall clock. A timezone change is
        // detected before execution and pauses the rule instead of shifting it.
        if (schedule.TimeZoneId != TimeZoneInfo.Local.Id)
            throw new InvalidOperationException("The PC time zone changed. Review the schedule before enabling it again.");
        var next = CleanupScheduleClock.Next(schedule, now);
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        XElement E(string name, object? value = null) => new(ns + name, value);
        return new XElement(ns + "Task", new XAttribute("version", "1.4"),
            E("RegistrationInfo", new[] { E("Author", sid), E("Source", Source(definition)), E("Description", "Space Maid saved cleanup: " + definition.Name) }),
            E("Triggers", E("CalendarTrigger", new[] { E("StartBoundary", next.NominalLocalTime.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture)),
                E("Enabled", "true"), E("ScheduleByWeek", new[] { E("WeeksInterval", "1"), E("DaysOfWeek", E(schedule.Day.ToString())) }) })),
            E("Principals", new XElement(ns + "Principal", new XAttribute("id", "Owner"), E("UserId", sid), E("LogonType", "InteractiveToken"), E("RunLevel", "LeastPrivilege"))),
            E("Settings", new[] { E("MultipleInstancesPolicy", "IgnoreNew"), E("DisallowStartIfOnBatteries", "false"), E("StopIfGoingOnBatteries", "false"),
                E("AllowHardTerminate", "false"), E("StartWhenAvailable", "false"), E("RunOnlyIfNetworkAvailable", "false"), E("AllowStartOnDemand", "true"),
                E("Enabled", "true"), E("Hidden", "false"), E("WakeToRun", "false"), E("ExecutionTimeLimit", "PT0S") }),
            new XElement(ns + "Actions", new XAttribute("Context", "Owner"), E("Exec", new[] { E("Command", launch.Executable),
                E("Arguments", launch.Arguments(definition)), E("WorkingDirectory", launch.WorkingDirectory) }))).ToString(SaveOptions.DisableFormatting);
    }

    internal static bool XmlMatches(string xml, CleanupDefinition definition, CleanupTaskLaunch launch, string sid)
    {
        var schedule = definition.Schedule;
        if (schedule is null || schedule.TimeZoneId != TimeZoneInfo.Local.Id) return false;
        var document = XElement.Parse(xml);
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        string? Value(params string[] path)
        { XElement? current = document; foreach (string part in path) current = current?.Element(ns + part); return current?.Value; }
        var triggers = document.Element(ns + "Triggers")?.Elements().ToArray();
        var actions = document.Element(ns + "Actions")?.Elements().ToArray();
        var days = document.Descendants(ns + "DaysOfWeek").SingleOrDefault()?.Elements().ToArray();
        // Windows canonicalizes registered XML by omitting schema defaults.
        // Only these documented defaults may be absent; explicit overrides
        // still fail verification and non-default safety settings stay required.
        return Value("RegistrationInfo", "Source") == Source(definition)
            && Value("Principals", "Principal", "UserId") == sid && Value("Principals", "Principal", "LogonType") == "InteractiveToken"
            && (Value("Principals", "Principal", "RunLevel") ?? "LeastPrivilege") == "LeastPrivilege"
            && (Value("Settings", "StartWhenAvailable") ?? "false") == "false" && (Value("Settings", "WakeToRun") ?? "false") == "false"
            && Value("Settings", "AllowHardTerminate") == "false" && Value("Settings", "MultipleInstancesPolicy") == "IgnoreNew"
            && (Value("Settings", "Enabled") ?? "true") == "true" && Value("Settings", "StopIfGoingOnBatteries") == "false"
            && Value("Settings", "DisallowStartIfOnBatteries") == "false" && Value("Settings", "ExecutionTimeLimit") == "PT0S"
            && triggers is { Length: 1 } && triggers[0].Name == ns + "CalendarTrigger"
            && (Value("Triggers", "CalendarTrigger", "Enabled") ?? "true") == "true"
            && Value("Triggers", "CalendarTrigger", "ScheduleByWeek", "WeeksInterval") == "1"
            && days is { Length: 1 } && days[0].Name == ns + schedule.Day.ToString()
            && DateTime.TryParse(Value("Triggers", "CalendarTrigger", "StartBoundary"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var start)
            && TimeOnly.FromDateTime(start) == schedule.Time
            && actions is { Length: 1 } && actions[0].Name == ns + "Exec"
            && Value("Actions", "Exec", "Command") == launch.Executable && Value("Actions", "Exec", "Arguments") == launch.Arguments(definition)
            && Value("Actions", "Exec", "WorkingDirectory") == launch.WorkingDirectory;
    }

    private static string Source(CleanupDefinition definition) => "SpaceMaid.Cleanup/1:" + definition.Id.ToString("N") + ":" + definition.Revision.ToString(CultureInfo.InvariantCulture);
    private static void WithRoot(Action<dynamic> action)
    {
        object? service = null, folder = null;
        try
        {
            var type = Type.GetTypeFromProgID("Schedule.Service") ?? throw new InvalidOperationException("Windows Task Scheduler is unavailable.");
            service = Activator.CreateInstance(type) ?? throw new InvalidOperationException("Windows Task Scheduler could not start.");
            dynamic scheduler = service; scheduler.Connect(); folder = scheduler.GetFolder("\\"); action(folder);
        }
        finally { Release(folder); Release(service); }
    }
    private static void Release(object? value) { if (value is not null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value); }
}
