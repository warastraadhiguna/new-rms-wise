using Pos.Shared.Printing;

namespace Pos.Tests.Printing;

public class EscPosTests
{
    private static readonly byte[] Init = [0x1B, 0x40];
    private static readonly byte[] DrawerPulse = [0x1B, 0x70, 0x00, 0x19, 0xFA];
    private static readonly byte[] FeedAndCut = [0x1D, 0x56, 0x42, 0x00];

    [Fact]
    public void Receipt_is_init_text_lines_then_feed_and_cut()
    {
        var bytes = EscPos.Receipt(["AB", "C"], openDrawer: false);

        byte[] expected = [.. Init, (byte)'A', (byte)'B', 0x0A, (byte)'C', 0x0A, .. FeedAndCut];
        Assert.Equal(expected, bytes);
    }

    [Fact]
    public void Cash_sale_opens_the_drawer_right_after_init()
    {
        var bytes = EscPos.Receipt(["X"], openDrawer: true);

        Assert.Equal([.. Init, .. DrawerPulse], bytes[..7]);
        Assert.Equal(FeedAndCut, bytes[^4..]);
        Assert.Equal(1, Occurrences(bytes, DrawerPulse));
    }

    [Fact]
    public void Without_drawer_there_is_no_pulse()
    {
        Assert.Equal(0, Occurrences(EscPos.Receipt(["X"], openDrawer: false), DrawerPulse));
    }

    [Fact]
    public void Cut_is_the_last_command_and_appears_once()
    {
        var bytes = EscPos.Receipt(["a", "b", "c"], openDrawer: true);

        Assert.Equal(1, Occurrences(bytes, FeedAndCut));
        Assert.Equal(FeedAndCut, bytes[^4..]);
    }

    [Fact]
    public void Text_is_plain_ascii_accents_removed_and_unknown_characters_replaced()
    {
        var bytes = EscPos.Receipt(["Café Ñ €5"], openDrawer: false);

        Assert.Equal("Cafe N ?5\n"u8.ToArray(), bytes[2..^4]);
    }

    [Fact]
    public void Control_characters_in_text_cannot_inject_commands()
    {
        var bytes = EscPos.Receipt(["A\u001B@B\u001DV"], openDrawer: false);

        Assert.Equal("A?@B?V\n"u8.ToArray(), bytes[2..^4]);
    }

    [Fact]
    public void Drawer_only_command()
    {
        Assert.Equal([.. Init, .. DrawerPulse], EscPos.OpenDrawer());
    }

    private static int Occurrences(byte[] haystack, byte[] needle)
    {
        var count = 0;
        for (var i = 0; i + needle.Length <= haystack.Length; i++)
        {
            if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle))
                count++;
        }
        return count;
    }
}
