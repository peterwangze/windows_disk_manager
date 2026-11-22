using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Shapes;

namespace UsageTree
{
    // Resolves the shared design resources loaded from Theme.xaml.
    internal static class Design
    {
        internal static object Find(string key)
        {
            var app = Application.Current;
            return app == null ? null : app.TryFindResource(key);
        }
        internal static Brush Brush(string key)
        {
            return Find(key) as Brush ?? Brushes.Transparent;
        }
        internal static Geometry Geometry(string key)
        {
            return Find(key) as Geometry ?? System.Windows.Media.Geometry.Empty;
        }
        internal static Style Style(string key)
        {
            return Find(key) as Style;
        }
        internal static FontFamily Font(string key)
        {
            return Find(key) as FontFamily ?? new FontFamily("Segoe UI");
        }
    }

    // Small builders shared by the main window and the code-created dialogs.
    internal static class UiKit
    {
        internal static Path LineIcon(string key, double size, Brush stroke, double thickness = 1.8)
        {
            return new Path
            {
                Data = Design.Geometry(key),
                Width = size,
                Height = size,
                Stretch = Stretch.Uniform,
                Stroke = stroke,
                StrokeThickness = thickness,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round,
                VerticalAlignment = VerticalAlignment.Center,
                SnapsToDevicePixels = true
            };
        }

        internal static Path FillIcon(string key, double size, Brush fill)
        {
            return new Path
            {
                Data = Design.Geometry(key),
                Width = size,
                Height = size,
                Stretch = Stretch.Uniform,
                Fill = fill,
                VerticalAlignment = VerticalAlignment.Center,
                SnapsToDevicePixels = true
            };
        }

        internal static FrameworkElement ButtonContent(string label, string iconKey)
        {
            var stack = new StackPanel { Orientation = Orientation.Horizontal };
            if (!String.IsNullOrEmpty(iconKey))
            {
                var icon = LineIcon(iconKey, 14, Brushes.Transparent, 1.9);
                icon.Margin = new Thickness(0, 0, 7, 0);
                icon.SetBinding(Shape.StrokeProperty, new Binding("Foreground")
                {
                    RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(Button), 1)
                });
                stack.Children.Add(icon);
            }
            stack.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
            return stack;
        }

        internal static Button Button(string label, string iconKey, string styleKey, string automationId)
        {
            var button = new Button
            {
                Style = Design.Style(styleKey),
                Content = ButtonContent(label, iconKey),
                Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            if (!String.IsNullOrEmpty(automationId)) AutomationProperties.SetAutomationId(button, automationId);
            return button;
        }

        internal static TextBlock Text(string text, string styleKey)
        {
            var block = new TextBlock { Text = text };
            var style = Design.Style(styleKey);
            if (style != null) block.Style = style;
            return block;
        }

        internal static Border IconTile(string iconKey, double size, Brush background, Brush iconBrush, bool stroked = true)
        {
            var icon = stroked ? LineIcon(iconKey, size * 0.5, iconBrush, 1.9) : FillIcon(iconKey, size * 0.5, iconBrush);
            return new Border
            {
                Width = size,
                Height = size,
                CornerRadius = new CornerRadius(size < 38 ? 10 : 12),
                Background = background,
                Child = icon,
                VerticalAlignment = VerticalAlignment.Top
            };
        }

        internal static Border Surface(UIElement child, Thickness padding)
        {
            return new Border
            {
                Style = Design.Style("SurfaceCard"),
                Padding = padding,
                Child = child
            };
        }

        internal static Geometry RingArc(double center, double radius, double fraction)
        {
            if (fraction <= 0) return System.Windows.Media.Geometry.Empty;
            if (fraction > 0.9998) fraction = 0.9998;
            double sweep = fraction * 360.0;
            Point start = OnCircle(center, radius, -90);
            Point end = OnCircle(center, radius, -90 + sweep);
            var figure = new PathFigure { StartPoint = start, IsClosed = false, IsFilled = false };
            figure.Segments.Add(new ArcSegment(end, new Size(radius, radius), 0, sweep > 180, SweepDirection.Clockwise, true));
            var geometry = new PathGeometry();
            geometry.Figures.Add(figure);
            geometry.Freeze();
            return geometry;
        }

        private static Point OnCircle(double center, double radius, double degrees)
        {
            double angle = degrees * Math.PI / 180.0;
            return new Point(center + radius * Math.Cos(angle), center + radius * Math.Sin(angle));
        }

        internal static StackPanel Horizontal(params UIElement[] children)
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var child in children) panel.Children.Add(child);
            return panel;
        }
    }
}
