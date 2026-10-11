using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using ShapePath = System.Windows.Shapes.Path;

namespace ZStationStudio.Controls
{
    public enum ZDialogKind { Info, Success, Warning, Danger, Question, Error }

    /// <summary>
    /// Netflix dark-styled dialog replacing the native Windows MessageBox
    /// so confirmations/notifications match the Z-Station Studio UI.
    /// </summary>
    public class ZDialog : Window
    {
        private static readonly Color BgColor = Color.FromRgb(0x1C, 0x1C, 0x1C);
        private static readonly Color BorderColor = Color.FromRgb(0x33, 0x33, 0x33);
        private static readonly Color Red = Color.FromRgb(0xE5, 0x09, 0x14);
        private static readonly Color RedHover = Color.FromRgb(0xF4, 0x06, 0x12);

        // Lucide icon paths
        private const string IconAlert = "M 21.73,18 L 13.73,4 a 2,2 0 0,0 -3.48,0 L 2.25,18 A 2,2 0 0,0 4,21 h 16 a 2,2 0 0,0 1.73,-3 Z M 12,9 v 4 M 12,17 h 0.01";
        private const string IconTrash = "M 3,6 H 21 M 19,6 V 20 A 2,2 0 0 1 17,22 H 7 A 2,2 0 0 1 5,20 V 6 M 8,6 V 4 A 2,2 0 0 1 10,2 H 14 A 2,2 0 0 1 16,4 V 6 M 10,11 V 17 M 14,11 V 17";
        private const string IconInfo = "M 2,12 a 10,10 0 1,0 20,0 a 10,10 0 1,0 -20,0 M 12,16 v -4 M 12,8 h 0.01";
        private const string IconCheck = "M 2,12 a 10,10 0 1,0 20,0 a 10,10 0 1,0 -20,0 M 9,12 l 2,2 l 4,-4";
        private const string IconHelp = "M 2,12 a 10,10 0 1,0 20,0 a 10,10 0 1,0 -20,0 M 9.09,9 a 3,3 0 0,1 5.83,1 c 0,2 -3,3 -3,3 M 12,17 h 0.01";
        private const string IconX = "M 2,12 a 10,10 0 1,0 20,0 a 10,10 0 1,0 -20,0 M 15,9 l -6,6 M 9,9 l 6,6";

        private bool _result;

        private ZDialog(Window? owner, string title, string message, ZDialogKind kind, string confirmText, string? cancelText)
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            SizeToContent = SizeToContent.WidthAndHeight;
            Title = title;

            if (owner != null && owner.IsLoaded)
            {
                Owner = owner;
                WindowStartupLocation = WindowStartupLocation.CenterOwner;
                FontFamily = owner.FontFamily;
            }
            else
            {
                WindowStartupLocation = WindowStartupLocation.CenterScreen;
                FontFamily = new FontFamily(new Uri("pack://application:,,,/"), "./Fonts/#Quicksand, Segoe UI");
            }

            (string iconData, Color accent) = kind switch
            {
                ZDialogKind.Danger => (IconTrash, Red),
                ZDialogKind.Warning => (IconAlert, Color.FromRgb(0xF5, 0xA6, 0x23)),
                ZDialogKind.Success => (IconCheck, Color.FromRgb(0x46, 0xD3, 0x69)),
                ZDialogKind.Question => (IconHelp, Color.FromRgb(0xB3, 0xB3, 0xB3)),
                ZDialogKind.Error => (IconX, Red),
                _ => (IconInfo, Color.FromRgb(0xB3, 0xB3, 0xB3))
            };

            // ---- Header ----
            var iconCircle = new Border
            {
                Width = 36,
                Height = 36,
                CornerRadius = new CornerRadius(18),
                Background = new SolidColorBrush(Color.FromArgb(0x26, accent.R, accent.G, accent.B)),
                Margin = new Thickness(0, 0, 12, 0),
                VerticalAlignment = VerticalAlignment.Top,
                Child = new ShapePath
                {
                    Data = Geometry.Parse(iconData),
                    Stroke = new SolidColorBrush(accent),
                    StrokeThickness = 2,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round,
                    StrokeLineJoin = PenLineJoin.Round,
                    Stretch = Stretch.Uniform,
                    Width = 18,
                    Height = 18,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };

            var titleBlock = new TextBlock
            {
                Text = title,
                FontSize = 16,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White,
                Margin = new Thickness(0, 0, 0, 6),
                TextWrapping = TextWrapping.Wrap
            };

            var msgBlock = new TextBlock
            {
                Text = message,
                FontSize = 13.5,
                LineHeight = 20,
                Foreground = new SolidColorBrush(Color.FromRgb(0xB3, 0xB3, 0xB3)),
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 340
            };

            var textStack = new StackPanel();
            textStack.Children.Add(titleBlock);
            textStack.Children.Add(msgBlock);

            var header = new DockPanel { Margin = new Thickness(0, 0, 0, 20) };
            DockPanel.SetDock(iconCircle, Dock.Left);
            header.Children.Add(iconCircle);
            header.Children.Add(textStack);

            // ---- Buttons ----
            var btnRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };

            if (cancelText != null)
            {
                var cancel = CreateButton(cancelText, false, kind, () => Finish(false));
                cancel.Margin = new Thickness(0, 0, 8, 0);
                btnRow.Children.Add(cancel);
            }

            var confirm = CreateButton(confirmText, true, kind, () => Finish(true));
            btnRow.Children.Add(confirm);

            var body = new StackPanel();
            body.Children.Add(header);
            body.Children.Add(btnRow);

            var card = new Border
            {
                Background = new SolidColorBrush(BgColor),
                BorderBrush = new SolidColorBrush(BorderColor),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(20, 18, 20, 18),
                Margin = new Thickness(24),
                MinWidth = 380,
                Child = body,
                Effect = new DropShadowEffect { BlurRadius = 30, ShadowDepth = 0, Opacity = 0.85, Color = Colors.Black },
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = new ScaleTransform(0.96, 0.96),
                Opacity = 0
            };
            card.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) try { DragMove(); } catch { } };

            Content = card;

            Loaded += (_, _) =>
            {
                var dur = TimeSpan.FromMilliseconds(140);
                var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
                card.BeginAnimation(OpacityProperty, new DoubleAnimation(1, dur) { EasingFunction = ease });
                var st = (ScaleTransform)card.RenderTransform;
                st.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, dur) { EasingFunction = ease });
                st.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, dur) { EasingFunction = ease });
                Activate();
            };

            PreviewKeyDown += (_, e) =>
            {
                if (e.Key == Key.Escape) { Finish(cancelText == null); e.Handled = true; }
                else if (e.Key == Key.Enter) { Finish(true); e.Handled = true; }
            };
        }

        private Border CreateButton(string text, bool primary, ZDialogKind kind, Action onClick)
        {
            bool redPrimary = primary && (kind == ZDialogKind.Danger || kind == ZDialogKind.Warning || kind == ZDialogKind.Error || kind == ZDialogKind.Question);
            bool neutralPrimary = primary && !redPrimary;

            Color bg, bgHover, border, borderHover, fg;
            if (redPrimary)
            {
                bg = Red; bgHover = RedHover; border = Red; borderHover = RedHover; fg = Colors.White;
            }
            else if (neutralPrimary)
            {
                bg = Colors.White; bgHover = Color.FromRgb(0xE0, 0xE0, 0xE0); border = Colors.White; borderHover = bgHover; fg = Color.FromRgb(0x14, 0x14, 0x14);
            }
            else
            {
                bg = Color.FromRgb(0x26, 0x26, 0x26); bgHover = Color.FromRgb(0x33, 0x33, 0x33);
                border = Color.FromRgb(0x38, 0x38, 0x38); borderHover = Color.FromRgb(0x4D, 0x4D, 0x4D);
                fg = Color.FromRgb(0xE5, 0xE5, 0xE5);
            }

            var bgBrush = new SolidColorBrush(bg);
            var borderBrush = new SolidColorBrush(border);

            var btn = new Border
            {
                Background = bgBrush,
                BorderBrush = borderBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(18, 7, 18, 7),
                MinWidth = 84,
                Cursor = Cursors.Hand,
                Child = new TextBlock
                {
                    Text = text,
                    FontSize = 13,
                    FontWeight = primary ? FontWeights.Bold : FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(fg),
                    HorizontalAlignment = HorizontalAlignment.Center
                }
            };

            btn.MouseEnter += (_, _) => { bgBrush.Color = bgHover; borderBrush.Color = borderHover; };
            btn.MouseLeave += (_, _) => { bgBrush.Color = bg; borderBrush.Color = border; };
            // Handle MouseDown here so it never bubbles to the card's DragMove (which would swallow MouseUp)
            btn.MouseLeftButtonDown += (_, e) =>
            {
                e.Handled = true;
                btn.CaptureMouse();
                bgBrush.Color = bg;
            };
            btn.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                var p = e.GetPosition(btn);
                bool inside = p.X >= 0 && p.Y >= 0 && p.X <= btn.ActualWidth && p.Y <= btn.ActualHeight;
                btn.ReleaseMouseCapture();
                if (inside) onClick();
            };
            return btn;
        }

        private void Finish(bool result)
        {
            _result = result;
            Close();
        }

        private static Window? ResolveOwner(Window? owner)
        {
            if (owner != null) return owner;
            var app = Application.Current;
            if (app == null) return null;
            foreach (Window w in app.Windows)
            {
                if (w.IsActive && w is not ZDialog) return w;
            }
            return app.MainWindow;
        }

        /// <summary>Shows a Yes/No confirmation. Returns true if confirmed.</summary>
        public static bool Confirm(string message, string title = "Xác nhận", ZDialogKind kind = ZDialogKind.Question,
                                   string confirmText = "Đồng ý", string cancelText = "Hủy", Window? owner = null)
        {
            var dlg = new ZDialog(ResolveOwner(owner), title, message, kind, confirmText, cancelText);
            dlg.ShowDialog();
            return dlg._result;
        }

        /// <summary>Shows an informational dialog with a single OK button.</summary>
        public static void Show(string message, string title = "Z-Station Studio", ZDialogKind kind = ZDialogKind.Info,
                                string okText = "OK", Window? owner = null)
        {
            var dlg = new ZDialog(ResolveOwner(owner), title, message, kind, okText, null);
            dlg.ShowDialog();
        }
    }
}
