namespace CastBox.Personal;

/// <summary>One picture or video in the index. Its full path is the parent folder's path plus <see cref="Name"/>.</summary>
public sealed class PersonalFile
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required bool IsImage { get; init; }
    public required long Size { get; init; }
    public required DateTime ModifiedUtc { get; init; }
    public required PersonalDir Parent { get; init; }

    public string FullPath => Path.Combine(Parent.FullPath, Name);
    public string Extension => Path.GetExtension(Name).TrimStart('.').ToLowerInvariant();
    public string Title => Path.GetFileNameWithoutExtension(Name);
}

/// <summary>A folder in the index with the pictures and videos directly inside it and the folders below.</summary>
public sealed class PersonalDir
{
    public required string Name { get; init; }
    public required string FullPath { get; init; }
    public List<PersonalDir> Dirs { get; } = new();
    public List<PersonalFile> Files { get; } = new();
    /// <summary>Pictures and videos in this folder and everything below it.</summary>
    public int TotalItems { get; internal set; }
    /// <summary>A picture to stand for the folder: the first one inside it or, failing that, below it.</summary>
    public PersonalFile? Cover { get; internal set; }
}

/// <summary>
/// A compact picture of the person's personal folders, built once (and rebuilt now and then) so a TV can browse one
/// folder at a time instead of downloading a list of every file; a library can hold hundreds of thousands of pictures.
/// </summary>
public sealed class PersonalIndex
{
    private readonly Dictionary<string, PersonalFile> _byId;

    private PersonalIndex(List<PersonalDir> roots, Dictionary<string, PersonalFile> byId, int pictures, int videos)
    {
        Roots = roots; _byId = byId; Pictures = pictures; Videos = videos; BuiltUtc = DateTime.UtcNow;
    }

    public IReadOnlyList<PersonalDir> Roots { get; }
    public int Pictures { get; }
    public int Videos { get; }
    public DateTime BuiltUtc { get; }

    public PersonalFile? Find(string id) => _byId.GetValueOrDefault(id);

    /// <summary>The folder at <paramref name="relativePath"/> (slash-separated, empty for the root itself) under root number <paramref name="root"/>.</summary>
    public PersonalDir? Resolve(int root, string relativePath)
    {
        if (root < 0 || root >= Roots.Count) return null;
        var dir = Roots[root];
        foreach (var segment in relativePath.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries))
        {
            dir = dir.Dirs.FirstOrDefault(d => string.Equals(d.Name, segment, StringComparison.OrdinalIgnoreCase));
            if (dir is null) return null;
        }
        return dir;
    }

    /// <summary>
    /// Reads every folder under <paramref name="roots"/>. Folders that can't be read are skipped, as are links to other folders
    /// (so a loop can't hang the scan) and folders with no pictures or videos at all.
    /// </summary>
    public static PersonalIndex Build(IEnumerable<string> roots, Func<string, string> makeId, ISet<string> imageExtensions, ISet<string> videoExtensions)
    {
        var byId = new Dictionary<string, PersonalFile>(StringComparer.OrdinalIgnoreCase);
        var rootDirs = new List<PersonalDir>();
        int pictures = 0, videos = 0;

        PersonalDir? Scan(DirectoryInfo info, string name)
        {
            var dir = new PersonalDir { Name = name, FullPath = info.FullName };
            try
            {
                foreach (var file in info.EnumerateFiles())
                {
                    var ext = file.Extension;
                    var isImage = imageExtensions.Contains(ext);
                    if (!isImage && !videoExtensions.Contains(ext)) continue;
                    var id = makeId(file.FullName);
                    var entry = new PersonalFile { Id = id, Name = file.Name, IsImage = isImage, Size = file.Length, ModifiedUtc = file.LastWriteTimeUtc, Parent = dir };
                    dir.Files.Add(entry);
                    byId[id] = entry;
                    if (isImage) pictures++; else videos++;
                }
                foreach (var sub in info.EnumerateDirectories())
                {
                    if ((sub.Attributes & (FileAttributes.ReparsePoint | FileAttributes.System)) != 0) continue;
                    var child = Scan(sub, sub.Name);
                    if (child is not null) dir.Dirs.Add(child);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* what could be read is kept */ }

            dir.Files.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            dir.Dirs.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            dir.TotalItems = dir.Files.Count + dir.Dirs.Sum(d => d.TotalItems);
            dir.Cover = dir.Files.FirstOrDefault(f => f.IsImage) ?? dir.Dirs.Select(d => d.Cover).FirstOrDefault(c => c is not null);
            return dir.TotalItems == 0 ? null : dir;
        }

        foreach (var root in roots)
        {
            var info = new DirectoryInfo(root);
            if (!info.Exists) continue;
            var name = info.Name.Length > 0 && info.Name != "\\" ? info.Name : root.TrimEnd('\\', '/');
            rootDirs.Add(Scan(info, name) ?? new PersonalDir { Name = name, FullPath = info.FullName });
        }
        return new PersonalIndex(rootDirs, byId, pictures, videos);
    }
}
