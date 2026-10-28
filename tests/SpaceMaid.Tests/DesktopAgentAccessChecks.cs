using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SpaceMaid.App;
using SpaceMaid.App.AgentAccess;
using SpaceMaid.Core.Analysis;
using SpaceMaid.Core.Scanning;
using Xunit;

namespace SpaceMaid.Tests;

public sealed partial class DesktopTests
{
    [Fact]
    [Trait("Stage", "System")]
    public void AgentAccessSharesAggregatesAndKeepsReviewHumanTriggered()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            Application? app = null; AgentAccessWindow? window = null;
            try
            {
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
                app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/SpaceMaid.App;component/Styles.xaml", UriKind.Relative) });
                var builder = new ScanBuilder { AllocationData = false };
                builder.RootIndex = builder.AddFile(new(new(1), FileAttributes.Directory, 0, 0, 0));
                var findings = new List<Opportunity>();
                for (int index = 0; index < 24; index++)
                {
                    ulong id = (ulong)(index * 2 + 2);
                    int folder = builder.AddFile(new(new(id), FileAttributes.Directory, 0, 0, 0));
                    builder.AddLink(folder, new(1), $"project-{index}");
                    int file = builder.AddFile(new(new(id + 1), FileAttributes.Normal, 104857600, 104857600, 0));
                    builder.AddLink(file, new(id), "private-output.bin");
                    findings.Add(new(folder, "Private project", "Private category", @"C:\Private\" + $"project-{index}", 0, 0, "", "", "", index % 3 == 0)
                    { ProviderId = index % 2 == 0 ? "space-maid.developer" : "space-maid.agents", IsBuildOutput = index % 2 == 0,
                        CleanupBlockReason = index % 5 == 0 ? "Kept by owning tool" : "" });
                }
                var scan = builder.Build(@"C:\Private", "fixture", TimeSpan.Zero, 0, default);
                AgentAggregateSession? session = null; int reviews = 0;
                window = new AgentAccessWindow(scan.RootPath, async (root, exclusions, level, token) =>
                    session = await AgentAggregateSession.CreateAsync(scan, findings, root, exclusions, level, true, () => true, token),
                    reviewed => { Assert.NotEmpty(reviewed.TakeReview()); reviews++; return Task.CompletedTask; });
                window.ShowActivated = false; window.ShowInTaskbar = false;
                window.WindowStartupLocation = WindowStartupLocation.Manual; window.Left = -10000; window.Top = -10000;
                window.Show();
                var share = (Button)window.FindName("ShareButton");
                var groups = (DataGrid)window.FindName("GroupsGrid");
                share.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpUntil(() => share.IsEnabled, TimeSpan.FromSeconds(10));
                Assert.Empty(groups.Items.Cast<object>()); Assert.Equal(AgentAccessLevel.Summary, session!.Level);
                share.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpUntil(() => ((TextBox)window.FindName("SetupBox")).Text.Length == 0, TimeSpan.FromSeconds(5));
                ((ComboBox)window.FindName("LevelBox")).SelectedIndex = 2;
                share.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpUntil(() => share.IsEnabled && ((TextBox)window.FindName("SetupBox")).Text.Length > 0, TimeSpan.FromSeconds(10));
                Assert.True(groups.Items.Count > 2); Assert.Equal(0, reviews);
                string config = ((TextBox)window.FindName("SetupBox")).Text;
                var suggested = session!.Groups.First(group => group.CanSuggestReview);
                var request = Task.Run(async () =>
                {
                    using var doc = JsonDocument.Parse(config);
                    var env = doc.RootElement.GetProperty("mcpServers").GetProperty("space-maid").GetProperty("env");
                    using var pipe = new NamedPipeClientStream(".", env.GetProperty("SPACEMAID_AGENT_PIPE").GetString()!, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await pipe.ConnectAsync(5000);
                    using var writer = AgentConnection.Writer(pipe); using var reader = AgentConnection.Reader(pipe);
                    await writer.WriteLineAsync(env.GetProperty("SPACEMAID_AGENT_TOKEN").GetString());
                    Assert.Equal("ready", await reader.ReadLineAsync());
                    await writer.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-11-25\",\"capabilities\":{},\"clientInfo\":{}}}");
                    Assert.Contains("protocolVersion", (await reader.ReadLineAsync())!);
                    await writer.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}");
                    await writer.WriteLineAsync(JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 2, method = "tools/call", @params = new
                        { name = "suggest_review", arguments = new { groupIds = new[] { suggested.Id }, note = "Review these groups for rebuild cost." } } }));
                    return await reader.ReadLineAsync();
                });
                PumpUntil(() => request.IsCompleted, TimeSpan.FromSeconds(10));
                Assert.Contains("awaiting_human_review", request.GetAwaiter().GetResult()!);
                Assert.Equal(0, reviews);
                var review = (Button)window.FindName("ReviewButton"); Assert.True(review.IsEnabled);
                string output = Path.Join(FindRepository(), "artifacts", "agent-access");
                Directory.CreateDirectory(output);
#pragma warning disable WPF0001
                foreach (var mode in new[] { ThemeMode.Light, ThemeMode.Dark })
                {
                    app.ThemeMode = mode;
                    Render(window, Path.Join(output, $"groups-{mode}.png"), 860, 690);
                }
#pragma warning restore WPF0001
                ((Expander)window.FindName("SetupExpander")).IsExpanded = true;
                Render(window, Path.Join(output, "setup-compact.png"), 720, 620);
                review.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpUntil(() => share.IsEnabled && reviews == 1, TimeSpan.FromSeconds(5));
                Assert.Empty(((TextBox)window.FindName("SetupBox")).Text); Assert.False(review.IsEnabled);
                Assert.Contains("Review finished", ((TextBlock)window.FindName("AccessStatus")).Text);
                window.Close(); window = null;
            }
            catch (Exception error) { failure = error; }
            finally { window?.Close(); app?.Shutdown(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(50)), "Agent access desktop check timed out.");
        if (failure is not null) throw new InvalidOperationException("Agent access desktop check failed.", failure);
    }
}
