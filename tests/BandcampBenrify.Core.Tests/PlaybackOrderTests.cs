using BandcampBenrify.Core;

namespace BandcampBenrify.Core.Tests;

public class PlaybackOrderTests
{
    private sealed class Item(int n)
    {
        public int N { get; } = n;

        public override string ToString() => N.ToString();
    }

    private static List<Item> Items(int count) => Enumerable.Range(0, count).Select(i => new Item(i)).ToList();

    [Fact]
    public void ShufflePlaysEveryItemOncePerCycle()
    {
        var items = Items(20);
        var order = new PlaybackOrder<Item>(new Random(1)) { Shuffle = true };
        Item? current = null;
        var played = new List<Item>();
        for (var i = 0; i < 20; i++)
        {
            current = order.Next(items, current);
            played.Add(current!);
        }

        Assert.Equal(20, played.Distinct().Count());

        // 2 周目も続けて再生でき、直前と同じ曲から始まらない
        var next = order.Next(items, current);
        Assert.NotNull(next);
        Assert.NotSame(current, next);
    }

    [Fact]
    public void SequentialWrapsWhenRepeat()
    {
        var items = Items(3);
        var order = new PlaybackOrder<Item>();
        Assert.Same(items[0], order.Next(items, null));
        Assert.Same(items[1], order.Next(items, items[0]));
        Assert.Same(items[0], order.Next(items, items[2]));

        order.Repeat = false;
        Assert.Null(order.Next(items, items[2]));
    }

    [Fact]
    public void AddedItemsJoinCurrentCycle()
    {
        var items = Items(5);
        var order = new PlaybackOrder<Item>(new Random(2)) { Shuffle = true };
        var current = order.Next(items, null);
        var extra = Items(3).Select(i => new Item(100 + i.N)).ToList();
        items.AddRange(extra);
        order.OnAdded(extra);

        var played = new HashSet<Item> { current! };
        for (var i = 0; i < 7; i++)
        {
            current = order.Next(items, current);
            played.Add(current!);
        }

        Assert.Equal(8, played.Count);
    }

    [Fact]
    public void RemovedItemsAreNotPlayed()
    {
        var items = Items(10);
        var order = new PlaybackOrder<Item>(new Random(3)) { Shuffle = true };
        var current = order.Next(items, null);
        var removed = items.Where(i => i != current).Take(5).ToList();
        items.RemoveAll(removed.Contains);

        for (var i = 0; i < 20; i++)
        {
            current = order.Next(items, current);
            Assert.DoesNotContain(current!, removed);
        }
    }

    [Fact]
    public void PreviousReturnsHistory()
    {
        var items = Items(10);
        var order = new PlaybackOrder<Item>(new Random(4)) { Shuffle = true };
        var a = order.Next(items, null);
        var b = order.Next(items, a);
        var c = order.Next(items, b);
        Assert.Same(b, order.Previous(items, c));
        Assert.Same(a, order.Previous(items, b));
    }
}
