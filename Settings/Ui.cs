global using Avalonia.Controls;
global using Avalonia.Controls.Templates;
global using Avalonia.Layout;
global using Avalonia.Media;
global using Avalonia.Threading;
global using TKCode.Arc;
global using TKCode.Arc.DesignTokens;
global using Button = TKCode.Arc.Button;
global using TabItem = TKCode.Arc.TabItem;
using Avalonia;
using Avalonia.Automation;

namespace Settings;

internal static class Ui
{
    public const string Fonts = "Microsoft YaHei UI, Segoe UI, Noto Sans CJK SC, Arial";
    public static TextBox Text(bool readOnly = false, bool multiline = false, double height = 0)
    {
        FieldTextBox box = multiline ? new Textarea { IsResizable = !readOnly, FrameMinHeight = 56 } : new Input();
        if (height > 0 && box is Textarea area) area.FrameHeight = height;
        box.IsReadOnly = readOnly; box.AcceptsReturn = multiline;
        box.TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap;
        return box;
    }
    public static TextBlock Label(string text, bool secondary = false)
    {
        var label = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        if (secondary) ArcTokens.Bind(label, TextBlock.ForegroundProperty, ArcDesignTokenKind.TextSecondary);
        return label;
    }
    public static Alert StatusAlert(string name) => new() { Name = name, Title = "状态", Content = "" };
    public static void Status(Alert alert, string message, AlertTone tone = AlertTone.Info)
    {
        alert.Tone = tone;
        alert.Content = message;
        alert.IsOpen = true;
    }
    public static Button Button(string text, Action action, ButtonVariant variant = ButtonVariant.Secondary)
    {
        var button = new Button { Content = text, Variant = variant };
        button.Click += (_, _) => action();
        return button;
    }
    public static WrapPanel Buttons(params Control[] controls)
    {
        var panel = new WrapPanel();
        foreach (var control in controls)
        {
            control.Margin = new Thickness(0, 0, 8, 6);
            control.VerticalAlignment = VerticalAlignment.Center;
            panel.Children.Add(control);
        }
        return panel;
    }
    public static Grid Form() => new() { ColumnDefinitions = new ColumnDefinitions("124,*,96") };
    public static TextBlock Row(Grid grid, string label, Control control, Control? extra = null)
    {
        int row = grid.RowDefinitions.Count;
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        var caption = Label(label);
        bool tall = control is Textarea or Table or StackPanel or TextBlock or Alert;
        caption.VerticalAlignment = tall ? VerticalAlignment.Top : VerticalAlignment.Center;
        caption.Margin = new Thickness(0, tall ? 7 : 0, 10, 6);
        ArcTokens.Bind(caption, TextBlock.ForegroundProperty, ArcDesignTokenKind.TextSecondary);
        double top = 0;
        if (control is TextBlock text)
        {
            top = 7;
            text.VerticalAlignment = VerticalAlignment.Top;
        }
        else if (control is Button) control.HorizontalAlignment = HorizontalAlignment.Left;
        if (control is TextBox or Select or NumberField && label.Length > 0) AutomationProperties.SetName(control, label);
        control.Margin = new Thickness(0, top, 8, control is WrapPanel ? 0 : 6);
        Grid.SetRow(caption, row);
        Grid.SetRow(control, row);
        Grid.SetColumn(control, 1);
        grid.Children.Add(caption);
        grid.Children.Add(control);
        if (extra is null) Grid.SetColumnSpan(control, 2);
        else
        {
            Grid.SetRow(extra, row);
            Grid.SetColumn(extra, 2);
            extra.Margin = new Thickness(0, 0, 0, 6);
            grid.Children.Add(extra);
        }
        return caption;
    }
    public static Border Card(Control content)
    {
        var card = new Border { Padding = new Thickness(14, 10, 14, 4), BorderThickness = new Thickness(1), Child = content };
        ArcTokens.Bind(card, Border.BackgroundProperty, ArcDesignTokenKind.Surface);
        ArcTokens.Bind(card, Border.BorderBrushProperty, ArcDesignTokenKind.Border);
        ArcTokens.BindCornerRadius(card, ArcDesignTokenKind.RadiusPanel);
        return card;
    }
    public static Border Section(string title, params Control[] content)
    {
        var heading = new TextBlock { Text = title, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 0, 6) };
        ArcTokens.Bind(heading, TextBlock.FontSizeProperty, ArcDesignTokenKind.TypeBase);
        var stack = new StackPanel { Children = { heading } };
        foreach (var control in content) stack.Children.Add(control);
        return Card(stack);
    }
    public const double PageWidth = 1100;
    public static ScrollArea Scroll(Control content) => new() { Content = content };
    public static ScrollArea Page(params Control[] sections)
    {
        var stack = new StackPanel { Spacing = 10, Margin = new Thickness(0, 0, 8, 8), MaxWidth = PageWidth, HorizontalAlignment = HorizontalAlignment.Center };
        foreach (var section in sections) stack.Children.Add(section);
        return Scroll(stack);
    }
    public static RibbonTab RibbonPage(string header, string groupHeader, params Control[] commands)
    {
        var group = new RibbonGroup { Header = groupHeader, CanCollapse = false };
        foreach (var command in commands) group.Items.Add(command);
        var tab = new RibbonTab { Header = header };
        tab.Groups.Add(group);
        return tab;
    }
    public static Table Records(string caption, double maxHeight = double.PositiveInfinity) => new() { Caption = caption, EmptyMessage = "暂无数据", ItemNameOne = "条", ItemNameOther = "条", MaxHeight = maxHeight };
    public static void Column<T>(Table table, string header, Func<T, string> text, double weight = 1, Func<T, ArcDesignTokenKind?>? tone = null) where T : class
    {
        table.Columns.Add(new DataTableColumn(header, row => text((T)row))
        {
            Width = new GridLength(weight, GridUnitType.Star), IsSortable = false,
            CellTemplate = new FuncDataTemplate<T>((_, _) => new ToneText<T>(text, tone))
        });
    }
    // Table recycles cells between rows, so the cell reads its row from DataContext.
    sealed class ToneText<T>(Func<T, string> text, Func<T, ArcDesignTokenKind?>? tone) : TextBlock where T : class
    {
        IDisposable? binding;
        protected override Type StyleKeyOverride => typeof(TextBlock);
        protected override void OnDataContextChanged(EventArgs e)
        {
            base.OnDataContextChanged(e);
            VerticalAlignment = VerticalAlignment.Center;
            TextTrimming = TextTrimming.CharacterEllipsis;
            binding?.Dispose();
            if (DataContext is not T row) { Text = ""; return; }
            Text = text(row);
            binding = ArcTokens.Bind(this, ForegroundProperty, tone?.Invoke(row) ?? ArcDesignTokenKind.Foreground);
        }
    }
    public static async Task Message(Window owner, string message, string title)
    {
        var dialog = new Window { Title = title, Width = 520, SizeToContent = SizeToContent.Height,
            CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false };
        var text = Label(message);
        var close = Button("确定", () => dialog.Close(), ButtonVariant.Primary);
        close.IsDefault = true;
        close.HorizontalAlignment = HorizontalAlignment.Right;
        dialog.Content = new StackPanel { Margin = new Thickness(20), Spacing = 16, Children = { text, close } };
        await dialog.ShowDialog(owner);
    }
    public static async Task<bool> Confirm(Window owner, string message, string title)
    {
        var dialog = new Window { Title = title, Width = 520, SizeToContent = SizeToContent.Height,
            CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false };
        var text = Label(message);
        var confirm = Button("确认移除", () => dialog.Close(true), ButtonVariant.Danger);
        confirm.IsDefault = true;
        var cancel = Button("取消", () => dialog.Close(false));
        cancel.IsCancel = true;
        var buttons = Buttons(confirm, cancel);
        buttons.HorizontalAlignment = HorizontalAlignment.Right;
        dialog.Content = new StackPanel { Margin = new Thickness(20), Spacing = 16, Children = { text, buttons } };
        return await dialog.ShowDialog<bool>(owner);
    }
}
