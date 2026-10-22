using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
// WinForms 隐式 using 与 WPF 同名类型冲突：这里显式指向 WPF 版本
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Colors = System.Windows.Media.Colors;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Orientation = System.Windows.Controls.Orientation;

namespace AguiGroupChat.Desktop;

/// <summary>
/// 可交互通知浮窗（1.0.314）：系统托盘气泡不支持按钮，到了「点通知就能跳到那条消息」的需求就无能为力，
/// 因此对**带知聚目标**的通知改用这个右下角无边框卡片，提供「打开 / 忽略」两个动作，8 秒后自动消失。
///
/// <para>多个通知自下而上堆叠；鼠标悬停暂停倒计时；关窗后重新排布剩余浮窗。
/// 浮窗不抢焦点（<c>ShowActivated=false</c>），避免打断正在输入的聊天。</para>
/// </summary>
internal sealed class NotificationToast : Window
{
    private const double ToastWidth = 344;
    private const double EdgeMargin = 16;
    private const double Gap = 8;

    private static readonly List<NotificationToast> Active = [];

    private readonly DispatcherTimer _timer;
    private bool _closing;

    public NotificationToast(string title, string body, Action onOpen, int autoCloseMs = 8000)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.Height;
        Width = ToastWidth;
        WindowStartupLocation = WindowStartupLocation.Manual;

        var card = new Border
        {
            CornerRadius = new CornerRadius(12),
            Background = new SolidColorBrush(Color.FromArgb(0xF2, 0x1C, 0x21, 0x2E)),
            Padding = new Thickness(14, 12, 14, 12),
            Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 2, Opacity = .32, Color = Colors.Black },
        };

        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.White,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        stack.Children.Add(new TextBlock
        {
            Text = body ?? "",
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromRgb(0xC7, 0xCF, 0xE0)),
            TextWrapping = TextWrapping.Wrap,
            MaxHeight = 96,
            Margin = new Thickness(0, 6, 0, 0),
        });

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 10, 0, 0),
        };
        var dismiss = MakeButton(L10n.ToastDismiss, primary: false);
        dismiss.Click += (_, _) => CloseToast();
        var open = MakeButton(L10n.ToastOpen, primary: true);
        open.Click += (_, _) =>
        {
            CloseToast();
            try { onOpen(); } catch { /* 目标窗口已关闭 */ }
        };
        actions.Children.Add(dismiss);
        actions.Children.Add(open);
        stack.Children.Add(actions);

        card.Child = stack;
        Content = card;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(Math.Max(1500, autoCloseMs)) };
        _timer.Tick += (_, _) => CloseToast();
        MouseEnter += (_, _) => _timer.Stop();
        MouseLeave += (_, _) => { if (!_closing) _timer.Start(); };
        Loaded += (_, _) =>
        {
            UpdateLayout();
            Active.Add(this);
            RepositionAll();
            _timer.Start();
        };
        Closed += (_, _) =>
        {
            _timer.Stop();
            Active.Remove(this);
            RepositionAll();
        };
    }

    private static System.Windows.Controls.Button MakeButton(string text, bool primary)
    {
        var button = new System.Windows.Controls.Button
        {
            Content = text,
            FontSize = 12,
            Padding = new Thickness(12, 4, 12, 4),
            Margin = new Thickness(8, 0, 0, 0),
            Cursor = System.Windows.Input.Cursors.Hand,
            BorderThickness = new Thickness(1),
            Foreground = primary ? Brushes.White : new SolidColorBrush(Color.FromRgb(0xC7, 0xCF, 0xE0)),
            Background = primary
                ? new SolidColorBrush(Color.FromRgb(0x4F, 0x8C, 0xFF))
                : Brushes.Transparent,
            BorderBrush = primary
                ? new SolidColorBrush(Color.FromRgb(0x4F, 0x8C, 0xFF))
                : new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF)),
        };
        return button;
    }

    private void CloseToast()
    {
        if (_closing) return;
        _closing = true;
        try { Close(); } catch { /* 已关闭 */ }
    }

    /// <summary>自下而上重新堆叠（最新一条在最下方，最贴近任务栏）。</summary>
    private static void RepositionAll()
    {
        if (Active.Count == 0) return;
        var work = SystemParameters.WorkArea;
        var bottom = work.Bottom - EdgeMargin;
        for (var i = Active.Count - 1; i >= 0; i--)
        {
            var toast = Active[i];
            try
            {
                var height = toast.ActualHeight > 0 ? toast.ActualHeight : 108;
                bottom -= height;
                toast.Left = Math.Max(work.Left, work.Right - toast.Width - EdgeMargin);
                toast.Top = Math.Max(work.Top, bottom);
                bottom -= Gap;
            }
            catch { /* 窗口已释放 */ }
        }
    }
}
