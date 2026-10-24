using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace CastBox.Personal;

/// <summary>Small JPEG previews of the person's pictures, so a TV can show a grid without downloading every full-size photo.</summary>
public static class Thumbnails
{
    private const int OrientationTag = 0x0112;

    /// <summary>
    /// Writes a JPEG of <paramref name="source"/> no wider or taller than <paramref name="maxSide"/> to <paramref name="target"/>
    /// (via a temporary file, so a reader never sees half a picture). Photos are turned upright using their camera orientation.
    /// Throws if the file isn't a picture System.Drawing can read.
    /// </summary>
    public static void Create(string source, string target, int maxSide)
    {
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var original = Image.FromStream(input, useEmbeddedColorManagement: false, validateImageData: false);
        ApplyOrientation(original);

        var scale = Math.Min(1.0, (double)maxSide / Math.Max(original.Width, original.Height));
        var width = Math.Max(1, (int)Math.Round(original.Width * scale));
        var height = Math.Max(1, (int)Math.Round(original.Height * scale));
        using var thumb = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(thumb))
        {
            g.Clear(Color.Black);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.SmoothingMode = SmoothingMode.HighQuality;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(original, new Rectangle(0, 0, width, height));
        }

        var directory = Path.GetDirectoryName(target);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        var temp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var jpeg = ImageCodecInfo.GetImageEncoders().First(e => e.FormatID == ImageFormat.Jpeg.Guid);
            using var parameters = new EncoderParameters(1);
            parameters.Param[0] = new EncoderParameter(Encoder.Quality, 82L);
            thumb.Save(temp, jpeg, parameters);
            File.Move(temp, target, overwrite: true);
        }
        finally { try { File.Delete(temp); } catch { } }
    }

    /// <summary>Rotates/flips <paramref name="image"/> in memory according to its EXIF orientation (1 = already upright).</summary>
    private static void ApplyOrientation(Image image)
    {
        if (!image.PropertyIdList.Contains(OrientationTag)) return;
        var value = image.GetPropertyItem(OrientationTag)?.Value;
        if (value is not { Length: >= 2 }) return;
        var flip = (BitConverter.ToUInt16(value, 0)) switch
        {
            2 => RotateFlipType.RotateNoneFlipX,
            3 => RotateFlipType.Rotate180FlipNone,
            4 => RotateFlipType.RotateNoneFlipY,
            5 => RotateFlipType.Rotate90FlipX,
            6 => RotateFlipType.Rotate90FlipNone,
            7 => RotateFlipType.Rotate270FlipX,
            8 => RotateFlipType.Rotate270FlipNone,
            _ => RotateFlipType.RotateNoneFlipNone,
        };
        if (flip != RotateFlipType.RotateNoneFlipNone) image.RotateFlip(flip);
    }
}
