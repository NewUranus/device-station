using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace DeviceStation.App.Controls;

/// <summary>
/// 分段步骤轨：作业的每一步是一段，段宽按步骤权重分配。已完成的段用半透明强调色，
/// 当前段按步骤内进度填充（失败时整段填满），之后的段只画底色。
/// 直接在 Render 里画几个圆角矩形，比用 ItemsControl 生成 13 个子控件轻得多（8 台设备每 100 ms 刷新）。
/// </summary>
public sealed class StepTrack : Control
{
    public static readonly StyledProperty<IReadOnlyList<double>?> WeightsProperty =
        AvaloniaProperty.Register<StepTrack, IReadOnlyList<double>?>(nameof(Weights));

    public static readonly StyledProperty<int> CurrentIndexProperty =
        AvaloniaProperty.Register<StepTrack, int>(nameof(CurrentIndex), -1);

    public static readonly StyledProperty<double> CurrentFractionProperty =
        AvaloniaProperty.Register<StepTrack, double>(nameof(CurrentFraction));

    public static readonly StyledProperty<bool> IsFailedProperty =
        AvaloniaProperty.Register<StepTrack, bool>(nameof(IsFailed));

    public static readonly StyledProperty<IBrush?> AccentProperty =
        AvaloniaProperty.Register<StepTrack, IBrush?>(nameof(Accent), Brushes.DodgerBlue);

    public static readonly StyledProperty<IBrush?> TrackBrushProperty =
        AvaloniaProperty.Register<StepTrack, IBrush?>(nameof(TrackBrush), Brushes.DimGray);

    public static readonly StyledProperty<double> GapProperty =
        AvaloniaProperty.Register<StepTrack, double>(nameof(Gap), 2);

    static StepTrack()
    {
        AffectsRender<StepTrack>(WeightsProperty, CurrentIndexProperty, CurrentFractionProperty, IsFailedProperty,
            AccentProperty, TrackBrushProperty, GapProperty);
    }

    public IReadOnlyList<double>? Weights { get => GetValue(WeightsProperty); set => SetValue(WeightsProperty, value); }
    public int CurrentIndex { get => GetValue(CurrentIndexProperty); set => SetValue(CurrentIndexProperty, value); }
    public double CurrentFraction { get => GetValue(CurrentFractionProperty); set => SetValue(CurrentFractionProperty, value); }
    public bool IsFailed { get => GetValue(IsFailedProperty); set => SetValue(IsFailedProperty, value); }
    public IBrush? Accent { get => GetValue(AccentProperty); set => SetValue(AccentProperty, value); }
    public IBrush? TrackBrush { get => GetValue(TrackBrushProperty); set => SetValue(TrackBrushProperty, value); }
    public double Gap { get => GetValue(GapProperty); set => SetValue(GapProperty, value); }

    public override void Render(DrawingContext context)
    {
        var weights = Weights;
        if (weights is null || weights.Count == 0) return;

        double total = weights.Sum();
        double height = Bounds.Height;
        double available = Bounds.Width - Gap * (weights.Count - 1);
        if (total <= 0 || available <= 0) return;

        double x = 0;
        for (int i = 0; i < weights.Count; i++)
        {
            double w = available * weights[i] / total;
            var rect = new Rect(x, 0, w, height);
            context.DrawRectangle(TrackBrush, null, rect, 2, 2);

            if (i < CurrentIndex)
            {
                using (context.PushOpacity(0.65))
                    context.DrawRectangle(Accent, null, rect, 2, 2);
            }
            else if (i == CurrentIndex)
            {
                double fill = IsFailed ? w : w * Math.Clamp(CurrentFraction, 0, 1);
                if (fill > 0) context.DrawRectangle(Accent, null, new Rect(x, 0, fill, height), 2, 2);
            }

            x += w + Gap;
        }
    }
}
