using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace Taskflare.Views;

/// <summary>true → Visible. ConverterParameter "not" flips it.</summary>
public sealed class BoolVis : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool b = value is bool v && v;
        if (parameter as string == "not") b = !b;
        return b ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>"#RRGGBB" → brush. ConverterParameter "soft" gives a see-through version.</summary>
public sealed class HexBrush : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        try
        {
            var c = (Color)ColorConverter.ConvertFromString(value as string ?? "#3B82F6");
            if (parameter as string == "soft") c = Color.FromArgb(0x33, c.R, c.G, c.B);
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }
        catch
        {
            return Brushes.SteelBlue;
        }
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>A heading row in the task list ("Overdue", "Today", "+ 2 completed tasks").</summary>
public sealed class RowHeader : Taskflare.Models.Notify
{
    private bool _expanded = true;
    private string _text = "";
    private int _count;

    public string Key { get; init; } = "";
    public bool IsCompleted { get; init; }
    public string Text { get => _text; set => Set(ref _text, value); }
    public int Count { get => _count; set { if (Set(ref _count, value)) Raise(nameof(CountText)); } }
    public string CountText => _count.ToString();
    public bool Expanded { get => _expanded; set { if (Set(ref _expanded, value)) Raise(nameof(Chevron)); } }
    public string Chevron => _expanded ? "" : "";
}
