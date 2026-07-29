using Shenora.WinForms;

namespace Shenora.Tests.WinForms;

public class DpiHelperTests
{
    [Theory]
    [InlineData(96, 1.0)]
    [InlineData(120, 1.25)]
    [InlineData(144, 1.5)]
    [InlineData(192, 2.0)]
    [InlineData(0, 1.0)]   // unknown → identity, never divide-by-zero
    [InlineData(-5, 1.0)]
    public void ScaleFromDeviceDpi_maps_known_dpis(int deviceDpi, double expected)
    {
        Assert.Equal(expected, DpiHelper.ScaleFromDeviceDpi(deviceDpi));
    }

    [Theory]
    [InlineData(100, 1.0, 100)]
    [InlineData(100, 1.5, 150)]
    [InlineData(101, 1.5, 152)] // 151.5 rounds to even → 152
    [InlineData(100, 2.0, 200)]
    public void Scale_rounds_logical_times_scale(int logical, double scale, int expected)
    {
        Assert.Equal(expected, DpiHelper.Scale(logical, scale));
    }

    [Fact]
    public void SystemScale_returns_a_positive_scale()
    {
        Assert.True(DpiHelper.SystemScale() > 0);
    }
}
