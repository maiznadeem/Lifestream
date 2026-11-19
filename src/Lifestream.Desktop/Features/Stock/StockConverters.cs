using System.Globalization;
using System.Windows.Data;
using Lifestream.Client.Common;
using Lifestream.Domain;

namespace Lifestream.Desktop.Features.Stock;

/// <summary>A blood group choice as set in type: "O−" with a real minus sign, or the option's own words ("All blood types").</summary>
public sealed class GroupOptionTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        FilterOption<BloodType> { Value: { } type } => type.ToTypographic(),
        FilterOption<BloodType> option => option.Display,
        _ => string.Empty,
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
