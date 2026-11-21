using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using TKCode.Arc;
using Xunit;
using Button = TKCode.Arc.Button;

// Boots the headless Avalonia app on a dedicated STA thread and pumps the
// dispatcher loop there, matching the old STAThread entry point. Test bodies
// marshal through Run so window/control access stays on the UI thread.
static class Ui
{
    static readonly TaskCompletionSource Ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    static Ui()
    {
        var thread = new Thread(() =>
        {
            Settings.Program.UseArcTheme(AppBuilder.Configure<Settings.App>()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions()).UseHarfBuzz()).SetupWithoutStarting();
            Ready.SetResult();
            Dispatcher.UIThread.MainLoop(CancellationToken.None);
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    internal static async Task Run(Func<Task> work)
    {
        await Ready.Task;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.UIThread.Post(async () =>
        {
            try { await work(); done.SetResult(); }
            catch (Exception e) { done.SetException(e); }
        });
        await done.Task;
    }

    internal static void Expect(bool condition, string text) => Assert.True(condition, text);

    internal static IEnumerable<Control> Controls(Control control)
    {
        yield return control;
        if (control is Ribbon ribbon)
            foreach (var tab in ribbon.Tabs)
                foreach (var child in Controls(tab)) yield return child;
        if (control is RibbonTab ribbonTab)
            foreach (var group in ribbonTab.Groups)
                foreach (var child in Controls(group)) yield return child;
        if (control is RibbonGroup ribbonGroup)
            foreach (var item in ribbonGroup.Items.OfType<Control>())
                foreach (var child in Controls(item)) yield return child;
        if (control is Panel panel)
            foreach (var item in panel.Children)
                foreach (var child in Controls(item)) yield return child;
        if (control is Border { Child: Control inner })
            foreach (var child in Controls(inner)) yield return child;
        if (control is ContentControl { Content: Control content })
            foreach (var child in Controls(content)) yield return child;
        if (control is ItemsControl items)
            foreach (var item in items.Items.OfType<Control>())
                foreach (var child in Controls(item)) yield return child;
    }
    internal static T Named<T>(Control control, string name) where T : Control => Controls(control).OfType<T>().First(c => c.Name == name);
    internal static void Click(Control control, string content) => Controls(control).OfType<Button>().First(b => b.Content?.ToString() == content && b.IsVisible).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    internal static async Task Until(Func<bool> condition)
    {
        for (int i = 0; i < 100 && !condition(); i++) await Task.Delay(10);
        Expect(condition(), "asynchronous UI operation completes");
    }
}
