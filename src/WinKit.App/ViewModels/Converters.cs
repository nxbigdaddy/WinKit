using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace WinKit.App.ViewModels
{
    /// <summary>
    /// &#x9875;&#x9762;&#x5171;&#x7528;&#x7684;&#x5C0F;&#x578B;&#x503C;&#x8F6C;&#x6362;&#x5668;&#x96C6;&#x5408;&#x3002;
    /// &#x5355;&#x72EC;&#x6210;&#x6587;&#x4EF6;&#xFF1A;&#x8BBE;&#x7F6E;&#x9875;&#x4E0E;&#x64A4;&#x9500;&#x4E2D;&#x5FC3;&#x5171;&#x7528;&#xFF0C
    /// &#x907F;&#x514D;&#x8BA9;&#x8BBE;&#x7F6E;&#x9875;&#x53BB;&#x4F9D;&#x8D56;&#x67D0;&#x4E2A; ViewModel &#x6587;&#x4EF6;&#x3002;
    /// &#x7C7B;&#x540D;&#x4E0E;&#x547D;&#x540D;&#x7A7A;&#x95F4;&#x4FDD;&#x6301; WinKit.App.ViewModels &#x4E0D;&#x53D8;&#x3002;
    /// </summary>
    public sealed class BoolToVisibility : IValueConverter
    {
        /// <summary>true &#x65F6;&#x8FD4;&#x56DE; Collapsed &#x800C;&#x975E; Visible&#xFF0C;&#x7528;&#x4E8E;&#x201C;&#x5217;&#x8868;&#x4E3A;&#x7A7A;&#x624D;&#x663E;&#x793A;&#x63D0;&#x793A;&#x201D;&#x8FD9;&#x7C7B;&#x53CD;&#x5411;&#x573A;&#x666F;&#x3002;</summary>
        public bool Invert { get; set; }

        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            var visible = value is bool flag && flag;
            if (Invert) visible = !visible;
            return visible ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            var visible = value is Visibility v && v == Visibility.Visible;
            return Invert ? !visible : visible;
        }
    }

    /// <summary>&#x8BED;&#x4E49;&#x5316;&#x5199;&#x6CD5;&#xFF1A;bool &#x4E3A; false &#x65F6;&#x53EF;&#x89C1;&#x3002;</summary>
    public sealed class BoolToVisibilityInverted : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            var visible = !(value is bool flag && flag);
            return visible ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            return !(value is Visibility v && v == Visibility.Visible);
        }
    }

    /// <summary>
    /// &#x5B57;&#x7B26;&#x4E32;&#x76F8;&#x7B49; &#x2192; bool&#x3002;&#x7528;&#x4E8E;&#x628A;&#x4E00;&#x7EC4; RadioButton &#x7ED1;&#x5230;&#x540C;&#x4E00;&#x4E2A;&#x5B57;&#x7B26;&#x4E32;&#x5C5E;&#x6027;
    /// &#xFF08;&#x671F;&#x671B;&#x503C;&#x7531; ConverterParameter &#x7ED9;&#x51FA;&#xFF09;&#x3002;
    /// ConvertBack &#x8FD4;&#x56DE; DoNothing&#xFF1A;&#x591A;&#x4E2A;&#x63A7;&#x4EF6;&#x5171;&#x7528;&#x4E00;&#x4EFD;&#x72B6;&#x6001;&#xFF0C;&#x53CD;&#x5411;&#x8D4B;&#x503C;&#x53EA;&#x4F1A;&#x4E92;&#x76F8;&#x8986;&#x76D6;&#x3002;
    /// </summary>
    public sealed class StringMatch : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            return string.Equals(value as string, parameter as string, StringComparison.OrdinalIgnoreCase);
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            return Binding.DoNothing;
        }
    }
}
