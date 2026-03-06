using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Shell;

namespace Wingman;

public partial class TabBar : UserControl
{
    private static readonly SolidColorBrush ActiveBg = new(Color.FromRgb(0x1E, 0x1E, 0x1E));
    private static readonly SolidColorBrush InactiveBg = new(Color.FromRgb(0x2D, 0x2D, 0x2D));
    private static readonly SolidColorBrush HighlightBg = new(Color.FromRgb(0x26, 0x4F, 0x78));
    private static readonly SolidColorBrush AccentBorder = new(Color.FromRgb(0x0E, 0x63, 0x9C));
    private static readonly SolidColorBrush TextColor = new(Color.FromRgb(0xCC, 0xCC, 0xCC));
    private static readonly SolidColorBrush DimTextColor = new(Color.FromRgb(0x99, 0x99, 0x99));

    private readonly HashSet<Guid> _highlightedTabs = [];

    public event Action<Guid>? TabSelected;
    public event Action<Guid>? TabCloseRequested;
    public event Action? NewTabRequested;
    public event Action<Guid, string>? TabRenamed;

    private readonly Dictionary<Guid, TabItemData> _tabs = [];
    private Guid? _activeTabId;

    private static readonly Geometry MaximizeGeometry = Geometry.Parse("M 0,0 H 10 V 10 H 0 Z");
    private static readonly Geometry RestoreGeometry = Geometry.Parse("M 0,2 H 8 V 10 H 0 Z M 2,2 V 0 H 10 V 8 H 8");

    public TabBar()
    {
        InitializeComponent();
        TabScroller.ScrollChanged += (_, _) => UpdateNewTabButtonPosition();
        Loaded += (_, _) =>
        {
            var window = Window.GetWindow(this);
            if (window != null)
            {
                window.StateChanged += (_, _) => UpdateMaximizeRestoreIcon();
                UpdateMaximizeRestoreIcon();
            }
            UpdateNewTabButtonPosition();
        };
    }

    public void AddTab(Guid id, string title)
    {
        var titleText = new TextBlock
        {
            Text = title,
            Foreground = TextColor,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 4, 0),
            MaxWidth = 150,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var renameBox = new TextBox
        {
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 4, 0),
            MaxWidth = 150,
            Background = new SolidColorBrush(Color.FromRgb(0x3C, 0x3C, 0x3C)),
            Foreground = TextColor,
            CaretBrush = TextColor,
            BorderThickness = new Thickness(1),
            BorderBrush = AccentBorder,
            Padding = new Thickness(2, 0, 2, 0),
            Visibility = Visibility.Collapsed,
            FocusVisualStyle = null,
        };

        var closeBtn = new Button
        {
            Content = "\u00D7",
            FontSize = 16,
            Width = 24,
            Height = 24,
            Background = Brushes.Transparent,
            Foreground = DimTextColor,
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
            Focusable = false,
            FocusVisualStyle = null,
            Visibility = Visibility.Visible,
            VerticalAlignment = VerticalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 4, 0),
            Template = MakeCloseButtonTemplate(),
        };

        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(titleText);
        content.Children.Add(renameBox);
        content.Children.Add(closeBtn);

        var tabItem = new Border
        {
            Background = Brushes.Transparent,
            CornerRadius = new CornerRadius(6, 6, 0, 0),
            Child = content,
            Cursor = Cursors.Hand,
            Margin = new Thickness(1, 4, 1, 0),
            Padding = new Thickness(4, 4, 0, 4),
        };

        WindowChrome.SetIsHitTestVisibleInChrome(tabItem, true);

        var data = new TabItemData(id, tabItem, titleText, closeBtn, renameBox);
        _tabs[id] = data;

        tabItem.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2)
            {
                StartRename(data);
                e.Handled = true;
            }
            else
            {
                TabSelected?.Invoke(id);
                e.Handled = true;
            }
        };

        tabItem.MouseDown += (_, e) =>
        {
            if (e.ChangedButton == MouseButton.Middle)
            {
                TabCloseRequested?.Invoke(id);
                e.Handled = true;
            }
        };

        tabItem.MouseEnter += (_, _) =>
        {
            if (_activeTabId != id)
                tabItem.Background = InactiveBg;
        };
        tabItem.MouseLeave += (_, _) =>
        {
            if (_activeTabId != id)
                tabItem.Background = _highlightedTabs.Contains(id) ? HighlightBg : Brushes.Transparent;
        };

        closeBtn.Click += (_, e) =>
        {
            TabCloseRequested?.Invoke(id);
            e.Handled = true;
        };

        renameBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                CommitRename(data);
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                CancelRename(data);
                e.Handled = true;
            }
        };
        renameBox.LostFocus += (_, _) => CommitRename(data);

        TabPanel.Children.Add(tabItem);
    }

    public void RemoveTab(Guid id)
    {
        if (!_tabs.TryGetValue(id, out var data)) return;
        TabPanel.Children.Remove(data.Item);
        _tabs.Remove(id);
    }

    public void SetActiveTab(Guid id)
    {
        if (_activeTabId.HasValue && _tabs.TryGetValue(_activeTabId.Value, out var oldData))
        {
            oldData.Item.Background = _highlightedTabs.Contains(_activeTabId.Value) ? HighlightBg : Brushes.Transparent;
        }

        _activeTabId = id;

        if (_tabs.TryGetValue(id, out var newData))
        {
            newData.Item.Background = ActiveBg;
        }
    }

    public void HighlightTab(Guid id)
    {
        if (_activeTabId == id) return;
        _highlightedTabs.Add(id);
        if (_tabs.TryGetValue(id, out var data))
            data.Item.Background = HighlightBg;
    }

    public void ClearHighlight(Guid id)
    {
        _highlightedTabs.Remove(id);
        if (_tabs.TryGetValue(id, out var data) && _activeTabId != id)
            data.Item.Background = Brushes.Transparent;
    }

    public void UpdateTitle(Guid id, string title, char? spinner = null)
    {
        if (_tabs.TryGetValue(id, out var data))
            data.TitleText.Text = spinner != null ? $"{spinner} {title}" : title;
    }

    private void OnNewTabClick(object sender, RoutedEventArgs e)
    {
        NewTabRequested?.Invoke();
    }

    private static void StartRename(TabItemData data)
    {
        data.RenameBox.Text = data.TitleText.Text;
        data.TitleText.Visibility = Visibility.Collapsed;
        data.RenameBox.Visibility = Visibility.Visible;
        data.RenameBox.Focus();
        data.RenameBox.SelectAll();
    }

    private void CommitRename(TabItemData data)
    {
        if (data.RenameBox.Visibility != Visibility.Visible) return;
        var newTitle = data.RenameBox.Text.Trim();
        if (!string.IsNullOrEmpty(newTitle))
        {
            data.TitleText.Text = newTitle;
            TabRenamed?.Invoke(data.Id, newTitle);
        }
        data.RenameBox.Visibility = Visibility.Collapsed;
        data.TitleText.Visibility = Visibility.Visible;
    }

    private static void CancelRename(TabItemData data)
    {
        data.RenameBox.Visibility = Visibility.Collapsed;
        data.TitleText.Visibility = Visibility.Visible;
    }

    private static ControlTemplate MakeCloseButtonTemplate()
    {
        var template = new ControlTemplate(typeof(Button));
        var bd = new FrameworkElementFactory(typeof(Border));
        bd.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(3));
        var cp = new FrameworkElementFactory(typeof(ContentPresenter));
        cp.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        cp.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        bd.AppendChild(cp);
        template.VisualTree = bd;
        var trigger = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        trigger.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Color.FromRgb(0x4D, 0x4D, 0x4D))));
        trigger.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.White));
        template.Triggers.Add(trigger);
        return template;
    }

    private void OnAppIconClick(object sender, RoutedEventArgs e)
    {
        var window = Window.GetWindow(this);
        if (window == null) return;
        var point = AppIconButton.PointToScreen(new Point(0, AppIconButton.ActualHeight));
        SystemCommands.ShowSystemMenu(window, point);
    }

    private void OnMinimizeClick(object sender, RoutedEventArgs e)
    {
        var window = Window.GetWindow(this);
        if (window != null) SystemCommands.MinimizeWindow(window);
    }

    private void OnMaximizeRestoreClick(object sender, RoutedEventArgs e)
    {
        var window = Window.GetWindow(this);
        if (window == null) return;
        if (window.WindowState == WindowState.Maximized)
            SystemCommands.RestoreWindow(window);
        else
            SystemCommands.MaximizeWindow(window);
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        var window = Window.GetWindow(this);
        if (window != null) SystemCommands.CloseWindow(window);
    }

    private void UpdateNewTabButtonPosition()
    {
        var tabsWidth = TabScroller.ExtentWidth;
        var viewportWidth = TabScroller.ViewportWidth;

        if (tabsWidth < viewportWidth)
            NewTabButton.RenderTransform = new TranslateTransform(tabsWidth - viewportWidth, 0);
        else
            NewTabButton.RenderTransform = Transform.Identity;
    }

    private void UpdateMaximizeRestoreIcon()
    {
        var isMaximized = Window.GetWindow(this)?.WindowState == WindowState.Maximized;
        MaximizeButton.Content = new Path
        {
            Data = isMaximized ? RestoreGeometry : MaximizeGeometry,
            Stroke = TextColor,
            StrokeThickness = 1,
            Fill = Brushes.Transparent,
            Stretch = Stretch.None,
        };
        MaximizeButton.ToolTip = isMaximized ? "Restore Down" : "Maximize";
    }

    private sealed record TabItemData(Guid Id, Border Item, TextBlock TitleText, Button CloseButton, TextBox RenameBox);
}
