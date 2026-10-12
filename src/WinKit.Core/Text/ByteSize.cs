using System;
using System.Globalization;

namespace WinKit.Core.Text
{
    /// <summary>字节量/速率的统一格式化，避免界面各处出现不同的写法。</summary>
    public static class ByteSize
    {
        private static readonly string[] Units = { "B", "KB", "MB", "GB", "TB", "PB" };

        public static string Format(long bytes, int decimals = 1)
        {
            bool negative = bytes < 0;
            double value = Math.Abs((double)bytes);
            int unit = 0;
            while (value >= 1024d && unit < Units.Length - 1)
            {
                value /= 1024d;
                unit++;
            }

            string text = unit == 0
                ? ((long)value).ToString(CultureInfo.InvariantCulture) + " B"
                : value.ToString("F" + decimals.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture)
                  + " " + Units[unit];

            return negative ? "-" + text : text;
        }

        public static string FormatRate(double bytesPerSecond)
        {
            if (double.IsNaN(bytesPerSecond) || double.IsInfinity(bytesPerSecond)) return "—";
            return Format((long)Math.Round(bytesPerSecond)) + "/s";
        }

        public static string FormatDelta(long bytes)
        {
            return (bytes >= 0 ? "+" : "-") + Format(Math.Abs(bytes));
        }
    }
}
