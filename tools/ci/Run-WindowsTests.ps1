[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$AssemblyPath,
    [switch]$TestUserProcess
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not $IsWindows -or $env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted') {
    throw 'This account-isolated test runner is only for GitHub-hosted Windows CI.'
}
$workspace = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$assembly = (Resolve-Path $AssemblyPath).Path
if (-not $assembly.StartsWith($workspace + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The test assembly must be inside this checkout.'
}
$results = Join-Path $workspace 'artifacts/ci-test-results'
$workerFolder = Join-Path (Split-Path $assembly) 'Extensions/space-maid.assistant/Worker'
$workerExecutable = Join-Path $workerFolder 'SpaceMaid.ModelWorker.exe'
if (-not (Test-Path -LiteralPath $workerExecutable -PathType Leaf)) {
    throw 'The test bundle must include the isolated model worker.'
}
$archive = Join-Path $workspace 'artifacts/ci-inputs/exiftool-13.42_64.zip'
$archiveHash = 'ABBE55E97868B869249E679649D89E3167330DE8CEE979F98E1B2E24075A74FC'
$administrator = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)

if ($TestUserProcess) {
    if ($administrator) { throw 'The desktop and extension tests must run without administrator access.' }
    # Known-folder lookup can expand the inherited runneradmin USERPROFILE.
    # Rebuild profile variables from this account's token and loaded profile.
    . (Join-Path $PSScriptRoot 'Initialize-TestUserEnvironment.ps1')
    Initialize-TestUserEnvironment
    & (Join-Path $PSScriptRoot 'Test-UserEnvironment.ps1')
    $env:TEMP = Join-Path $env:LOCALAPPDATA 'Temp'
    $env:TMP = $env:TEMP
    $env:SPACEMAID_TEST_MODEL_WORKER = $workerExecutable
    New-Item -ItemType Directory -Path $env:TEMP, $results -Force | Out-Null
    if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $archiveHash) { throw 'ExifTool archive hash mismatch.' }
    $toolFolder = Join-Path $env:LOCALAPPDATA 'Programs/ExifTool'
    Expand-Archive -LiteralPath $archive -DestinationPath $toolFolder
    $version = & (Join-Path $toolFolder 'ExifTool.exe') -ver
    if ($LASTEXITCODE -ne 0 -or $version -ne '13.42') { throw 'The pinned ExifTool installation did not start.' }
    Set-Location -LiteralPath $workspace
    & dotnet vstest $assembly "--ResultsDirectory:$results" '--Logger:trx;LogFileName=windows-tests.trx'
    exit $LASTEXITCODE
}

if (-not $administrator) { throw 'Account setup requires the GitHub-hosted runner administrator.' }
New-Item -ItemType Directory -Path (Split-Path $archive), $results -Force | Out-Null
# The official Windows package maintainer retains versioned archives; the
# ExifTool main site removes old downloads. Keep the complete Perl bundle.
Invoke-WebRequest -Uri 'https://oliverbetz.de/cms/files/Artikel/ExifTool-for-Windows/exiftool-13.42_64.zip' -OutFile $archive
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $archiveHash) { throw 'ExifTool archive hash mismatch.' }

# CreateProcessWithLogonW requires access to both inherited GUI objects.
# Grant only the disposable SID, then remove these exact ACEs after its tests.
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;

namespace SpaceMaid.CI {
    public sealed class DesktopPermissionLease : IDisposable {
        private readonly List<(IntPtr Handle, byte[] Ace)> grants = new List<(IntPtr, byte[])>();
        public static DesktopPermissionLease Acquire(string sid) {
            var lease = new DesktopPermissionLease();
            try {
                var identity = new SecurityIdentifier(sid);
                // READ_CONTROL plus each object's GUI rights; no WRITE_DAC,
                // WRITE_OWNER or DELETE rights are given to the test user.
                lease.Grant(GetProcessWindowStation(), identity, 0x0002037F);
                lease.Grant(GetThreadDesktop(GetCurrentThreadId()), identity, 0x000201FF);
                return lease;
            } catch { lease.Dispose(); throw; }
        }
        private void Grant(IntPtr handle, SecurityIdentifier sid, int mask) {
            if (handle == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            var acl = Read(handle);
            if (acl == null) return; // A null DACL already permits access.
            var ace = new CommonAce(AceFlags.None, AceQualifier.AccessAllowed, mask, sid, false, null);
            var bytes = new byte[ace.BinaryLength];
            ace.GetBinaryForm(bytes, 0);
            int index = 0;
            while (index < acl.Count && (acl[index].AceFlags & AceFlags.Inherited) == 0) index++;
            acl.InsertAce(index, ace);
            Write(handle, acl);
            grants.Add((handle, bytes));
        }
        public void Dispose() {
            var failures = new List<Exception>();
            foreach (var grant in grants.AsEnumerable().Reverse()) {
                try {
                    var acl = Read(grant.Handle);
                    if (acl == null) continue;
                    for (int i = acl.Count - 1; i >= 0; i--) {
                        var bytes = new byte[acl[i].BinaryLength];
                        acl[i].GetBinaryForm(bytes, 0);
                        if (bytes.SequenceEqual(grant.Ace)) { acl.RemoveAce(i); Write(grant.Handle, acl); break; }
                    }
                } catch (Exception error) { failures.Add(error); }
            }
            grants.Clear();
            if (failures.Count != 0) throw new AggregateException("Could not remove CI desktop permissions.", failures);
        }
        private static RawAcl Read(IntPtr handle) {
            uint error = GetSecurityInfo(handle, 7, 4, out _, out _, out _, out _, out var descriptor);
            if (error != 0) throw new Win32Exception((int)error);
            try {
                var bytes = new byte[GetSecurityDescriptorLength(descriptor)];
                Marshal.Copy(descriptor, bytes, 0, bytes.Length);
                return new RawSecurityDescriptor(bytes, 0).DiscretionaryAcl;
            } finally { LocalFree(descriptor); }
        }
        private static void Write(IntPtr handle, RawAcl acl) {
            var bytes = new byte[acl.BinaryLength];
            acl.GetBinaryForm(bytes, 0);
            var memory = Marshal.AllocHGlobal(bytes.Length);
            try {
                Marshal.Copy(bytes, 0, memory, bytes.Length);
                uint error = SetSecurityInfo(handle, 7, 4, IntPtr.Zero, IntPtr.Zero, memory, IntPtr.Zero);
                if (error != 0) throw new Win32Exception((int)error);
            } finally { Marshal.FreeHGlobal(memory); }
        }
        [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr GetProcessWindowStation();
        [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr GetThreadDesktop(uint threadId);
        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
        [DllImport("advapi32.dll")] private static extern uint GetSecurityDescriptorLength(IntPtr descriptor);
        [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
        [DllImport("advapi32.dll")] private static extern uint GetSecurityInfo(IntPtr handle, int type, uint info,
            out IntPtr owner, out IntPtr group, out IntPtr dacl, out IntPtr sacl, out IntPtr descriptor);
        [DllImport("advapi32.dll")] private static extern uint SetSecurityInfo(IntPtr handle, int type, uint info,
            IntPtr owner, IntPtr group, IntPtr dacl, IntPtr sacl);
    }
}
'@

$accountName = 'smtests' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
$password = ConvertTo-SecureString ('Sm!' + [Guid]::NewGuid().ToString('N') + '9a') -AsPlainText -Force
$account = $null
$process = $null
$desktopAccess = $null
try {
    $account = New-LocalUser -Name $accountName -Password $password -AccountNeverExpires -PasswordNeverExpires
    $users = Get-LocalGroup -SID 'S-1-5-32-545'
    Add-LocalGroupMember -Group $users -Member $account
    # Tests write fixtures and renders, while the real app still rejects an
    # elevated extension host. Grant only this disposable checkout to the user.
    $acl = Get-Acl -LiteralPath $workspace
    $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new(
        $account.SID, 'Modify', 'ContainerInherit, ObjectInherit', 'None', 'Allow'))
    Set-Acl -LiteralPath $workspace -AclObject $acl

    # Native isolation tests temporarily grant their AppContainer SID access
    # to worker code. Downloaded files belong to runneradmin, so allow DACL
    # changes only in this dedicated runtime package.
    $workerAcl = Get-Acl -LiteralPath $workerFolder
    $workerAcl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new(
        $account.SID, 'ChangePermissions', 'ContainerInherit, ObjectInherit', 'None', 'Allow'))
    Set-Acl -LiteralPath $workerFolder -AclObject $workerAcl

    $desktopAccess = [SpaceMaid.CI.DesktopPermissionLease]::Acquire($account.SID.Value)
    $start = [Diagnostics.ProcessStartInfo]::new((Get-Process -Id $PID).Path)
    $start.WorkingDirectory = $workspace
    $start.UseShellExecute = $false
    $start.UserName = $accountName
    $start.Domain = $env:COMPUTERNAME
    $start.Password = $password
    $start.LoadUserProfile = $true
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    # Materialize the inherited environment: a null block would instead use
    # only the new account's profile and lose CI markers and setup-dotnet PATH.
    $start.Environment['GITHUB_ACTIONS'] = $env:GITHUB_ACTIONS
    $start.Environment['RUNNER_ENVIRONMENT'] = $env:RUNNER_ENVIRONMENT
    foreach ($argument in @('-NoLogo', '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $PSCommandPath,
            '-AssemblyPath', $assembly, '-TestUserProcess')) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($start)
    $stdout = $process.StandardOutput.ReadToEndAsync()
    $stderr = $process.StandardError.ReadToEndAsync()
    if (-not $process.WaitForExit(15 * 60 * 1000)) {
        $process.Kill($true)
        $process.WaitForExit()
        throw 'Windows tests exceeded 15 minutes.'
    }
    Write-Output $stdout.GetAwaiter().GetResult()
    Write-Output $stderr.GetAwaiter().GetResult()
    $exitCode = $process.ExitCode
}
finally {
    try {
        if ($null -ne $process) {
            if (-not $process.HasExited) { $process.Kill($true); $process.WaitForExit() }
            $process.Dispose()
        }
    }
    finally {
        try { if ($null -ne $desktopAccess) { $desktopAccess.Dispose() } }
        finally {
            try { if ($null -ne $account) { Remove-LocalUser -SID $account.SID } }
            finally { $password.Dispose() }
        }
    }
}
exit $exitCode
