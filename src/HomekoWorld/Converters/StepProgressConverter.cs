using System.Globalization;
using System.Windows.Data;

namespace HomekoWorld.Converters;

// Returns the pixel width of the progress fill bar.
// values[0] = StepIndex (int, 1-based), values[1] = TotalSteps (int), values[2] = track ActualWidth (double)
public class StepProgressConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 3) return 0.0;
        if (values[0] is not int step || values[1] is not int total || total <= 0) return 0.0;
        if (values[2] is not double trackWidth || trackWidth <= 0) return 0.0;
        var ratio = Math.Clamp((double)step / total, 0.0, 1.0);
        return ratio * trackWidth;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
