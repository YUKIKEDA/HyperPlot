namespace HyperPlot.Wpf;

/// <summary>
/// The fingers on the element (design §6.1 "WPF の変換", row W1a): one finger is a pointer; a second finger cancels it, and
/// the manipulation's scale and moves are gestures until every finger is up. A pure state, so that the tests check it
/// without a touch screen.
/// </summary>
internal sealed class TouchTracker
{
    private readonly HashSet<int> _down = [];

    /// <summary>What a touch means to the engine.</summary>
    public enum Action
    {
        /// <summary>Nothing: a finger of a gesture, or of a touch already over.</summary>
        None,

        /// <summary>The pointer goes down, moves, or goes up (by the event's kind).</summary>
        Pointer,

        /// <summary>The pointer is cancelled: a second finger came, and gestures follow.</summary>
        Cancel,
    }

    /// <summary>Gets a value indicating whether two or more fingers made this touch a gesture, until all are up.</summary>
    public bool Gesturing { get; private set; }

    /// <summary>Gets the finger of the pointer, or −1.</summary>
    public int Pointer { get; private set; } = -1;

    /// <summary>A finger touches: the first is the pointer; the second cancels it.</summary>
    public Action Down(int id)
    {
        _ = _down.Add(id);
        if (Gesturing)
        {
            return Action.None;
        }

        if (_down.Count == 1)
        {
            Pointer = id;
            return Action.Pointer;
        }

        Gesturing = true;
        var had = Pointer >= 0;
        Pointer = -1;
        return had ? Action.Cancel : Action.None;
    }

    /// <summary>A finger moves: the pointer's move, or nothing.</summary>
    public Action Move(int id) => !Gesturing && id == Pointer ? Action.Pointer : Action.None;

    /// <summary>A finger lifts: the pointer's up, or nothing; the last finger ends a gesture.</summary>
    public Action Up(int id)
    {
        _ = _down.Remove(id);
        var action = !Gesturing && id == Pointer ? Action.Pointer : Action.None;
        if (id == Pointer)
        {
            Pointer = -1;
        }

        if (_down.Count == 0)
        {
            Gesturing = false;
        }

        return action;
    }
}
