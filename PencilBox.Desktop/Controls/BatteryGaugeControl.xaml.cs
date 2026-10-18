// <copyright file="BatteryGaugeControl.xaml.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace PencilBox.Desktop.Controls;

/// <summary>
/// 电池圆环仪表盘控件
/// 独立可替换的 UI 边界：后续美化只需改此控件视觉，不影响 ViewModel/View。
/// </summary>
public partial class BatteryGaugeControl : UserControl
{
    public static readonly DependencyProperty ValueProperty =
        DependencyProperty.Register(nameof(Value), typeof(double), typeof(BatteryGaugeControl),
            new PropertyMetadata(0.0, OnVisualPropertyChanged));

    public static readonly DependencyProperty MaxValueProperty =
        DependencyProperty.Register(nameof(MaxValue), typeof(double), typeof(BatteryGaugeControl),
            new PropertyMetadata(100.0, OnVisualPropertyChanged));

    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(nameof(Title), typeof(string), typeof(BatteryGaugeControl),
            new PropertyMetadata("Battery"));

    /// <summary>环中心充电状态文字（"充电中"/"已充满"）；空串=未充电，不占位（% 数字保持可见）</summary>
    public static readonly DependencyProperty ChargeTextProperty =
        DependencyProperty.Register(nameof(ChargeText), typeof(string), typeof(BatteryGaugeControl),
            new PropertyMetadata(""));

    /// <summary>充电状态文字颜色（充电中橙 / 已充满绿，由消费方按语义给）</summary>
    public static readonly DependencyProperty ChargeBrushProperty =
        DependencyProperty.Register(nameof(ChargeBrush), typeof(Brush), typeof(BatteryGaugeControl),
            new PropertyMetadata(new SolidColorBrush(Color.FromRgb(0xE6, 0x7E, 0x22))));

    /// <summary>% 数值颜色：充电中灰显=读数虚高仅供参考（数值保持可见），其余深灰正常</summary>
    public static readonly DependencyProperty ValueBrushProperty =
        DependencyProperty.Register(nameof(ValueBrush), typeof(Brush), typeof(BatteryGaugeControl),
            new PropertyMetadata(new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33))));

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public double MaxValue
    {
        get => (double)GetValue(MaxValueProperty);
        set => SetValue(MaxValueProperty, value);
    }

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string ChargeText
    {
        get => (string)GetValue(ChargeTextProperty);
        set => SetValue(ChargeTextProperty, value);
    }

    public Brush ChargeBrush
    {
        get => (Brush)GetValue(ChargeBrushProperty);
        set => SetValue(ChargeBrushProperty, value);
    }

    public Brush ValueBrush
    {
        get => (Brush)GetValue(ValueBrushProperty);
        set => SetValue(ValueBrushProperty, value);
    }

    public BatteryGaugeControl()
    {
        InitializeComponent();
    }

    private static void OnVisualPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is BatteryGaugeControl control)
            control.UpdateGauge();
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        UpdateGauge();
    }

    private void UpdateGauge()
    {
        if (GaugeCanvas == null)
            return;

        double size = Math.Min(GaugeCanvas.ActualWidth, GaugeCanvas.ActualHeight);
        if (size <= 0)
            return;

        double stroke = size * 0.10;
        double radius = (size - stroke) / 2;
        var center = new Point(size / 2, size / 2);

        // 背景完整圆环
        BackgroundRing.Width = size;
        BackgroundRing.Height = size;
        BackgroundRing.StrokeThickness = stroke;

        // 前景圆弧（从顶部顺时针扫过 value 比例）
        double ratio = MaxValue > 0 ? Math.Clamp(Value / MaxValue, 0.0, 1.0) : 0.0;
        ForegroundArc.Width = size;
        ForegroundArc.Height = size;
        ForegroundArc.StrokeThickness = stroke;
        ForegroundArc.Data = BuildArc(center, radius, ratio);

        // 中心文字
        BatteryText.Text = $"{Value:F0}%";
    }

    private static Geometry BuildArc(Point center, double radius, double ratio)
    {
        // 满环：起点=终点的 ArcSegment 是退化弧（WPF 渲染为空，表现为"100% 显示 0%"），
        // 满值（含浮点舍入逼近满值）直接画整圆；0 仍画空弧。
        if (ratio >= 0.999)
            return new EllipseGeometry(center, radius, radius);

        if (ratio <= 0)
        {
            // 空弧，返回一个点
            return new EllipseGeometry(center, 0, 0);
        }

        // 从 12 点方向开始，顺时针扫过 ratio * 360 度
        double startAngle = -90.0; // 顶部
        double sweepAngle = ratio * 360.0;

        var startPoint = PointOnCircle(center, radius, startAngle);
        var endPoint = PointOnCircle(center, radius, startAngle + sweepAngle);

        var figure = new PathFigure { StartPoint = startPoint, IsClosed = false };
        var arc = new ArcSegment
        {
            Point = endPoint,
            Size = new Size(radius, radius),
            SweepDirection = SweepDirection.Clockwise,
            IsLargeArc = sweepAngle > 180.0
        };
        figure.Segments.Add(arc);

        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        return geometry;
    }

    private static Point PointOnCircle(Point center, double radius, double angleDegrees)
    {
        double rad = angleDegrees * Math.PI / 180.0;
        return new Point(
            center.X + radius * Math.Sin(rad),
            center.Y - radius * Math.Cos(rad));
    }
}
