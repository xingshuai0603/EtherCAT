using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace EtherCAT.App.ViewModels;

/// <summary>布尔取反转换器，用于空状态提示的 IsVisible 绑定</summary>
public sealed class InverseBooleanConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is bool b ? !b : value;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is bool b ? !b : value;
}
