using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Notch.Models;
using Notch.ViewModels;

namespace Notch.Views;

public partial class IslandWindow : Window
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    // Breite, Höhe, Radius, Kopfzeilenhöhe je Zustand
    private static readonly Dictionary<IslandState, (double W, double H, double R, double Header)> Layout = new()
    {
        [IslandState.Idle]     = (90, 8, 4, 36),
        [IslandState.Compact]  = (200, 36, 18, 36),
        [IslandState.Expanded] = (400, 88, 30, 88),
        [IslandState.Large]    = (400, 190, 34, 88),
    };

    private static readonly TimeSpan MorphDuration = TimeSpan.FromMilliseconds(350);

    public static readonly DependencyProperty IslandRadiusProperty = DependencyProperty.Register(
        nameof(IslandRadius), typeof(double), typeof(IslandWindow),
        new PropertyMetadata(4.0, (d, e) => ((IslandWindow)d).Island.CornerRadius = new CornerRadius((double)e.NewValue)));

    public double IslandRadius
    {
        get => (double)GetValue(IslandRadiusProperty);
        set => SetValue(IslandRadiusProperty, value);
    }

    private readonly IslandViewModel _vm;
    private IslandState _prevState = IslandState.Idle;

    public IslandWindow(IslandViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;

        Left = (SystemParameters.PrimaryScreenWidth - Width) / 2;
        Top = 0;

        vm.PropertyChanged += OnVmChanged;
        vm.EventShown += kind =>
        {
            Flash();
            if (kind == EventKind.InputNeeded) Pulse();
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // Nie Fokus übernehmen, nicht in Alt+Tab erscheinen
        var hwnd = new WindowInteropHelper(this).Handle;
        SetWindowLong(hwnd, GWL_EXSTYLE, GetWindowLong(hwnd, GWL_EXSTYLE) | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(IslandViewModel.State): ApplyState(_vm.State); break;
            case nameof(IslandViewModel.IsRunning): UpdateWave(_vm.IsRunning); break;
        }
    }

    private void ApplyState(IslandState state)
    {
        var l = Layout[state];
        var spring = (IEasingFunction)FindResource("EaseSpring");
        var idle = state == IslandState.Idle;

        Island.IsHitTestVisible = !idle;
        if (!idle) Header.Height = l.Header;

        // Drop: nur beim Erscheinen von oberhalb der Bildschirmkante hineinfallen
        if (_prevState == IslandState.Idle && !idle)
        {
            IslandShift.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(-(l.H + 24), 0, TimeSpan.FromMilliseconds(480)) { EasingFunction = spring });
        }
        _prevState = state;

        Island.BeginAnimation(FrameworkElement.WidthProperty, new DoubleAnimation(l.W, MorphDuration) { EasingFunction = spring });
        Island.BeginAnimation(FrameworkElement.HeightProperty, new DoubleAnimation(l.H, MorphDuration) { EasingFunction = spring });
        BeginAnimation(IslandRadiusProperty, new DoubleAnimation(l.R, MorphDuration) { EasingFunction = spring });

        Island.BeginAnimation(OpacityProperty, new DoubleAnimation(idle ? 0 : 1,
            TimeSpan.FromMilliseconds(idle ? 250 : 150)));

        // Inhalt mit 100 ms Versatz einblenden
        Body.BeginAnimation(OpacityProperty, null);
        if (idle)
        {
            Body.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(120)));
            return;
        }
        Body.Opacity = 0;
        Body.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220))
        {
            BeginTime = TimeSpan.FromMilliseconds(100),
            EasingFunction = (IEasingFunction)FindResource("EaseOut"),
        });
    }

    private void UpdateWave(bool run)
    {
        ScaleTransform[] bars = { Bar1T, Bar2T, Bar3T, Bar4T };
        double[] periods = { 0.55, 0.8, 0.65, 0.95 };

        for (var i = 0; i < bars.Length; i++)
        {
            if (!run)
            {
                bars[i].BeginAnimation(ScaleTransform.ScaleYProperty, null);
                continue;
            }
            bars[i].BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.35, 1.0, TimeSpan.FromSeconds(periods[i]))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                BeginTime = TimeSpan.FromMilliseconds(i * 120),
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            });
        }
    }

    private void Flash()
    {
        Glow.BorderBrush = _vm.Accent;
        if (_vm.Accent is SolidColorBrush b) GlowShadow.Color = b.Color;

        var anim = new DoubleAnimationUsingKeyFrames();
        anim.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        anim.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(180)),
            new CubicEase { EasingMode = EasingMode.EaseOut }));
        anim.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(450))));
        anim.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(900)),
            new CubicEase { EasingMode = EasingMode.EaseIn }));
        Glow.BeginAnimation(OpacityProperty, anim);
    }

    private void Pulse()
    {
        var anim = new DoubleAnimation(1, 1.3, TimeSpan.FromMilliseconds(380))
        {
            AutoReverse = true,
            BeginTime = TimeSpan.FromMilliseconds(250),
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        };
        GlyphScale.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
        GlyphScale.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
    }

    private void OnIslandEnter(object sender, MouseEventArgs e) => _vm.SetHover(true);
    private void OnIslandLeave(object sender, MouseEventArgs e) => _vm.SetHover(false);
    private void OnIslandClick(object sender, MouseButtonEventArgs e) => _vm.Click();

    private void OnActionClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string id }) _vm.InvokeAction(id);
    }

    private void OnMediaClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string command }) _vm.InvokeMedia(command);
    }

    private void OnExit(object sender, RoutedEventArgs e) => Application.Current.Shutdown();
}
