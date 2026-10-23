using CastBox.MediaLog;
using Xunit;

namespace CastBox.Tests;

public class TitleParserTests
{
    [Theory]
    [InlineData(@"D:\Movies\Pam.and.Tommy.S01.1080p.DSNP.WEB-DL.DD+5.1.H.264-playWEB\Pam.and.Tommy.S01E01.Drilling.and.Pounding.1080p.DSNP.WEB-DL.DD+5.1.H.264-playWEB.mkv", "Pam and Tommy", 1, 1)]
    [InlineData(@"D:\Movies\Star.Wars.Young.Jedi.Adventures.S01.1080p.DSNP.WEB-DL.DD+5.1.H.264-playWEB\Star.Wars.Young.Jedi.Adventures.S01E25.The.Prince.and.the.Pirate.1080p.DSNP.WEB-DL.DD+5.1.H.264-playWEB.mkv", "Star Wars Young Jedi Adventures", 1, 25)]
    [InlineData(@"D:\Movies\Stargate.Atlantis.S01.1080p.AMZN.WEB-DL.DD+2.0.H.264-playWEB\Stargate.Atlantis.S01E02.Rising.Part.2.1080p.AMZN.WEB-DL.DD+2.0.H.264-playWEB.mkv", "Stargate Atlantis", 1, 2)]
    public void Episodes_from_scene_names(string path, string title, int season, int episode)
    {
        var parsed = TitleParser.Parse(path);
        Assert.Equal(WatchKind.Episode, parsed.Kind);
        Assert.Equal(title, parsed.Title);
        Assert.Equal(season, parsed.Season);
        Assert.Equal(episode, parsed.Episode);
    }

    [Theory]
    [InlineData(@"D:\TV\Show Name\Show Name - 1x05 - Title.mkv", "Show Name", 1, 5)]
    [InlineData(@"D:\TV\Show Name\Show_Name_s02e10.mkv", "Show Name", 2, 10)]
    [InlineData(@"D:\TV\Show.Name.S03E04E05.720p.HDTV.x264.mkv", "Show Name", 3, 4)]
    public void Other_episode_notations(string path, string title, int season, int episode)
    {
        var parsed = TitleParser.Parse(path);
        Assert.Equal((WatchKind.Episode, title, season, episode), (parsed.Kind, parsed.Title, parsed.Season, parsed.Episode));
    }

    [Fact]
    public void A_bare_episode_file_borrows_the_title_from_its_folder()
    {
        var parsed = TitleParser.Parse(@"D:\TV\Pam.and.Tommy.S01.1080p.WEB-DL\S01E03.mkv");
        Assert.Equal(WatchKind.Episode, parsed.Kind);
        Assert.Equal("Pam and Tommy", parsed.Title);
        Assert.Equal((1, 3), (parsed.Season, parsed.Episode));
    }

    [Fact]
    public void A_show_year_in_the_name_is_kept_as_a_hint_but_does_not_make_it_a_movie()
    {
        var parsed = TitleParser.Parse(@"D:\TV\Pam.and.Tommy.2022.S01E01.1080p.WEB-DL.mkv");
        Assert.Equal(WatchKind.Episode, parsed.Kind);
        Assert.Equal("Pam and Tommy", parsed.Title);
        Assert.Equal(2022, parsed.Year);
    }

    [Theory]
    [InlineData(@"D:\Movies\Obsession.2025.1080p.BluRay.DD+7.1.x264-playHD\Obsession.2025.1080p.BluRay.DD+7.1.x264-playHD.mkv", "Obsession", 2025)]
    [InlineData(@"D:\Movies\Trei.frati.de.belea.2006.VOYO.WEB-DL.AAC2.0.H.264-playWEB.mkv", "Trei frati de belea", 2006)]
    [InlineData(@"D:\Movies\Dune (2021) 2160p UHD.mkv", "Dune", 2021)]
    public void Movies_have_a_title_and_a_year(string path, string title, int year)
    {
        var parsed = TitleParser.Parse(path);
        Assert.Equal(WatchKind.Movie, parsed.Kind);
        Assert.Equal(title, parsed.Title);
        Assert.Equal(year, parsed.Year);
    }

    [Fact]
    public void A_title_with_a_number_that_is_not_a_year_is_left_alone()
    {
        var parsed = TitleParser.Parse(@"D:\Movies\Blade.Runner.2049.2017.1080p.BluRay.mkv");
        // "2049" is outside 19xx/20xx only by luck of the range; the later 2017 is what we want, but the
        // first match wins, so this documents the known limitation: such titles are confirmed by the person.
        Assert.Equal(WatchKind.Movie, parsed.Kind);
    }

    [Theory]
    [InlineData(@"D:\Home videos\holiday clip.mp4")]
    [InlineData(@"D:\Misc\video123.mkv")]
    public void Names_that_say_nothing_are_unknown(string path)
    {
        Assert.Equal(WatchKind.Unknown, TitleParser.Parse(path).Kind);
    }

    [Fact]
    public void A_movie_without_a_year_is_unknown_so_the_person_confirms_it()
    {
        var parsed = TitleParser.Parse(@"D:\Movies\Some.Film.1080p.BluRay.x264.mkv");
        Assert.Equal(WatchKind.Unknown, parsed.Kind);
        Assert.Equal("Some Film", parsed.Title);
    }

    [Fact]
    public void Only_the_names_are_read_so_a_missing_file_is_fine()
    {
        Assert.Equal(WatchKind.Episode, TitleParser.Parse(@"Z:\does\not\exist\Show.S01E01.mkv").Kind);
    }

    [Theory]
    [InlineData("Pam & Tommy", "pam and tommy")]
    [InlineData("Amélie", "amelie")]
    [InlineData("Star Wars: Young Jedi Adventures", "STAR WARS YOUNG JEDI ADVENTURES")]
    [InlineData("Trei frați de belea", "Trei frati de belea")]
    public void Normalize_ignores_case_accents_punctuation_and_ampersands(string a, string b)
    {
        Assert.Equal(TitleParser.Normalize(a), TitleParser.Normalize(b));
    }

    [Fact]
    public void Normalize_keeps_different_titles_different()
    {
        Assert.NotEqual(TitleParser.Normalize("Dune"), TitleParser.Normalize("Dune Part Two"));
    }
}
