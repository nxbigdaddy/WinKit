using WinKit.Core.Text;
using Xunit;

namespace WinKit.Core.Tests
{
    public class ByteSizeTests
    {
        [Theory]
        [InlineData(0L, "0 B")]
        [InlineData(1L, "1 B")]
        [InlineData(512L, "512 B")]
        [InlineData(1023L, "1023 B")]
        [InlineData(1024L, "1.0 KB")]
        [InlineData(1536L, "1.5 KB")]
        [InlineData(1048576L, "1.0 MB")]
        [InlineData(1572864L, "1.5 MB")]
        [InlineData(1073741824L, "1.0 GB")]
        [InlineData(1099511627776L, "1.0 TB")]
        public void Format_uses_binary_units(long bytes, string expected)
        {
            Assert.Equal(expected, ByteSize.Format(bytes));
        }

        [Fact]
        public void Format_keeps_sign_for_negative_values()
        {
            Assert.Equal("-1.5 KB", ByteSize.Format(-1536));
        }

        [Fact]
        public void Format_respects_decimal_places()
        {
            Assert.Equal("1.50 KB", ByteSize.Format(1536, 2));
        }

        [Fact]
        public void FormatRate_appends_per_second()
        {
            Assert.Equal("2.0 KB/s", ByteSize.FormatRate(2048));
        }

        [Fact]
        public void FormatRate_handles_nan_defensively()
        {
            Assert.Equal("—", ByteSize.FormatRate(double.NaN));
        }
    }
}
