using CastBox.Personal;
using Xunit;

namespace CastBox.Tests;

public sealed class PersonalIndexTests : IDisposable
{
    private static readonly HashSet<string> Images = new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".png" };
    private static readonly HashSet<string> Videos = new(StringComparer.OrdinalIgnoreCase) { ".mp4", ".mov" };
    private readonly string _root = Path.Combine(Path.GetTempPath(), "castbox-personal-" + Guid.NewGuid().ToString("N"));

    public PersonalIndexTests()
    {
        Touch("b.JPG"); Touch("a.jpg"); Touch("notes.txt"); Touch("clip.mp4");
        Touch("Trip/2.jpg"); Touch("Trip/1.jpg"); Touch("Trip/sub/3.png");
        Touch("Empty/readme.txt");
        Touch("Videos/v.mov");
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private void Touch(string relative)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "x");
    }

    private PersonalIndex Build(params string[] roots) =>
        PersonalIndex.Build(roots, path => "id-" + path.GetHashCode().ToString("x"), Images, Videos);

    [Fact]
    public void Only_pictures_and_videos_are_counted_everywhere_up_the_tree()
    {
        var index = Build(_root);
        Assert.Equal(5, index.Pictures);
        Assert.Equal(2, index.Videos);
        var root = Assert.Single(index.Roots);
        Assert.Equal(7, root.TotalItems);
        Assert.Equal(3, index.Resolve(0, "Trip")!.TotalItems);
    }

    [Fact]
    public void Folders_without_pictures_or_videos_are_left_out_and_the_rest_are_sorted()
    {
        var root = Build(_root).Roots[0];
        Assert.Equal(new[] { "Trip", "Videos" }, root.Dirs.Select(d => d.Name));
        Assert.Equal(new[] { "a.jpg", "b.JPG", "clip.mp4" }, root.Files.Select(f => f.Name));
        Assert.Equal(new[] { "1.jpg", "2.jpg" }, index().Resolve(0, "Trip")!.Files.Select(f => f.Name));
        PersonalIndex index() => Build(_root);
    }

    [Fact]
    public void A_folder_is_covered_by_its_first_picture_or_one_from_below_and_not_by_a_video()
    {
        var index = Build(_root);
        Assert.Equal("a.jpg", index.Roots[0].Cover!.Name);
        Assert.Equal("1.jpg", index.Resolve(0, "Trip")!.Cover!.Name);
        Assert.Equal("3.png", index.Resolve(0, "Trip/sub")!.Cover!.Name);
        Assert.Null(index.Resolve(0, "Videos")!.Cover);
    }

    [Fact]
    public void Folders_are_found_by_a_relative_path_in_any_case_and_with_either_slash()
    {
        var index = Build(_root);
        Assert.Same(index.Roots[0], index.Resolve(0, ""));
        Assert.Equal("sub", index.Resolve(0, "trip/SUB")!.Name);
        Assert.Equal("sub", index.Resolve(0, "Trip" + (char)92 + "sub")!.Name);
        Assert.Null(index.Resolve(0, "Trip/nope"));
        Assert.Null(index.Resolve(1, ""));
        Assert.Null(index.Resolve(-1, ""));
    }

    [Fact]
    public void Every_file_can_be_found_again_by_its_id_and_knows_its_path()
    {
        var index = Build(_root);
        var file = index.Resolve(0, "Trip/sub")!.Files.Single();
        var found = index.Find(file.Id)!;
        Assert.Same(file, found);
        Assert.Equal(Path.Combine(_root, "Trip", "sub", "3.png"), found.FullPath);
        Assert.True(found.IsImage);
        Assert.Equal("png", found.Extension);
        Assert.Equal("3", found.Title);
        Assert.Null(index.Find("nope"));
    }

    [Fact]
    public void A_missing_folder_is_skipped_and_several_folders_are_numbered_in_order()
    {
        var other = Path.Combine(_root, "Trip");
        var index = Build(Path.Combine(_root, "gone"), other, _root);
        Assert.Equal(2, index.Roots.Count);
        Assert.Equal("Trip", index.Roots[0].Name);
        Assert.Equal(Path.GetFileName(_root), index.Roots[1].Name);
    }
}
