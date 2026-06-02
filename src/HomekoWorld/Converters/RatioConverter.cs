using System.Globalization;
using System.Windows.Data;

namespace HomekoWorld.Converters;

/// <summary>
/// double değeri ConverterParameter (oran, ör. "0.6") ile çarpar.
/// Bir elemanın MaxHeight'ını ebeveynin SABİT yüksekliğinin belli bir oranına bağlamak için
/// (responsive + layout feedback loop yok). Örn: scrollable expander içeriği = sayfa × 0.6.
/// </summary>
[ValueConversion(typeof(double), typeof(double))]
public class RatioConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is double d && parameter is string p &&
            double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out double r))
            return d * r;
        return value;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}
