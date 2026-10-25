using CastBox.ScreenShare;
using Xunit;

namespace CastBox.Tests;

public sealed class SilenceFillerTests
{
    private static byte[] Bytes(params int[] values) => values.Select(v => (byte)v).ToArray();

    [Fact]
    public void Nothing_playing_gives_exactly_the_requested_amount_of_silence()
    {
        var filler = new SilenceFiller(1000);
        var taken = filler.Take(64);
        Assert.Equal(64, taken.Length);
        Assert.All(taken, b => Assert.Equal(0, b));
    }

    [Fact]
    public void Sound_comes_out_in_order_across_chunk_boundaries_and_the_gap_is_silence()
    {
        var filler = new SilenceFiller(1000);
        filler.Add(Bytes(1, 2, 3), 3);
        filler.Add(Bytes(4, 5), 2);
        Assert.Equal(Bytes(1, 2), filler.Take(2));
        Assert.Equal(Bytes(3, 4, 5, 0, 0), filler.Take(5)); // sound ran out: padded with silence
        Assert.Equal(Bytes(0, 0), filler.Take(2));
    }

    [Fact]
    public void Only_the_counted_bytes_of_a_buffer_are_used()
    {
        var filler = new SilenceFiller(1000);
        filler.Add(Bytes(9, 9, 9, 9), 2);
        Assert.Equal(Bytes(9, 9, 0, 0), filler.Take(4));
        filler.Add(Bytes(1), 0); // empty add changes nothing
        Assert.Equal(Bytes(0), filler.Take(1));
    }

    [Fact]
    public void Sound_that_piled_up_is_dropped_oldest_first_so_it_cannot_lag_behind_the_picture()
    {
        var filler = new SilenceFiller(4);
        filler.Add(Bytes(1, 1, 1, 1), 4);
        filler.Add(Bytes(2, 2, 2, 2), 4);
        filler.Add(Bytes(3, 3, 3, 3), 4);
        Assert.Equal(Bytes(3, 3, 3, 3), filler.Take(4));
        Assert.Equal(Bytes(0, 0), filler.Take(2));
    }
}
