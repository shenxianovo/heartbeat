using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Heartbeat.Desktop.UI.Controls;
using Heartbeat.Desktop.UI.Logging;
using Heartbeat.Desktop.UI.Presentation;
using Heartbeat.Desktop.UI.Views;
using Serilog;

namespace Heartbeat.Desktop.UI.Tests.Views;

public sealed class CollectorPageLayoutTests
{
    private static readonly HeadlessUnitTestSession Session =
        HeadlessUnitTestSession.StartNew(
            typeof(HeadlessEntryPoint),
            AvaloniaTestIsolationLevel.PerAssembly);

    [Fact]
    public Task SystemSettingsExpander_StretchesAcrossThePage_AndContainsCapabilityCards() =>
        Session.Dispatch(() =>
        {
            using var viewModel = ViewModels.TestViewModel.Create();
            viewModel.NavigateCommand.Execute("Collectors");
            var system = Assert.Single(viewModel.Collectors);
            var window = new MainWindow(viewModel)
            {
                Width = 900,
                Height = 620,
                AllowClose = true,
            };

            window.Show();
            Dispatcher.UIThread.RunJobs();

            var expander = Assert.Single(
                window.GetVisualDescendants().OfType<SettingsExpander>(),
                control => control.IsVisible);
            var header = Assert.Single(
                expander.GetVisualDescendants().OfType<Button>(),
                control => control.Classes.Contains("settings-expander-header"));
            Assert.True(header.Bounds.Width >= 600, $"Header width was {header.Bounds.Width}.");

            header.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();

            Assert.True(system.IsExpanded);
            Assert.Equal(4, expander.GetVisualDescendants().OfType<SettingsCard>().Count());
            var headerBackground = Assert.IsAssignableFrom<ISolidColorBrush>(header.Background);
            Assert.Equal(0, headerBackground.Color.A);

            window.Close();
        }, CancellationToken.None);

    [Fact]
    public Task SettingsPage_UsesSettingsCardsAndExpanderAsItsLayoutVocabulary() =>
        Session.Dispatch(() =>
        {
            using var viewModel = ViewModels.TestViewModel.Create();
            viewModel.NavigateCommand.Execute("Settings");
            var window = new MainWindow(viewModel)
            {
                Width = 900,
                Height = 620,
                AllowClose = true,
            };

            window.Show();
            Dispatcher.UIThread.RunJobs();

            var cards = window.GetVisualDescendants()
                .OfType<SettingsCard>()
                .ToArray();
            var cardHeaders = cards
                .Select(card => card.Header?.ToString())
                .ToHashSet();
            Assert.Contains("API Key", cardHeaders);
            Assert.Contains("设备名称", cardHeaders);
            Assert.Contains("上传间隔", cardHeaders);
            Assert.Contains("登录时启动", cardHeaders);
            Assert.Contains("主题", cardHeaders);
            Assert.Contains("软件更新", cardHeaders);
            Assert.All(cards, card =>
                Assert.True(card.Bounds.Width >= 600, $"{card.Header} width was {card.Bounds.Width}."));

            var diagnostics = Assert.Single(
                window.GetVisualDescendants().OfType<SettingsExpander>(),
                control => control.Header?.ToString() == "诊断");
            Assert.True(diagnostics.Bounds.Width >= 600, $"Diagnostics width was {diagnostics.Bounds.Width}.");

            window.Close();
        }, CancellationToken.None);

    [Fact]
    public Task DiagnosticsLogViewer_ShowsAVerticalScrollbarAndFollowsTheLiveTail() =>
        Session.Dispatch(() =>
        {
            var logs = new ViewModels.FakeLogFeed(
                Enumerable.Range(0, 80)
                    .Select(index => new LogEntry($"log line {index}", Serilog.Events.LogEventLevel.Information))
                    .ToArray());
            using var viewModel = ViewModels.TestViewModel.Create(logs: logs);
            viewModel.NavigateCommand.Execute("Settings");
            viewModel.IsDiagnosticsExpanded = true;
            var window = new MainWindow(viewModel)
            {
                Width = 900,
                Height = 620,
                AllowClose = true,
            };

            window.Show();
            Dispatcher.UIThread.RunJobs();

            var logViewer = Assert.Single(
                window.GetVisualDescendants().OfType<TailScrollViewer>());
            Assert.True(
                logViewer.Extent.Height > logViewer.Viewport.Height,
                $"Extent={logViewer.Extent}, Viewport={logViewer.Viewport}, Bounds={logViewer.Bounds}, " +
                $"Visible={logViewer.IsVisible}, Effective={logViewer.IsEffectivelyVisible}, " +
                $"TextLength={viewModel.LogText.Length}");
            var verticalScrollbar = Assert.Single(
                logViewer.GetVisualDescendants().OfType<ScrollBar>(),
                scrollbar => scrollbar.Orientation == Avalonia.Layout.Orientation.Vertical);
            Assert.True(verticalScrollbar.IsVisible);

            var previousExtent = logViewer.Extent.Height;
            logs.Publish(
            [
                new LogEntry(
                    string.Join(Environment.NewLine, Enumerable.Repeat("live tail marker", 10)),
                    Serilog.Events.LogEventLevel.Information)
            ]);
            Dispatcher.UIThread.RunJobs();

            Assert.True(logViewer.Extent.Height > previousExtent);
            Assert.InRange(
                logViewer.Offset.Y,
                logViewer.Extent.Height - logViewer.Viewport.Height - 1,
                logViewer.Extent.Height - logViewer.Viewport.Height + 1);

            window.Close();
        }, CancellationToken.None);

    [Fact]
    public Task DiagnosticsLogViewer_UpdatesWhenTheLiveSinkEmits() =>
        Session.Dispatch(() =>
        {
            var logs = new RingBufferSink();
            using var viewModel = ViewModels.TestViewModel.Create(logs: logs);
            viewModel.NavigateCommand.Execute("Settings");
            viewModel.IsDiagnosticsExpanded = true;
            var window = new MainWindow(viewModel)
            {
                Width = 900,
                Height = 620,
                AllowClose = true,
            };
            using var logger = new LoggerConfiguration().WriteTo.RingBuffer(logs).CreateLogger();

            window.Show();
            Dispatcher.UIThread.RunJobs();
            Task.Run(() => logger.Information("live log marker")).GetAwaiter().GetResult();
            Dispatcher.UIThread.RunJobs();

            Assert.Contains("live log marker", viewModel.LogText);
            var text = Assert.Single(
                window.GetVisualDescendants().OfType<SelectableTextBlock>(),
                control => control.Classes.Contains("log-text"));
            Assert.Contains("live log marker", text.Text);

            window.Close();
        }, CancellationToken.None);

    public sealed class HeadlessEntryPoint
    {
        public static AppBuilder BuildAvaloniaApp() =>
            AppBuilder.Configure<TestApplication>()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions());
    }

    private sealed class TestApplication : Application
    {
        public override void Initialize()
        {
            Styles.Add(new FluentTheme());
            Styles.Add(new StyleInclude(new Uri("avares://Heartbeat.Desktop.UI.Tests/"))
            {
                Source = new Uri("avares://Heartbeat.Desktop.UI/Themes/HeartbeatTheme.axaml"),
            });
        }
    }
}
