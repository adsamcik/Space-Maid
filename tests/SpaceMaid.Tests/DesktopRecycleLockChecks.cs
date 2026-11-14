using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SpaceMaid.App;
using SpaceMaid.Core.Analysis;
using Xunit;

namespace SpaceMaid.Tests;

public sealed partial class DesktopTests
{
    private static void CheckRecycleLockRecovery(MainWindow window, string artifactRoot)
    {
        using var fixture = new TestDirectory();
        string folder = Directory.CreateDirectory(Path.Join(fixture.Path, "locked folder")).FullName;
        string file = Path.Join(folder, "payload.txt");
        File.WriteAllText(file, "Owned desktop lock fixture");
        string ready = Path.Join(fixture.Path, "ready");
        string script = Path.Join(fixture.Path, "fixture.ps1");
        File.WriteAllText(script, """
            param($Payload, $Ready)
            $ErrorActionPreference = 'Stop'
            Add-Type -AssemblyName System.Windows.Forms
            $handle = [IO.File]::Open($Payload, 'Open', 'Read', 'Read')
            try {
                $window = New-Object Windows.Forms.Form
                $window.Text = 'Space Maid lock test'
                $window.Width = 270
                $window.Height = 90
                $window.StartPosition = 'Manual'
                $window.Left = 40
                $window.Top = 40
                $window.ShowInTaskbar = $true
                $window.Opacity = 0.05
                $window.Add_Shown({ [IO.File]::WriteAllText($Ready, 'Ready') }.GetNewClosure())
                [Windows.Forms.Application]::Run($window)
            }
            finally { $handle.Dispose() }
            """);
        var start = new ProcessStartInfo(Path.Join(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (string argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-STA", "-File", script, file, ready }) start.ArgumentList.Add(argument);
        using var lockingApp = Process.Start(start)!;
        var appErrors = lockingApp.StandardError.ReadToEndAsync();
        _ = lockingApp.StandardOutput.ReadToEndAsync();
        string? recycledPath = null;
        var timeline = Path.Join(artifactRoot, "artifacts", "recycle-lock-test.tsv");
        void Note(string step) => File.AppendAllText(timeline, $"{DateTime.UtcNow:O}\t{step}\n");
        try
        {
            Note("waiting for fixture");
            PumpUntil(() => File.Exists(ready) || lockingApp.HasExited, TimeSpan.FromSeconds(20));
            Assert.False(lockingApp.HasExited, lockingApp.HasExited && appErrors.IsCompletedSuccessfully ? appErrors.Result : null);
            Note("fixture ready");
            var target = FolderDeletionTarget.CaptureCurrent(folder, new FileInfo(file).Length);
            var dialog = new RecycleLocksWindow(target, new(window.Operations.Files), default) { Owner = window };

            bool checkedOwner = false;
            Exception? failure = null;
            var unlock = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
            unlock.Tick += (_, _) =>
            {
                var close = (Button)dialog.FindName("CloseAppsButton");
                if (!close.IsVisible) return;
                unlock.Stop();
                try
                {
                    var owners = (ListBox)dialog.FindName("Owners");
                    var owner = Assert.Single(owners.Items.Cast<RecycleLockOwner>(), item => item.Pid == lockingApp.Id);
                    Assert.True(owner.CanClose);
                    Assert.False(lockingApp.HasExited);
                    Assert.False(close.IsEnabled);
                    owners.SelectedItems.Add(owner);
                    Assert.True(close.IsEnabled);
                    Render(dialog, Path.Join(artifactRoot, "artifacts", "ui", "recycle-open-apps.png"), 540, 300);
                    checkedOwner = true;
                    Note("requesting app close");
                    close.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                }
                catch (Exception error) { failure = error; dialog.Close(); }
            };
            var deadline = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
            deadline.Tick += (_, _) =>
            {
                deadline.Stop();
                if (dialog.IsVisible)
                {
                    Note(((TextBlock)dialog.FindName("ResultText")).Text);
                    Note(string.Join(", ", ((ListBox)dialog.FindName("Owners")).Items.Cast<RecycleLockOwner>()));
                    lockingApp.Refresh();
                    if (!lockingApp.HasExited) Note($"Fixture PID {lockingApp.Id}, session {lockingApp.SessionId}, window {lockingApp.MainWindowHandle}");
                    dialog.Close();
                }
            };
            unlock.Start(); deadline.Start();
            bool confirmed;
            try
            {
                confirmed = dialog.ShowDialog() == true;
            }
            finally { unlock.Stop(); deadline.Stop(); }
            Note("lock recovery completed");
            if (failure is not null) throw failure;
            Assert.True(checkedOwner);
            Assert.True(confirmed);
            recycledPath = Finish(window.Operations.RecycleAsync(target, 0, default)).RecycledPath;
            Assert.NotNull(recycledPath);
            Assert.Equal("Owned desktop lock fixture", File.ReadAllText(Path.Join(recycledPath, Path.GetFileName(file))));
            Assert.False(Directory.Exists(folder));
            PumpUntil(() => lockingApp.HasExited, TimeSpan.FromSeconds(10));
            Assert.Equal(0, lockingApp.ExitCode);
        }
        finally
        {
            if (!lockingApp.HasExited)
            {
                // The test owns this exact child process and only its fixture.
                lockingApp.Kill();
                lockingApp.WaitForExit(5000);
            }
            if (recycledPath is not null) DeletionTests.RestoreFixture(recycledPath, folder);
        }
    }
}
