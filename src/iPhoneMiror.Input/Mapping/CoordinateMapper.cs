using iPhoneMiror.Core.Abstractions;

namespace iPhoneMiror.Input.Mapping;

/// <summary>
/// Maps a point in the on-screen view that shows the mirrored image (letterboxed with uniform scaling)
/// to device coordinates in points. All sizes are in their own units; the image is centred in the view.
/// </summary>
public static class CoordinateMapper
{
    /// <param name="viewX">Pointer X inside the view.</param>
    /// <param name="viewY">Pointer Y inside the view.</param>
    /// <param name="viewWidth">Width of the view the image is rendered in.</param>
    /// <param name="viewHeight">Height of the view.</param>
    /// <param name="imageWidth">Width of the frame (pixels).</param>
    /// <param name="imageHeight">Height of the frame (pixels).</param>
    /// <param name="deviceWidth">Device screen width in points for the current orientation.</param>
    /// <param name="deviceHeight">Device screen height in points for the current orientation.</param>
    /// <returns>The device point, or null when the pointer is outside the displayed image.</returns>
    public static DevicePoint? ViewToDevice(
        double viewX, double viewY,
        double viewWidth, double viewHeight,
        double imageWidth, double imageHeight,
        double deviceWidth, double deviceHeight)
    {
        if (viewWidth <= 0 || viewHeight <= 0 || imageWidth <= 0 || imageHeight <= 0 || deviceWidth <= 0 || deviceHeight <= 0)
            return null;

        var scale = Math.Min(viewWidth / imageWidth, viewHeight / imageHeight);
        var shownW = imageWidth * scale;
        var shownH = imageHeight * scale;
        var offsetX = (viewWidth - shownW) / 2;
        var offsetY = (viewHeight - shownH) / 2;

        var u = (viewX - offsetX) / shownW;
        var v = (viewY - offsetY) / shownH;
        if (u < 0 || v < 0 || u > 1 || v > 1) return null;

        // The frame may be landscape while the reported window size is still portrait (or vice-versa).
        var frameLandscape = imageWidth > imageHeight;
        var deviceLandscape = deviceWidth > deviceHeight;
        var (dw, dh) = frameLandscape == deviceLandscape ? (deviceWidth, deviceHeight) : (deviceHeight, deviceWidth);

        return new DevicePoint(Math.Round(u * dw, 1), Math.Round(v * dh, 1));
    }
}
