namespace HyperPlot.SurfaceLod;

/// <summary>
/// SPIKE (row K0c, Issue #489): the places of the tiles in the pool's faces. A face of side S holds ⌊S / (tile + 1)⌋² tiles
/// of (tile + 1)² heights: with the residency cache's 258 × 258 faces (design §5.1), one 257² tile of 256 cells, four
/// 129² tiles of 128 cells (2 × 129 = 258, no texel left), or nine 65² tiles of 64 cells (3 × 65 = 195: 43 % of the face
/// unused; 4 × 65 = 260 would need a face of 260). A frame makes each drawn tile resident: a tile already placed is kept,
/// a new one takes a free place or the place of a tile no drawn tile of this frame holds (a clock over the places), and
/// its heights are copied in. The upload is not capped: the spike measures what each frame asks for against the cache's
/// 4 MB a frame (design §5.1). A tile that finds no place is not drawn and is counted. Then the drawn tiles are written
/// grouped by edge shape, for one draw a shape.
/// </summary>
internal sealed class Residency
{
    private readonly SurfaceData _data;
    private readonly Gpu _gpu;
    private readonly int _perSide;
    private readonly int _perFace;
    private readonly int _places;
    private readonly int[] _nodePlace;
    private readonly int[] _placeNode;
    private readonly long[] _placeUsed;
    private readonly int[] _free;
    private readonly int[] _cursor = new int[Stitch.Shapes];
    private int _freeCount;
    private int _hand;

    public Residency(SurfaceData data, Gpu gpu, int faceSide, int faces)
    {
        _data = data;
        _gpu = gpu;
        _perSide = faceSide / (data.Tile + 1);
        _perFace = _perSide * _perSide;
        _places = faces * _perFace;
        _nodePlace = new int[data.NodeCount];
        Array.Fill(_nodePlace, -1);
        _placeNode = new int[_places];
        Array.Fill(_placeNode, -1);
        _placeUsed = new long[_places];
        Array.Fill(_placeUsed, -1);
        _free = new int[_places];
        for (var p = 0; p < _places; p++)
        {
            _free[p] = _places - 1 - p;
        }

        _freeCount = _places;
        Instances = new TileInstance[data.NodeCount];
    }

    /// <summary>The tiles a face holds.</summary>
    public int PerFace => _perFace;

    /// <summary>The tiles the pool holds.</summary>
    public int Places => _places;

    /// <summary>The bytes a tile's upload copies: (tile + 1)² float32.</summary>
    public int TileBytes => (_data.Tile + 1) * (_data.Tile + 1) * sizeof(float);

    /// <summary>The drawn tiles of the last frame, grouped by edge shape.</summary>
    public TileInstance[] Instances { get; }

    /// <summary>The first instance of each edge shape.</summary>
    public int[] Starts { get; } = new int[Stitch.Shapes];

    /// <summary>The instances of each edge shape.</summary>
    public int[] Counts { get; } = new int[Stitch.Shapes];

    /// <summary>The instances written: the drawn tiles that found a place.</summary>
    public int Written { get; private set; }

    /// <summary>The tiles uploaded in the last frame.</summary>
    public int Uploaded { get; private set; }

    /// <summary>The tiles of the last frame that found no place.</summary>
    public int Missing { get; private set; }

    /// <summary>Makes the selector's tiles resident, uploading the new ones, and writes the instances.</summary>
    public void Prepare(Selector selector, long frame)
    {
        var data = _data;
        var count = selector.Count;
        var tiles = selector.Tiles;
        var masks = selector.Masks;
        Uploaded = 0;
        Missing = 0;
        for (var k = 0; k < count; k++)
        {
            var place = _nodePlace[tiles[k]];
            if (place >= 0)
            {
                _placeUsed[place] = frame;
            }
        }

        Array.Clear(Counts);
        for (var k = 0; k < count; k++)
        {
            var id = tiles[k];
            if (_nodePlace[id] < 0)
            {
                var place = Take(frame);
                if (place < 0)
                {
                    Missing++;
                    continue;
                }

                _nodePlace[id] = place;
                _placeNode[place] = id;
                _placeUsed[place] = frame;
                Upload(id, place);
                Uploaded++;
            }

            Counts[masks[k]]++;
        }

        var start = 0;
        for (var shape = 0; shape < Stitch.Shapes; shape++)
        {
            Starts[shape] = start;
            _cursor[shape] = start;
            start += Counts[shape];
        }

        Written = start;
        for (var k = 0; k < count; k++)
        {
            var id = tiles[k];
            var place = _nodePlace[id];
            if (place < 0)
            {
                continue;
            }

            var level = data.NodeLevel[id];
            var sub = place % _perFace;
            var span = data.Tile << level;
            Instances[_cursor[masks[k]]++] = new TileInstance
            {
                Face = (uint)(place / _perFace),
                OriginX = (uint)((sub % _perSide) * (data.Tile + 1)),
                OriginY = (uint)((sub / _perSide) * (data.Tile + 1)),
                SampleX = (uint)(data.NodeX[id] * span),
                SampleY = (uint)(data.NodeY[id] * span),
                Step = 1u << level,
            };
        }
    }

    // A free place, or the next place by the clock that no drawn tile of this frame holds; -1 when every place is held.
    private int Take(long frame)
    {
        if (_freeCount > 0)
        {
            return _free[--_freeCount];
        }

        for (var tries = 0; tries < _places; tries++)
        {
            var place = _hand;
            _hand = (_hand + 1) % _places;
            if (_placeUsed[place] != frame)
            {
                _nodePlace[_placeNode[place]] = -1;
                return place;
            }
        }

        return -1;
    }

    private void Upload(int id, int place)
    {
        var data = _data;
        var level = data.NodeLevel[id];
        var sub = place % _perFace;
        var side = data.Tile + 1;
        _gpu.Upload(
            place / _perFace,
            (sub % _perSide) * side,
            (sub / _perSide) * side,
            data.Levels[level],
            SurfaceData.LevelWidth(level),
            data.NodeX[id] * data.Tile,
            data.NodeY[id] * data.Tile,
            side
        );
    }
}
