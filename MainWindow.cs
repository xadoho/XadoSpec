using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace XadoSpec
{
    sealed class MainWindow : Window
    {
        static Brush B(string hex)
        {
            var b = (SolidColorBrush)new BrushConverter().ConvertFromString(hex);
            b.Freeze();
            return b;
        }

        static Brush Tint(string hex, byte alpha)
        {
            var c = (Color)ColorConverter.ConvertFromString(hex);
            var b = new SolidColorBrush(Color.FromArgb(alpha, c.R, c.G, c.B));
            b.Freeze();
            return b;
        }

        static readonly Brush BgB = B("#0D0F13"), CardB = B("#14171E"), LineB = B("#222632"),
            TextB = B("#E8EBF1"), MutedB = B("#8C94A8"), FaintB = B("#5E6577");
        static readonly FontFamily IconFont = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");

        readonly string shot;
        readonly Grid head = new Grid { Margin = new Thickness(36, 26, 36, 12) };
        readonly StackPanel buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        readonly Grid cols = new Grid();
        readonly StackPanel left = new StackPanel(), right = new StackPanel();
        readonly UniformGrid tiles = new UniformGrid { Columns = 4 };
        readonly TextBlock title = new TextBlock(), subtitle = new TextBlock(), status = new TextBlock();
        readonly Button refreshBtn, copyBtn;
        readonly TextBlock copyText = new TextBlock { Text = "Копировать отчёт" };
        readonly ScrollViewer scroll;
        readonly List<(FrameworkElement Card, int Weight)> cards = new List<(FrameworkElement, int)>();
        Report report;
        bool? narrow;

        public MainWindow(string shotPath, double shotW, double shotH)
        {
            shot = shotPath;
            Title = "XadoSpec — характеристики ПК";
            Width = 1180;
            Height = 820;
            MinWidth = 540;
            MinHeight = 480;
            Background = BgB;
            Foreground = TextB;
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
            UseLayoutRounding = true;
            SnapsToDevicePixels = true;
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            if (shot != null)
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = -32000;
                Top = -32000;
                ShowActivated = false;
                ShowInTaskbar = false;
                Width = shotW;
                Height = shotH;
            }

            refreshBtn = MakeButton("", new TextBlock { Text = "Обновить" }, "Btn");
            copyBtn = MakeButton("", copyText, "BtnAccent");
            refreshBtn.Click += async (s, e) => await Reload();
            copyBtn.Click += (s, e) => CopyReport();
            refreshBtn.ToolTip = "Собрать данные заново (F5)";
            copyBtn.ToolTip = "Скопировать все характеристики текстом";

            var root = new Grid { Background = BgB };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition());

            head.ColumnDefinitions.Add(new ColumnDefinition());
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            head.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var hl = new StackPanel();
            hl.Children.Add(new TextBlock { Text = "XADOSPEC · ХАРАКТЕРИСТИКИ ПК", Foreground = B("#7C9CFF"), FontSize = 11.5, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 6) });
            title.Text = "Собираю данные…";
            title.FontSize = 28;
            title.FontWeight = FontWeights.SemiBold;
            title.TextTrimming = TextTrimming.CharacterEllipsis;
            subtitle.Foreground = MutedB;
            subtitle.FontSize = 13.5;
            subtitle.Margin = new Thickness(0, 4, 0, 0);
            subtitle.TextWrapping = TextWrapping.Wrap;
            hl.Children.Add(title);
            hl.Children.Add(subtitle);
            head.Children.Add(hl);
            buttons.Children.Add(refreshBtn);
            copyBtn.Margin = new Thickness(10, 0, 0, 0);
            buttons.Children.Add(copyBtn);
            head.Children.Add(buttons);
            root.Children.Add(head);

            var body = new StackPanel { Margin = new Thickness(28, 4, 28, 28) };
            body.Children.Add(tiles);
            status.Foreground = FaintB;
            status.FontSize = 12;
            status.Margin = new Thickness(10, 6, 10, 4);
            body.Children.Add(status);
            cols.ColumnDefinitions.Add(new ColumnDefinition());
            cols.ColumnDefinitions.Add(new ColumnDefinition());
            Grid.SetColumn(right, 1);
            cols.Children.Add(left);
            cols.Children.Add(right);
            body.Children.Add(cols);

            scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Focusable = false,
                Content = body
            };
            scroll.PreviewMouseWheel += (s, e) =>
            {
                scroll.ScrollToVerticalOffset(scroll.VerticalOffset - e.Delta * 0.6);
                e.Handled = true;
            };
            Grid.SetRow(scroll, 1);
            root.Children.Add(scroll);
            Content = root;

            SourceInitialized += (s, e) => DarkChrome();
            Loaded += async (s, e) => await Reload();
            SizeChanged += (s, e) => Relayout();
            KeyDown += async (s, e) => { if (e.Key == Key.F5) await Reload(); };
        }

        Button MakeButton(string glyph, TextBlock text, string style)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(new TextBlock { Text = glyph, FontFamily = IconFont, FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 1, 8, 0) });
            text.VerticalAlignment = VerticalAlignment.Center;
            sp.Children.Add(text);
            return new Button { Content = sp, Style = (Style)Application.Current.Resources[style] };
        }

        void DarkChrome()
        {
            var h = new WindowInteropHelper(this).Handle;
            int on = 1;
            if (Native.DwmSetWindowAttribute(h, 20, ref on, 4) != 0) Native.DwmSetWindowAttribute(h, 19, ref on, 4);
            int caption = 0x00130F0D;
            Native.DwmSetWindowAttribute(h, 35, ref caption, 4);
        }

        async Task Reload()
        {
            if (!refreshBtn.IsEnabled) return;
            refreshBtn.IsEnabled = copyBtn.IsEnabled = false;
            status.Text = "Собираю данные…";
            try
            {
                report = await Task.Run(() => Collector.Collect());
                Render(report);
            }
            finally
            {
                refreshBtn.IsEnabled = copyBtn.IsEnabled = true;
            }
            if (shot != null) _ = Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(SaveShot));
        }

        void Render(Report r)
        {
            title.Text = r.Title ?? Environment.MachineName;
            subtitle.Text = r.Subtitle ?? "";
            tiles.Children.Clear();
            foreach (var t in new[] { r.Cpu, r.Ram, r.Gpu, r.Disk })
                if (t != null) tiles.Children.Add(MakeTile(t));
            status.Text = "Обновлено в " + r.Time.ToString("HH:mm:ss") + " · сбор занял " + r.Took.TotalSeconds.ToString("0.0") + " с · значения можно выделять и копировать";
            cards.Clear();
            foreach (var s in r.Sections)
                cards.Add((MakeCard(s), s.Lines.Count + s.Lines.Count(l => l.Header) + 4));
            narrow = null;
            Relayout();
        }

        void Relayout()
        {
            double w = ActualWidth > 0 ? ActualWidth : Width;
            tiles.Columns = w < 820 ? 2 : 4;

            bool compactHead = w < 760;
            Grid.SetColumn(buttons, compactHead ? 0 : 1);
            Grid.SetRow(buttons, compactHead ? 1 : 0);
            buttons.HorizontalAlignment = compactHead ? HorizontalAlignment.Left : HorizontalAlignment.Right;
            buttons.Margin = compactHead ? new Thickness(0, 14, 0, 0) : new Thickness(20, 0, 0, 0);

            bool n = w < 1000;
            if (narrow == n) return;
            narrow = n;
            left.Children.Clear();
            right.Children.Clear();
            cols.ColumnDefinitions[1].Width = n ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
            int a = 0, b = 0;
            foreach (var c in cards)
            {
                if (n || a <= b) { left.Children.Add(c.Card); a += c.Weight; }
                else { right.Children.Add(c.Card); b += c.Weight; }
            }
        }

        FrameworkElement MakeTile(Tile t)
        {
            var border = new Border
            {
                Background = CardB,
                BorderBrush = LineB,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(14),
                Padding = new Thickness(18, 14, 18, 15),
                Margin = new Thickness(8)
            };
            var sp = new StackPanel();
            var cap = new StackPanel { Orientation = Orientation.Horizontal };
            cap.Children.Add(new Ellipse { Width = 7, Height = 7, Fill = B(t.Color ?? "#7C9CFF"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 1, 8, 0) });
            cap.Children.Add(new TextBlock { Text = t.Caption, Foreground = MutedB, FontSize = 12 });
            sp.Children.Add(cap);
            sp.Children.Add(new TextBlock
            {
                Text = t.Value ?? "—",
                Foreground = TextB,
                FontSize = 16,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 9, 0, 3),
                TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = t.Value
            });
            sp.Children.Add(new TextBlock { Text = string.IsNullOrEmpty(t.Sub) ? " " : t.Sub, Foreground = MutedB, FontSize = 12.5, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = t.Sub });
            border.Child = sp;
            return border;
        }

        FrameworkElement MakeCard(Section s)
        {
            var accent = B(s.Color);
            var border = new Border
            {
                Background = CardB,
                BorderBrush = LineB,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(16),
                Padding = new Thickness(22, 18, 22, 16),
                Margin = new Thickness(8)
            };
            var sp = new StackPanel();
            var hdr = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
            var badge = new Border { Width = 34, Height = 34, CornerRadius = new CornerRadius(10), Background = Tint(s.Color, 0x24) };
            badge.Child = new TextBlock { Text = s.Glyph, FontFamily = IconFont, FontSize = 16, Foreground = accent, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            hdr.Children.Add(badge);
            hdr.Children.Add(new TextBlock { Text = s.Title, FontSize = 16.5, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) });
            sp.Children.Add(hdr);

            bool first = true;
            foreach (var l in s.Lines)
            {
                if (l.Header) sp.Children.Add(MakeSub(l.Value, accent, first));
                else sp.Children.Add(MakeRow(l, accent));
                first = false;
            }
            border.Child = sp;
            return border;
        }

        FrameworkElement MakeSub(string text, Brush accent, bool first)
        {
            var sp = new StackPanel();
            if (!first) sp.Children.Add(new Border { Height = 1, Background = LineB, Margin = new Thickness(0, 10, 0, 0) });
            var row = new Grid { Margin = new Thickness(0, first ? 6 : 12, 0, 4) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.Children.Add(new Border { Width = 3, Height = 15, CornerRadius = new CornerRadius(2), Background = accent, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Left });
            var tb = new TextBlock { Text = text, FontSize = 13.5, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
            Grid.SetColumn(tb, 1);
            row.Children.Add(tb);
            sp.Children.Add(row);
            return sp;
        }

        FrameworkElement MakeRow(Line l, Brush accent)
        {
            var g = new Grid { Margin = new Thickness(0, 3, 0, 3) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(168) });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.Children.Add(new TextBlock { Text = l.Key, Foreground = MutedB, FontSize = 13, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 12, 0) });
            var v = new TextBox
            {
                Text = l.Value,
                IsReadOnly = true,
                IsReadOnlyCaretVisible = false,
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                Foreground = l.Weak ? FaintB : TextB,
                FontStyle = l.Weak ? FontStyles.Italic : FontStyles.Normal,
                FontSize = 13.5,
                TextWrapping = TextWrapping.Wrap,
                Padding = new Thickness(0),
                Margin = new Thickness(-2, 0, 0, 0),
                SelectionBrush = accent,
                CaretBrush = Brushes.Transparent,
                FocusVisualStyle = null,
                Cursor = Cursors.IBeam
            };
            Grid.SetColumn(v, 1);
            g.Children.Add(v);
            return g;
        }

        void CopyReport()
        {
            if (report == null) return;
            string text = report.ToText();
            for (int i = 0; i < 5; i++)
            {
                try { Clipboard.SetText(text); break; }
                catch { System.Threading.Thread.Sleep(60); }
            }
            copyText.Text = "Скопировано ✓";
            var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.6) };
            t.Tick += (s, e) => { copyText.Text = "Копировать отчёт"; t.Stop(); };
            t.Start();
        }

        void SaveShot()
        {
            var args = Environment.GetCommandLineArgs();
            bool full = args.Contains("--full");
            int si = Array.IndexOf(args, "--scale");
            double scale = si >= 0 && si + 1 < args.Length && double.TryParse(args[si + 1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double sc) ? sc : 1;
            var el = (FrameworkElement)Content;
            el.UpdateLayout();
            var body = (FrameworkElement)scroll.Content;
            double w = el.ActualWidth;
            double headH = head.Margin.Top + head.ActualHeight + head.Margin.Bottom;
            double h = full ? headH + body.Margin.Top + body.ActualHeight + body.Margin.Bottom : el.ActualHeight;
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.DrawRectangle(BgB, null, new Rect(0, 0, w, h));
                if (full)
                {
                    Draw(dc, head, head.Margin.Left, head.Margin.Top);
                    Draw(dc, body, body.Margin.Left, headH + body.Margin.Top);
                }
                else Draw(dc, el, 0, 0);
            }
            var rtb = new RenderTargetBitmap((int)Math.Round(w * scale), (int)Math.Round(h * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            rtb.Render(dv);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            using (var f = File.Create(shot)) enc.Save(f);
            Close();
        }

        static void Draw(DrawingContext dc, FrameworkElement e, double x, double y)
        {
            var o = VisualTreeHelper.GetOffset(e);
            var brush = new VisualBrush(e)
            {
                ViewboxUnits = BrushMappingMode.Absolute,
                Viewbox = new Rect(o.X, o.Y, e.ActualWidth, e.ActualHeight),
                Stretch = Stretch.Fill
            };
            dc.DrawRectangle(brush, null, new Rect(x, y, e.ActualWidth, e.ActualHeight));
        }
    }
}
