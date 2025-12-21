using System.Globalization;
using System.Windows.Data;

namespace SteamRoll.Controls;

public class BooleanToHeartConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool isFavorite && isFavorite)
        {
            return "❤️";
        }
        return ""; // Empty string for not favorite to keep list clean
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
