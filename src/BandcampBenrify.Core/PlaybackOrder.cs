namespace BandcampBenrify.Core;

/// <summary>
/// 再生順の決定。シャッフル時は「まだこの周で再生していない曲」の袋からランダムに取り出すので、
/// 全曲を一巡するまで同じ曲は流れない。一巡したら袋を詰め直して続ける。
/// </summary>
public sealed class PlaybackOrder<T>
    where T : class
{
    private readonly Random _rng;
    private readonly List<T> _bag = [];
    private readonly List<T> _history = [];
    private bool _shuffle;

    public PlaybackOrder(Random? rng = null)
    {
        _rng = rng ?? new Random();
    }

    public bool Shuffle
    {
        get => _shuffle;
        set
        {
            _shuffle = value;
            _bag.Clear();
        }
    }

    /// <summary>最後まで行ったら先頭 (シャッフル時は新しい周) に戻る。</summary>
    public bool Repeat { get; set; } = true;

    /// <summary>この周で再生されるのを待っている曲数 (シャッフル時)。</summary>
    public int RemainingInCycle => _bag.Count;

    public T? Next(IReadOnlyList<T> items, T? current)
    {
        if (items.Count == 0)
        {
            return null;
        }

        if (current is not null)
        {
            _history.Add(current);
            if (_history.Count > 1000)
            {
                _history.RemoveAt(0);
            }
        }

        if (!Shuffle)
        {
            var index = current is null ? -1 : IndexOf(items, current);
            if (index + 1 < items.Count)
            {
                return items[index + 1];
            }

            return Repeat ? items[0] : null;
        }

        var alive = new HashSet<T>(items, ReferenceEqualityComparer.Instance);
        _bag.RemoveAll(x => !alive.Contains(x));
        if (_bag.Count == 0)
        {
            Refill(items, current);
            if (_bag.Count == 0)
            {
                // 1 曲しかない
                return Repeat ? items[0] : null;
            }
        }

        var next = _bag[^1];
        _bag.RemoveAt(_bag.Count - 1);
        return next;
    }

    public T? Previous(IReadOnlyList<T> items, T? current)
    {
        var alive = new HashSet<T>(items, ReferenceEqualityComparer.Instance);
        while (_history.Count > 0)
        {
            var prev = _history[^1];
            _history.RemoveAt(_history.Count - 1);
            if (alive.Contains(prev))
            {
                if (Shuffle && current is not null && !_bag.Contains(current))
                {
                    // 戻った分、今の曲はこの周でまだ聴いていない扱いにする
                    _bag.Insert(_rng.Next(_bag.Count + 1), current);
                }

                return prev;
            }
        }

        if (!Shuffle && current is not null)
        {
            var index = IndexOf(items, current);
            return index > 0 ? items[index - 1] : null;
        }

        return null;
    }

    /// <summary>ユーザーが曲を直接選んで再生したとき。</summary>
    public void MarkPlayed(T item, T? previous)
    {
        if (previous is not null && !ReferenceEquals(previous, item))
        {
            _history.Add(previous);
        }

        _bag.Remove(item);
    }

    /// <summary>シャッフル中に曲が追加されたら、この周の残りにランダムに混ぜる。</summary>
    public void OnAdded(IEnumerable<T> added)
    {
        if (!Shuffle || _bag.Count == 0)
        {
            return; // 次の Next で全体から詰め直される
        }

        foreach (var item in added)
        {
            _bag.Insert(_rng.Next(_bag.Count + 1), item);
        }
    }

    public void Reset()
    {
        _bag.Clear();
        _history.Clear();
    }

    private void Refill(IReadOnlyList<T> items, T? current)
    {
        _bag.Clear();
        foreach (var item in items)
        {
            if (!ReferenceEquals(item, current))
            {
                _bag.Add(item);
            }
        }

        // Fisher–Yates
        for (var i = _bag.Count - 1; i > 0; i--)
        {
            var j = _rng.Next(i + 1);
            (_bag[i], _bag[j]) = (_bag[j], _bag[i]);
        }
    }

    private static int IndexOf(IReadOnlyList<T> items, T item)
    {
        for (var i = 0; i < items.Count; i++)
        {
            if (ReferenceEquals(items[i], item))
            {
                return i;
            }
        }

        return -1;
    }
}
