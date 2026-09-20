#:property ManagePackageVersionsCentrally=false
#:package SkiaSharp@3.119.0

// The solution uses central package management; a file-based app inherits it and
// then trips over pinning its own version inline, hence the property above.
//
// Generates the application icon.
//
//     dotnet run packaging/make-icon.cs
//
// Writes src/XtreamPlayer.App/Assets/icon.ico plus the PNG renders beside
// it. Committed rather than hand-drawn once and forgotten: the icon is geometry,
// and geometry in source is editable, diffable and reproducible.
//
// The mark is a play triangle on a dark tile, in the app's own accent blue, with
// a seek bar beneath it. The seek bar is what stops it reading as a generic
// media player, and it is dropped below 48px where it would collapse into an
// unreadable smear.

using SkiaSharp;

var repoRoot = FindRepoRoot();
var assets = Path.Combine(repoRoot, "src", "XtreamPlayer.App", "Assets");
Directory.CreateDirectory(assets);

// Every size Windows asks for: Explorer, the taskbar, alt-tab, and the
// high-DPI variants in between.
int[] sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];

var renders = new Dictionary<int, SKBitmap>();

foreach (var size in sizes)
{
    renders[size] = Render(size);
}

// PNGs alongside the .ico: the Avalonia window icon wants one, and they make
// the result reviewable without opening an icon editor.
foreach (var size in new[] { 32, 64, 256 })
{
    var path = Path.Combine(assets, $"icon-{size}.png");
    using var data = renders[size].Encode(SKEncodedImageFormat.Png, 100);
    File.WriteAllBytes(path, data.ToArray());
    Console.WriteLine($"  {Path.GetFileName(path),-16} {new FileInfo(path).Length,7:N0} bytes");
}

var icoPath = Path.Combine(assets, "icon.ico");
WriteIco(icoPath, sizes.Select(s => (s, renders[s])).ToList());
Console.WriteLine($"  {Path.GetFileName(icoPath),-16} {new FileInfo(icoPath).Length,7:N0} bytes  ({sizes.Length} sizes)");

foreach (var bitmap in renders.Values)
{
    bitmap.Dispose();
}

// ---------------------------------------------------------------------------

static SKBitmap Render(int size)
{
    var bitmap = new SKBitmap(size, size, SKColorType.Bgra8888, SKAlphaType.Premul);

    using var canvas = new SKCanvas(bitmap);
    canvas.Clear(SKColors.Transparent);

    var s = (float)size;
    using var aa = new SKPaint { IsAntialias = true };

    // ---- tile ----
    // Windows draws app icons as rounded squares; a bare glyph looks unfinished
    // beside everything else on the taskbar.
    var inset = s * 0.015f;
    var tile = new SKRect(inset, inset, s - inset, s - inset);

    // Proportionally less rounding when small: at 16px a 21.5% radius eats a
    // third of the area the glyph has to live in.
    var radius = s * (size >= 48 ? 0.215f : 0.170f);

    using (var paint = new SKPaint { IsAntialias = true })
    {
        paint.Shader = SKShader.CreateLinearGradient(
            new SKPoint(tile.Left, tile.Top),
            new SKPoint(tile.Right, tile.Bottom),
            [new SKColor(0x26, 0x2E, 0x4A), new SKColor(0x12, 0x13, 0x1C), new SKColor(0x0A, 0x0A, 0x0E)],
            [0f, 0.55f, 1f],
            SKShaderTileMode.Clamp);

        canvas.DrawRoundRect(tile, radius, radius, paint);
    }

    // A glow behind the mark, so the triangle sits in the tile rather than on it.
    using (var paint = new SKPaint { IsAntialias = true })
    {
        paint.Shader = SKShader.CreateRadialGradient(
            new SKPoint(s * 0.46f, s * 0.44f),
            s * 0.46f,
            [new SKColor(0x5B, 0x8C, 0xFF, 0x4D), new SKColor(0x5B, 0x8C, 0xFF, 0x00)],
            [0f, 1f],
            SKShaderTileMode.Clamp);

        canvas.Save();
        using var clip = new SKPath();
        clip.AddRoundRect(new SKRoundRect(tile, radius, radius));
        canvas.ClipPath(clip, SKClipOperation.Intersect, antialias: true);
        canvas.DrawRect(tile, paint);
        canvas.Restore();
    }

    // A hairline along the top edge reads as a light source and keeps the tile
    // from looking flat at large sizes. Below 32px it is thinner than a pixel.
    if (size >= 32)
    {
        using var paint = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = MathF.Max(1f, s * 0.006f),
            Color = new SKColor(0xFF, 0xFF, 0xFF, 0x1F),
        };

        var edge = new SKRect(
            tile.Left + paint.StrokeWidth * 0.5f, tile.Top + paint.StrokeWidth * 0.5f,
            tile.Right - paint.StrokeWidth * 0.5f, tile.Bottom - paint.StrokeWidth * 0.5f);

        canvas.DrawRoundRect(edge, radius, radius, paint);
    }

    var showBar = size >= 48;

    // With the bar present the mark shifts up to stay optically centred; without
    // it the triangle owns the whole tile.
    var centreY = showBar ? s * 0.425f : s * 0.500f;

    // ---- play triangle ----
    // Nudged right of centre: a triangle balanced on its bounding box reads as
    // sitting too far left, because its visual mass is toward the flat edge.
    var halfHeight = s * (showBar ? 0.215f : 0.270f);
    var left = s * (showBar ? 0.330f : 0.290f);
    var right = s * (showBar ? 0.715f : 0.760f);

    using (var path = new SKPath())
    {
        path.MoveTo(left, centreY - halfHeight);
        path.LineTo(right, centreY);
        path.LineTo(left, centreY + halfHeight);
        path.Close();

        using var paint = new SKPaint { IsAntialias = true };

        // Rounded corners, but only where a pixel can show them. A corner
        // effect at 16px eats the point of the triangle.
        if (size >= 32)
        {
            paint.PathEffect = SKPathEffect.CreateCorner(s * 0.045f);
        }

        paint.Shader = SKShader.CreateLinearGradient(
            new SKPoint(left, centreY - halfHeight),
            new SKPoint(right, centreY + halfHeight),
            [new SKColor(0x9C, 0xBD, 0xFF), new SKColor(0x5B, 0x8C, 0xFF), new SKColor(0x3A, 0x63, 0xC4)],
            [0f, 0.5f, 1f],
            SKShaderTileMode.Clamp);

        canvas.DrawPath(path, paint);
    }

    // ---- seek bar ----
    if (showBar)
    {
        var barY = s * 0.760f;
        var barLeft = s * 0.245f;
        var barRight = s * 0.755f;
        var thickness = s * 0.046f;
        var half = thickness * 0.5f;

        using (var paint = new SKPaint
        {
            IsAntialias = true,
            Color = new SKColor(0xFF, 0xFF, 0xFF, 0x2E),
        })
        {
            canvas.DrawRoundRect(
                new SKRect(barLeft, barY - half, barRight, barY + half), half, half, paint);
        }

        // Played portion, ending at the playhead.
        var played = barLeft + (barRight - barLeft) * 0.58f;

        using (var paint = new SKPaint { IsAntialias = true })
        {
            paint.Shader = SKShader.CreateLinearGradient(
                new SKPoint(barLeft, barY),
                new SKPoint(played, barY),
                [new SKColor(0x5B, 0x8C, 0xFF), new SKColor(0x9C, 0xBD, 0xFF)],
                null,
                SKShaderTileMode.Clamp);

            canvas.DrawRoundRect(
                new SKRect(barLeft, barY - half, played, barY + half), half, half, paint);
        }

        // The playhead in the live colour: the one warm note in the mark, and
        // what makes it read as a player rather than a play button.
        using (var paint = new SKPaint
        {
            IsAntialias = true,
            Color = new SKColor(0xFF, 0x5B, 0x6E),
        })
        {
            canvas.DrawCircle(played, barY, thickness * 1.05f, paint);
        }
    }

    return bitmap;
}

/// <summary>
/// Packs the renders into a Windows .ico.
/// </summary>
/// <remarks>
/// 256px goes in as PNG, everything else as an uncompressed DIB. Windows has
/// read PNG-in-ICO since Vista, but some consumers — installers and older shell
/// code paths among them — still expect DIBs at the small sizes, and a DIB costs
/// nothing at 48px.
/// </remarks>
static void WriteIco(string path, List<(int Size, SKBitmap Bitmap)> images)
{
    using var stream = File.Create(path);
    using var writer = new BinaryWriter(stream);

    writer.Write((ushort)0);                 // reserved
    writer.Write((ushort)1);                 // 1 = icon
    writer.Write((ushort)images.Count);

    var payloads = new List<byte[]>(images.Count);

    foreach (var (size, bitmap) in images)
    {
        payloads.Add(size >= 256 ? EncodePng(bitmap) : EncodeDib(bitmap));
    }

    // Directory entries are fixed width, so the first image starts after all of
    // them.
    var offset = 6 + images.Count * 16;

    for (var i = 0; i < images.Count; i++)
    {
        var size = images[i].Size;

        writer.Write((byte)(size >= 256 ? 0 : size));  // 0 means 256
        writer.Write((byte)(size >= 256 ? 0 : size));
        writer.Write((byte)0);               // palette size, 0 for truecolour
        writer.Write((byte)0);               // reserved
        writer.Write((ushort)1);             // colour planes
        writer.Write((ushort)32);            // bits per pixel
        writer.Write(payloads[i].Length);
        writer.Write(offset);

        offset += payloads[i].Length;
    }

    foreach (var payload in payloads)
    {
        writer.Write(payload);
    }
}

static byte[] EncodePng(SKBitmap bitmap)
{
    using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
    return data.ToArray();
}

static byte[] EncodeDib(SKBitmap bitmap)
{
    var width = bitmap.Width;
    var height = bitmap.Height;

    // The AND mask is 1 bit per pixel with rows padded to 4 bytes. It is
    // redundant for a 32-bit icon, but the format still requires the space and
    // the declared height still counts it twice.
    var maskStride = (width + 31) / 32 * 4;

    using var memory = new MemoryStream();
    using var writer = new BinaryWriter(memory);

    writer.Write(40);                        // BITMAPINFOHEADER size
    writer.Write(width);
    writer.Write(height * 2);                // colour data plus mask
    writer.Write((ushort)1);                 // planes
    writer.Write((ushort)32);                // bits per pixel
    writer.Write(0);                         // BI_RGB
    writer.Write(width * height * 4 + maskStride * height);
    writer.Write(0);                         // pixels per metre, x
    writer.Write(0);                         // pixels per metre, y
    writer.Write(0);                         // palette entries used
    writer.Write(0);                         // important palette entries

    // DIBs are stored bottom-up.
    for (var y = height - 1; y >= 0; y--)
    {
        for (var x = 0; x < width; x++)
        {
            var pixel = bitmap.GetPixel(x, y);

            // Premultiplied in the bitmap, straight in the file: leaving it
            // premultiplied darkens every antialiased edge against a light
            // background.
            var alpha = pixel.Alpha;

            if (alpha is > 0 and < 255)
            {
                writer.Write(Unpremultiply(pixel.Blue, alpha));
                writer.Write(Unpremultiply(pixel.Green, alpha));
                writer.Write(Unpremultiply(pixel.Red, alpha));
            }
            else
            {
                writer.Write(pixel.Blue);
                writer.Write(pixel.Green);
                writer.Write(pixel.Red);
            }

            writer.Write(alpha);
        }
    }

    writer.Write(new byte[maskStride * height]);

    return memory.ToArray();
}

static byte Unpremultiply(byte channel, byte alpha) =>
    (byte)Math.Clamp(channel * 255 / alpha, 0, 255);

static string FindRepoRoot()
{
    var directory = new DirectoryInfo(AppContext.BaseDirectory);

    // By the solution file, not a directory name: the sources used to live
    // under app/ and naming that here is how it breaks when they move.
    while (directory is not null
        && !File.Exists(Path.Combine(directory.FullName, "XtreamPlayer.slnx")))
    {
        directory = directory.Parent;
    }

    // A file-based app runs from a build directory under the user profile, so
    // the walk above finds nothing; fall back to this file's own location.
    return directory?.FullName
        ?? Path.GetFullPath(Path.Combine(Path.GetDirectoryName(GetSourcePath())!, ".."));
}

static string GetSourcePath([System.Runtime.CompilerServices.CallerFilePath] string path = "") => path;
