using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Taskflare.Models;
using Taskflare.Services;

namespace Taskflare.Views;

/// <summary>
/// A themed pop-up for picking a date (and optionally a time): quick choices (Today, Tomorrow, Next week),
/// a month calendar, a time box and Remove. Used for due dates and reminders.
/// </summary>
public static class DatePopup
{
    public static Popup Open(UIElement target, DateTime? current, bool withTime, TimeSpan? currentTime, Action<DateTime?, TimeSpan?> onPick, string title = "Due date")
    {
        var popup = new Popup
        {
            PlacementTarget = target,
            Placement = PlacementMode.Bottom,
            StaysOpen = false,
            AllowsTransparency = true,
            PopupAnimation = PopupAnimation.Fade,
        };
        var today = DateTime.Today;
        DateTime? selected = current?.Date;
        var month = new DateTime((selected ?? today).Year, (selected ?? today).Month, 1);

        var root = new Border { CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), Padding = new Thickness(10), Margin = new Thickness(0, 4, 12, 12), Width = 290 };
        root.SetResourceReference(Border.BackgroundProperty, "MenuBg");
        root.SetResourceReference(Border.BorderBrushProperty, "MenuBorder");
        root.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 16, ShadowDepth = 3, Opacity = 0.25 };
        var stack = new StackPanel();
        root.Child = stack;
        TextElement.SetFontSize(stack, 13.5);
        stack.SetResourceReference(TextElement.ForegroundProperty, "Text");

        var heading = new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, Margin = new Thickness(4, 0, 0, 6) };
        stack.Children.Add(heading);

        TextBox timeBox = null;
        void Pick(DateTime? d)
        {
            TimeSpan? t = null;
            if (withTime && d != null)
            {
                t = QuickAddParser.ParseTime(timeBox?.Text, DateTime.Now) ?? currentTime ?? new TimeSpan(9, 0, 0);
            }
            popup.IsOpen = false;
            onPick(d, t);
        }

        Button Quick(string glyph, string text, string hint, DateTime d)
        {
            var b = new Button { Tag = glyph, Style = (Style)Application.Current.FindResource("DetailRow"), Height = 36, Background = Brushes.Transparent };
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var t1 = new TextBlock { Text = text };
            t1.SetResourceReference(TextBlock.ForegroundProperty, "Text");
            var t2 = new TextBlock { Text = hint, FontSize = 12 };
            t2.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
            Grid.SetColumn(t2, 1);
            g.Children.Add(t1);
            g.Children.Add(t2);
            b.Content = g;
            b.Click += (_, _) => Pick(d);
            return b;
        }

        int toMon = ((int)DayOfWeek.Monday - (int)today.DayOfWeek + 7) % 7;
        if (toMon == 0) toMon = 7;
        var nextMon = today.AddDays(toMon);
        stack.Children.Add(Quick("", "Today", today.ToString("ddd"), today));
        stack.Children.Add(Quick("", "Tomorrow", today.AddDays(1).ToString("ddd"), today.AddDays(1)));
        stack.Children.Add(Quick("", "Next week", nextMon.ToString("ddd, MMM d"), nextMon));

        var sep = new Border { Height = 1, Margin = new Thickness(0, 6, 0, 6) };
        sep.SetResourceReference(Border.BackgroundProperty, "Divider");
        stack.Children.Add(sep);

        // ---- month calendar ----
        var head = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition());
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var prev = IconButton("");
        var next = IconButton("");
        var monthText = new TextBlock { FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(monthText, 1);
        Grid.SetColumn(next, 2);
        head.Children.Add(prev);
        head.Children.Add(monthText);
        head.Children.Add(next);
        stack.Children.Add(head);

        var days = new UniformGrid { Columns = 7 };
        stack.Children.Add(days);

        void Render()
        {
            monthText.Text = month.ToString("MMMM yyyy", CultureInfo.CurrentCulture);
            days.Children.Clear();
            foreach (var n in new[] { "Su", "Mo", "Tu", "We", "Th", "Fr", "Sa" })
            {
                var h = new TextBlock { Text = n, FontSize = 11.5, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2, 0, 4) };
                h.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
                days.Children.Add(h);
            }
            var start = month.AddDays(-(int)month.DayOfWeek);
            for (int i = 0; i < 42; i++)
            {
                var d = start.AddDays(i);
                var b = new Button
                {
                    Content = d.Day.ToString(),
                    Width = 34,
                    Height = 32,
                    Padding = new Thickness(0),
                    Margin = new Thickness(1),
                    Style = (Style)Application.Current.FindResource("FlatBase"),
                    FontSize = 13,
                };
                if (d.Month != month.Month) b.SetResourceReference(Control.ForegroundProperty, "TextDim");
                else b.SetResourceReference(Control.ForegroundProperty, "Text");
                if (selected == d)
                {
                    b.SetResourceReference(Control.BackgroundProperty, "Accent");
                    b.SetResourceReference(Control.ForegroundProperty, "AccentText");
                    b.FontWeight = FontWeights.SemiBold;
                }
                else if (d == today)
                {
                    b.SetResourceReference(Control.BorderBrushProperty, "Accent");
                    b.FontWeight = FontWeights.SemiBold;
                }
                var day = d;
                b.Click += (_, _) =>
                {
                    if (withTime)
                    {
                        selected = day;
                        if (day.Month != month.Month) month = new DateTime(day.Year, day.Month, 1);
                        Render();
                    }
                    else Pick(day);
                };
                days.Children.Add(b);
            }
        }
        prev.Click += (_, _) => { month = month.AddMonths(-1); Render(); };
        next.Click += (_, _) => { month = month.AddMonths(1); Render(); };
        Render();

        // ---- time + buttons ----
        var bottom = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        bottom.ColumnDefinitions.Add(new ColumnDefinition());
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        if (withTime)
        {
            timeBox = new TextBox
            {
                Style = (Style)Application.Current.FindResource("FieldBox"),
                Tag = "Time, e.g. 9am or 14:30",
                Text = currentTime.HasValue ? Dates.Time(currentTime.Value) : "9:00 AM",
                Margin = new Thickness(0, 0, 8, 0),
            };
            timeBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) Pick(selected ?? today); };
            bottom.Children.Add(timeBox);
            var ok = new Button { Content = "Save", Style = (Style)Application.Current.FindResource("AccentButton"), MinWidth = 70 };
            ok.Click += (_, _) => Pick(selected ?? today);
            Grid.SetColumn(ok, 1);
            bottom.Children.Add(ok);
            stack.Children.Add(bottom);
        }
        if (current != null)
        {
            var remove = new Button { Content = "Remove", Style = (Style)Application.Current.FindResource("LinkButton"), Margin = new Thickness(0, 6, 0, 0) };
            remove.SetResourceReference(Control.ForegroundProperty, "Danger");
            remove.Click += (_, _) => { popup.IsOpen = false; onPick(null, null); };
            stack.Children.Add(remove);
        }

        popup.Child = root;
        popup.IsOpen = true;
        return popup;
    }

    private static Button IconButton(string glyph)
    {
        var b = new Button { Content = glyph, Style = (Style)Application.Current.FindResource("IconButton"), Width = 30, Height = 30, FontSize = 12 };
        b.SetResourceReference(Control.ForegroundProperty, "Text");
        return b;
    }
}
