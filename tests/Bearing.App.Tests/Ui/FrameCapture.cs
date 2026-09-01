using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;

namespace Bearing.App.Tests.Ui;

/// <summary>
/// Real rendered pixels from a headless window — the half of UI testing that only exists because the harness
/// runs on Skia rather than the headless drawing stub (§4.5).
/// <para>
/// Use it for the questions no property can answer: whether a code-built visual actually puts marks on the
/// surface, and whether it puts <i>different</i> marks in a different state. Do not use it to assert exact
/// colours — anti-aliasing, the platform accent and font fallback all move those, and a test that pins them
/// fails for reasons nobody wants to read. Every assertion here is deliberately channel-order agnostic:
/// distinctness and difference, never a literal ARGB value.
/// </para>
/// </summary>
internal sealed class FrameCapture
{
    private readonly uint[] _pixels;

    private FrameCapture(int width, int height, uint[] pixels)
    {
        Width = width;
        Height = height;
        _pixels = pixels;
    }

    public int Width { get; }
    public int Height { get; }

    /// <summary>Render the window and read the frame back. Deterministic because the harness renders on the
    /// dispatcher thread, so the frame is on the surface by the time this returns.</summary>
    public static FrameCapture Of(Window window)
    {
        window.UpdateLayout();
        var bitmap = window.CaptureRenderedFrame()
                     ?? throw new InvalidOperationException(
                         "nothing was rendered — the window must be shown and laid out before capturing");
        using var _ = bitmap;
        using var buffer = bitmap.Lock();

        var width = buffer.Size.Width;
        var height = buffer.Size.Height;
        var pixels = new uint[width * height];
        var row = new byte[buffer.RowBytes];
        for (var y = 0; y < height; y++)
        {
            Marshal.Copy(buffer.Address + (y * buffer.RowBytes), row, 0, buffer.RowBytes);
            for (var x = 0; x < width; x++)
                pixels[(y * width) + x] = BitConverter.ToUInt32(row, x * 4);
        }
        return new FrameCapture(width, height, pixels);
    }

    /// <summary>The pixels inside a control's bounds, in the window's coordinate space. Clipped to the
    /// frame, so a control partly off screen yields what is actually visible.</summary>
    public IReadOnlyList<uint> Within(Visual control, Visual root)
    {
        var origin = control.TranslatePoint(default, root)
                     ?? throw new InvalidOperationException("the control is not connected to the root");
        var left = Math.Max(0, (int)Math.Floor(origin.X));
        var top = Math.Max(0, (int)Math.Floor(origin.Y));
        var right = Math.Min(Width, (int)Math.Ceiling(origin.X + control.Bounds.Width));
        var bottom = Math.Min(Height, (int)Math.Ceiling(origin.Y + control.Bounds.Height));

        var region = new List<uint>(Math.Max(0, (right - left) * (bottom - top)));
        for (var y = top; y < bottom; y++)
            for (var x = left; x < right; x++)
                region.Add(_pixels[(y * Width) + x]);
        return region;
    }

    /// <summary>
    /// The raw pixel at a point in the window's coordinate space. Opaque on purpose — the caller gets a
    /// <c>uint</c>, not a colour, because the channel order is not guaranteed and this file's whole contract
    /// is distinctness and difference rather than literal values.
    /// </summary>
    public uint At(int x, int y)
    {
        if (x < 0 || y < 0 || x >= Width || y >= Height)
            throw new ArgumentOutOfRangeException(nameof(x), $"({x},{y}) is outside the {Width}x{Height} frame");
        return _pixels[(y * Width) + x];
    }

    /// <summary>
    /// How bright a pixel is, without needing to know which byte is which. The sum of all four bytes: alpha
    /// is 255 in whichever position it sits for an opaque frame, so it contributes the same constant to every
    /// pixel and a comparison between two of them still means what it says.
    /// </summary>
    public static int Brightness(uint pixel)
        => (int)((pixel & 0xFF) + ((pixel >> 8) & 0xFF) + ((pixel >> 16) & 0xFF) + ((pixel >> 24) & 0xFF));

    /// <summary>Write the frame out for a human to look at. Not called by any test — this is the hook for
    /// diagnosing one that fails, since "the pixels differ" is not something you can read.</summary>
    public static void Dump(Window window, string path)
    {
        var bitmap = window.CaptureRenderedFrame();
        if (bitmap is null) return;
        using var _ = bitmap;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        bitmap.Save(path);
    }
}
