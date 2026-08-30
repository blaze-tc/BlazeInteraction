using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Yuexin.Radar.Bridge.Wpf.Controls;
using Yuexin.Radar.Bridge.Wpf.ViewModels;
using Yuexin.Radar.Configuration;

namespace Yuexin.Radar.Bridge.Wpf.Tests;

public sealed class RadarEditorLifecycleTests
{
    [Fact]
    public void OpenSameSensorOneHundredTimes_ReusesOneEditorAndReopenCreatesOneReplacement()
    {
        MainWindowBindingTests.WpfTestHost.Instance.Invoke(() =>
        {
            var runtime = new MainWindowBindingTests.TestRuntime();
            using var viewModel = new MainViewModel(RadarAppConfiguration.CreateDefault(), runtime);
            var window = new MainWindow(viewModel, runtime);
            window.Show();
            window.UpdateLayout();
            var openButton = FindVisualChildren<Button>(window)
                .Single(button => string.Equals(button.Content as string, "放大编辑", StringComparison.Ordinal));

            for (var index = 0; index < 100; index++)
            {
                openButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

            var firstEditor = Assert.Single(OwnedEditors(window));
            var firstView = Assert.Single(FindVisualChildren<RadarPointCloudView>(firstEditor));
            firstEditor.Close();
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            Assert.True(firstView.DisplayResourcesDisposed);
            Assert.Empty(OwnedEditors(window));

            runtime.PublishSensorSnapshot(RadarUiLoadFixture.CreateSnapshot(100_000, sequence: 9));
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

            openButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            var replacement = Assert.Single(OwnedEditors(window));
            Assert.NotSame(firstEditor, replacement);

            window.Close();
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            Assert.False(replacement.IsVisible);
        });
    }

    [Fact]
    public void OwnerClose_DisposesEveryOpenSensorEditor()
    {
        MainWindowBindingTests.WpfTestHost.Instance.Invoke(() =>
        {
            var runtime = new MainWindowBindingTests.TestRuntime();
            using var viewModel = new MainViewModel(RadarAppConfiguration.CreateDefault(), runtime);
            var window = new MainWindow(viewModel, runtime);
            window.Show();
            window.UpdateLayout();
            var openButton = FindVisualChildren<Button>(window)
                .Single(button => string.Equals(button.Content as string, "放大编辑", StringComparison.Ordinal));
            openButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            var editor = Assert.Single(OwnedEditors(window));
            var editorView = Assert.Single(FindVisualChildren<RadarPointCloudView>(editor));

            window.Close();
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

            Assert.False(editor.IsVisible);
            Assert.True(editorView.DisplayResourcesDisposed);
        });
    }

    private static IReadOnlyList<Window> OwnedEditors(Window owner) => Application.Current.Windows
        .Cast<Window>()
        .Where(candidate => candidate.Owner == owner && candidate != owner)
        .ToArray();

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match) yield return match;
            foreach (var descendant in FindVisualChildren<T>(child)) yield return descendant;
        }
    }
}
