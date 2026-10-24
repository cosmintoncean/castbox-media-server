using System.Drawing;
using System.Drawing.Imaging;
using System.Reflection;
using CastBox.Personal;
using Xunit;

namespace CastBox.Tests;

public sealed class ThumbnailTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "castbox-thumbs-" + Guid.NewGuid().ToString("N"));

    public ThumbnailTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private string Picture(string name, int width, int height, ushort? orientation = null)
    {
        var path = Path.Combine(_dir, name);
        using var bitmap = new Bitmap(width, height);
        using (var g = Graphics.FromImage(bitmap)) g.Clear(Color.CornflowerBlue);
        if (orientation is { } o)
        {
            var item = (PropertyItem)Activator.CreateInstance(typeof(PropertyItem), nonPublic: true)!;
            item.Id = 0x0112; item.Type = 3; item.Len = 2; item.Value = BitConverter.GetBytes(o);
            bitmap.SetPropertyItem(item);
        }
        bitmap.Save(path, ImageFormat.Jpeg);
        return path;
    }

    private static Size SizeOf(string path)
    {
        using var image = Image.FromFile(path);
        return image.Size;
    }

    [Fact]
    public void A_large_picture_is_scaled_down_keeping_its_shape()
    {
        var target = Path.Combine(_dir, "out", "a.jpg");
        Thumbnails.Create(Picture("a.jpg", 400, 200), target, 100);
        Assert.Equal(new Size(100, 50), SizeOf(target));
    }

    [Fact]
    public void A_small_picture_is_not_blown_up()
    {
        var target = Path.Combine(_dir, "b-small.jpg");
        Thumbnails.Create(Picture("b.jpg", 60, 40), target, 480);
        Assert.Equal(new Size(60, 40), SizeOf(target));
    }

    [Fact]
    public void A_photo_taken_sideways_comes_out_upright()
    {
        // Stored 400x200 with orientation 6 ("rotate 90 degrees clockwise to view"): upright it is 200 wide and 400 tall.
        var target = Path.Combine(_dir, "c-out.jpg");
        Thumbnails.Create(Picture("c.jpg", 400, 200, orientation: 6), target, 1000);
        Assert.Equal(new Size(200, 400), SizeOf(target));
    }

    [Fact]
    public void Something_that_is_not_a_picture_fails_and_leaves_nothing_behind()
    {
        var notPicture = Path.Combine(_dir, "notes.jpg");
        File.WriteAllText(notPicture, "this is not an image");
        var target = Path.Combine(_dir, "out2", "d.jpg");
        Assert.ThrowsAny<Exception>(() => Thumbnails.Create(notPicture, target, 100));
        Assert.False(File.Exists(target));
        Assert.Empty(Directory.Exists(Path.GetDirectoryName(target)!) ? Directory.GetFiles(Path.GetDirectoryName(target)!) : Array.Empty<string>());
    }

    [Fact]
    public void Making_a_preview_twice_replaces_it_without_error()
    {
        var source = Picture("e.jpg", 300, 300);
        var target = Path.Combine(_dir, "e-thumb.jpg");
        Thumbnails.Create(source, target, 100);
        Thumbnails.Create(source, target, 50);
        Assert.Equal(new Size(50, 50), SizeOf(target));
    }
}
