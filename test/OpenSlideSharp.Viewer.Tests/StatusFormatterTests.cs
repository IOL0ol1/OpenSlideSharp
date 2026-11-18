using System.Collections.Generic;
using System.Globalization;
using Xunit;

namespace OpenSlideSharp.Viewer.Tests;

public class StatusFormatterTests
{
    [Fact]
    public void Coordinates_FloorsLevel0Pixels()
    {
        Assert.Equal("", StatusFormatter.Coordinates(null, null));
        Assert.Equal("", StatusFormatter.Coordinates(12, null));
        Assert.Equal("", StatusFormatter.Coordinates(null, 34));
        Assert.Equal("x: 1234  y: 567", StatusFormatter.Coordinates(1234.6, 567.2));
        Assert.Equal("x: 0  y: 0", StatusFormatter.Coordinates(0.99, 0));
    }

    [Fact]
    public void Zoom_ShowsDownsampleAndMagnification()
    {
        Assert.Equal("1:4.02 (9.96×)", StatusFormatter.Zoom(4.016129, 40));
        Assert.Equal("1:1.00", StatusFormatter.Zoom(1, null));
        Assert.Equal("1:0.25 (80.00×)", StatusFormatter.Zoom(0.25, 20));

        // An objective power that is not a positive number is left out.
        Assert.Equal("1:2.00", StatusFormatter.Zoom(2, 0));
        Assert.Equal("1:2.00", StatusFormatter.Zoom(2, double.NaN));
    }

    [Fact]
    public void PhysicalSize_PicksMicrometresOrMillimetres()
    {
        Assert.Equal("812.5 × 600.0 µm", StatusFormatter.PhysicalSize(1625, 1200, 0.5, 0.5));
        Assert.Equal("1.25 × 0.80 mm", StatusFormatter.PhysicalSize(5000, 3200, 0.25, 0.25));
        Assert.Equal("0.50 × 1.00 mm", StatusFormatter.PhysicalSize(1000, 4000, 0.5, 0.25)); // the height reaches 1000 µm
        Assert.Equal("", StatusFormatter.PhysicalSize(10, 10, null, 0.5));
    }

    [Fact]
    public void StatusBar_NoMpp_OmitsPhysicalSize()
    {
        Assert.Equal("", StatusFormatter.PhysicalSize(800, 600, null, null));
        Assert.Equal("", StatusFormatter.PhysicalSize(800, 600, double.NaN, 0.5));
        Assert.Equal("", StatusFormatter.PhysicalSize(800, 600, 0.5, double.PositiveInfinity));
        Assert.Equal("", StatusFormatter.PhysicalSize(800, 600, 0, 0.5));
        Assert.Equal("", StatusFormatter.PhysicalSize(800, 600, -0.5, 0.5));
        Assert.Equal("", StatusFormatter.PhysicalSize(double.NaN, 600, 0.5, 0.5));
    }

    [Fact]
    public void Formatting_IgnoresTheCurrentCulture()
    {
        CultureInfo culture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE"); // a decimal comma
            Assert.Equal("1:4.02 (9.96×)", StatusFormatter.Zoom(4.016129, 40));
            Assert.Equal("812.5 × 600.0 µm", StatusFormatter.PhysicalSize(1625, 1200, 0.5, 0.5));
            Assert.Equal("x: 1234  y: 567", StatusFormatter.Coordinates(1234.6, 567.2));
            Assert.Equal(0.25, StatusFormatter.Number(new Dictionary<string, string> { ["mpp"] = "0.25" }, "mpp"));
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
        }
    }

    [Fact]
    public void Number_ReadsAPositiveProperty()
    {
        var properties = new Dictionary<string, string>
        {
            [OpenSlideProperties.MppX] = "0.2527",
            [OpenSlideProperties.MppY] = "nonsense",
            [OpenSlideProperties.ObjectivePower] = "-20",
            ["zero"] = "0",
            ["nan"] = "NaN",
        };

        Assert.Equal(0.2527, StatusFormatter.Number(properties, OpenSlideProperties.MppX));
        Assert.Null(StatusFormatter.Number(properties, OpenSlideProperties.MppY));
        Assert.Null(StatusFormatter.Number(properties, OpenSlideProperties.ObjectivePower));
        Assert.Null(StatusFormatter.Number(properties, "zero"));
        Assert.Null(StatusFormatter.Number(properties, "nan"));
        Assert.Null(StatusFormatter.Number(properties, "missing"));
    }
}
