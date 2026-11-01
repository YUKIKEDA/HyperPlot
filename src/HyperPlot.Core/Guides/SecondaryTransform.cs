namespace HyperPlot;

/// <summary>
/// The transform a secondary axis's ticks are chosen through (design §4.1, row G6a): from an axis value t back to the primary
/// scale's space, the user's inverse then the primary's transform, negated when the user's transform decreases so that it
/// increases as a tick axis needs. The guides hold one for x and one for y and set them at each layout; allocates nothing.
/// </summary>
internal sealed class SecondaryTransform : IScale
{
    private IScale? _primary;
    private IScale? _user;
    private bool _negate;

    /// <summary>Sets the primary scale's transform (null for linear), the user's transform (null for a copy), and the direction.</summary>
    public void Set(IScale? primary, IScale? user, bool negate) => (_primary, _user, _negate) = (primary, user, negate);

    /// <inheritdoc />
    public void Forward(ReadOnlySpan<double> values, Span<double> destination)
    {
        if (_user is null)
        {
            values.CopyTo(destination);
        }
        else
        {
            _user.Inverse(values, destination);
        }

        _primary?.Forward(destination, destination);
        if (_negate)
        {
            for (var i = 0; i < destination.Length; i++)
            {
                destination[i] = -destination[i];
            }
        }
    }

    /// <inheritdoc />
    public void Inverse(ReadOnlySpan<double> values, Span<double> destination)
    {
        values.CopyTo(destination);
        if (_negate)
        {
            for (var i = 0; i < destination.Length; i++)
            {
                destination[i] = -destination[i];
            }
        }

        _primary?.Inverse(destination, destination);
        _user?.Forward(destination, destination);
    }
}
