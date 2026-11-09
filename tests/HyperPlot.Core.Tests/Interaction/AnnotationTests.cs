using System.Numerics;
using HyperPlot.Interaction;
using HyperPlot.Rendering;

namespace HyperPlot.Tests;

/// <summary>
/// The notes and the annotations (design §4.1, §4.6, §5.4, §6.3, §6.5, §10, row I4a, Issue #382): their checks, layout,
/// recording, hit test, drag, and notice.
/// </summary>
public sealed class AnnotationTests
{
    private static readonly PanelId Panel = new(0, 0);

    /// <summary>The compile refuses each annotation that does not check, and the builder and the factories make the spec (design §4.8).</summary>
    /// <remarks>
    /// Preconditions:
    /// - The fixture's line (x 0 to 999); a marker at (500, 0) of the data.
    ///
    /// Steps:
    /// - Set specs with each fault below; build one with Subtitle, Caption, and two Annotate calls; call Annotate(null).
    ///
    /// Expected:
    /// - ArgumentNullException: a null list, a null element, a null text. ArgumentException: 1,025 annotations, a coordinate
    ///   of NaN or ∞, a Panel of −1, a Width of 0, a Size of NaN, a Radius of −1, a Size past 512 pixels, Data2 with no
    ///   secondary scale, a rectangle of the data in polar coordinates, a polar point half of the data and half of the panel.
    ///   ArgumentOutOfRangeException: an unnamed Coord, Shape, and alignment. The built spec holds the subtitle, the caption,
    ///   and the two annotations in order; Annotate(null) throws ArgumentNullException.
    /// </remarks>
    [Fact]
    public void Spec_ChecksItsAnnotations()
    {
        using var f = new EngineFixture();
        var at = new AnnotationPoint(AnnotationValue.Data(500), AnnotationValue.Data(0));
        var marker = Annotation.Marker(at);
        void Set<TException>(PlotSpec spec)
            where TException : Exception => Assert.Throws<TException>(() => f.Engine.SetPlot(spec));

        Set<ArgumentNullException>(f.Spec with { Annotations = null! });
        Set<ArgumentNullException>(f.Spec with { Annotations = [null!] });
        Set<ArgumentNullException>(f.Spec with { Annotations = [Annotation.Text(at, "a") with { Text = null! }] });
        Set<ArgumentException>(f.Spec with { Annotations = [.. Enumerable.Repeat<AnnotationSpec>(marker, 1025)] });
        Set<ArgumentException>(f.Spec with { Annotations = [Annotation.Marker(at with { X = AnnotationValue.Data(double.NaN) })] });
        Set<ArgumentException>(f.Spec with { Annotations = [Annotation.Marker(at with { Y = AnnotationValue.Panel(double.PositiveInfinity) })] });
        Set<ArgumentException>(f.Spec with { Annotations = [marker with { Panel = -1 }] });
        Set<ArgumentException>(f.Spec with { Annotations = [Annotation.Line(at, at) with { Width = 0 }] });
        Set<ArgumentException>(f.Spec with { Annotations = [Annotation.Text(at, "a") with { Size = float.NaN }] });
        Set<ArgumentException>(f.Spec with { Annotations = [Annotation.Text(at, "a") with { Size = 600 }] });
        Set<ArgumentException>(f.Spec with { Annotations = [marker with { Radius = -1 }] });
        Set<ArgumentException>(f.Spec with { Annotations = [Annotation.Marker(at with { Y = AnnotationValue.Data2(1) })] });
        var polar = f.Spec with { Coord = Coord.Polar() };
        Set<ArgumentException>(polar with { Annotations = [Annotation.Rect(at, at)] });
        Set<ArgumentException>(polar with { Annotations = [Annotation.Marker(at with { Y = AnnotationValue.Panel(0.5) })] });
        Set<ArgumentOutOfRangeException>(
            f.Spec with
            {
                Annotations = [Annotation.Marker(at with { X = new AnnotationValue((AnnotationCoord)9, 0, 1) })],
            }
        );
        Set<ArgumentOutOfRangeException>(f.Spec with { Annotations = [marker with { Shape = (MarkerShape)9 }] });
        Set<ArgumentOutOfRangeException>(f.Spec with { Annotations = [Annotation.Text(at, "a") with { Horizontal = (TextAlign)9 }] });

        var line = Annotation.Line(at, at);
        var builder = Plot.Create(f.Table).Line(f.Table.Col<double>("x"), f.Table.Col<float>("y")).Subtitle("sub").Caption("cap");
        var spec = builder.Annotate(marker).Annotate(line).Build();
        Assert.Equal(("sub", "cap"), (spec.Subtitle, spec.Caption));
        Assert.Equal([marker, line], spec.Annotations);
        _ = Assert.Throws<ArgumentNullException>(() => Plot.Create(f.Table).Annotate(null!));
    }

    /// <summary>
    /// The subtitle takes a band below the title and the caption one at the bottom, and the panel gives up their height
    /// (design §4.6, Issue #382 Decisions 2 and 14), in a plot and in a figure's cell.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The fixture's line titled "Title".
    ///
    /// Steps:
    /// - Draw it; with Subtitle "Sub"; with Caption "Cap" too. Draw a figure of the last and the plain plot side by side.
    ///
    /// Expected:
    /// - The subtitle: the panel's top lower by its band, its text below the title's, centered as the title. The caption: the
    ///   panel's bottom higher, its text below the panel and ending 10 pixels (the padding) from the target's right. In the
    ///   figure, the first cell's panel starts lower than the second's.
    /// </remarks>
    [Fact]
    public void Notes_TakeTheirBands()
    {
        using var f = new EngineFixture();
        var titled = f.Spec with { Title = "Title" };
        f.Engine.SetPlot(titled);
        f.Engine.RenderFrame();
        var plain = f.Engine.Plan!.Guides.PanelAt(0);
        var (plainTop, plainBottom) = (plain.Top, plain.Top + plain.Height);

        f.Engine.SetPlot(titled with { Subtitle = "Sub" });
        f.Engine.RenderFrame();
        var sub = f.Engine.Plan!.Guides.PanelAt(0);
        Assert.True(sub.Top > plainTop);
        var texts = f.Engine.Plan.List.Texts.ToArray();
        var (title, subtitle) = (texts.Single(t => t.Text == "Title"), texts.Single(t => t.Text == "Sub"));
        Assert.True(subtitle.Position.Y > title.Position.Y && subtitle.Position.Y < sub.Top);
        Assert.Equal(13f, subtitle.Style.Size);

        f.Engine.SetPlot(titled with { Subtitle = "Sub", Caption = "Cap" });
        f.Engine.RenderFrame();
        var both = f.Engine.Plan!.Guides.PanelAt(0);
        Assert.True(both.Top + both.Height < plainBottom);
        var caption = f.Engine.Plan.List.Texts.ToArray().Single(t => t.Text == "Cap");
        var width = f.Backend.Text.Measure("Cap", caption.Style).Width;
        Assert.Equal(EngineFixture.Width - 10, caption.Position.X + width, 2);
        Assert.True(caption.Position.Y > both.Top + both.Height);

        f.Engine.SetFigure(Figure.HConcat(titled with { Subtitle = "Sub" }, titled));
        f.Engine.RenderFrame();
        Assert.True(f.Engine.Figure!.Plans[0].Guides.PanelAt(0).Top > f.Engine.Figure.Plans[1].Guides.PanelAt(0).Top);
    }

    /// <summary>
    /// The four kinds are recorded after the data layers inside the panel, in their default looks and given ones (design §5.4,
    /// Issue #382 Decisions 4, 8, 12).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The fixture's line; a line from (100, −10) to (900, 10); a rectangle (200, −5) to (400, 5) filled red, 2 DIPs wide; a
    ///   text "note" at (500, 0); markers at (600, 0): a circle, a square of radius 5, a cross; all of the data.
    ///
    /// Steps:
    /// - Draw.
    ///
    /// Expected:
    /// - The line: a polyline of one segment between the points' pixels, 1 pixel wide, the foreground, clipped to the panel,
    ///   after the data line. The rectangle: a red rect between the corners' pixels, and a closed polyline of 4 edges 2 pixels
    ///   wide. The text: "note" at its point, 12 pixels, left and bottom aligned. The circle: a sector call at its point; the
    ///   square: a rect 10 pixels a side; the cross: two polylines.
    /// </remarks>
    [Fact]
    public void Record_DrawsTheFourKinds()
    {
        using var f = new EngineFixture();
        var red = new Color(255, 0, 0, 255);
        AnnotationPoint P(double x, double y) => new(AnnotationValue.Data(x), AnnotationValue.Data(y));
        f.Engine.SetPlot(
            f.Spec with
            {
                Annotations =
                [
                    Annotation.Line(P(100, -10), P(900, 10)),
                    Annotation.Rect(P(200, -5), P(400, 5)) with
                    {
                        Fill = red,
                        Width = 2,
                    },
                    Annotation.Text(P(500, 0), "note"),
                    Annotation.Marker(P(600, 0)),
                    Annotation.Marker(P(600, 0)) with
                    {
                        Shape = MarkerShape.Square,
                        Radius = 5,
                    },
                    Annotation.Marker(P(600, 0)) with
                    {
                        Shape = MarkerShape.Cross,
                    },
                ],
            }
        );
        f.Engine.RenderFrame();
        var list = f.Engine.Plan!.List;
        var guide = f.Engine.Plan.Guides.PanelAt(0);
        var clip = new ClipRect(guide.Left, guide.Top, guide.Left + guide.Width, guide.Top + guide.Height);
        Vector2 Pixel(double x, double y) => f.Engine.ViewToPixel(Panel, ViewRange.FromValues(x, x), ViewRange.FromValues(y, y));

        // The data line is the first polyline; the annotation's line comes after it.
        var polylines = list.Polylines.ToArray();
        var line = polylines.Single(p => p.SegmentCount == 1 && Near(list.Segments[p.SegmentStart].Start, Pixel(100, -10)));
        Assert.True(Array.IndexOf(polylines, line) > 0);
        Assert.True(Near(list.Segments[line.SegmentStart].End, Pixel(900, 10)));
        Assert.Equal((Theme.Light.Foreground, 1f, clip), (line.Style.Color, line.Style.Width, line.Clip));

        var (corner, opposite) = (Pixel(200, -5), Pixel(400, 5));
        var (min, max) = (Vector2.Min(corner, opposite), Vector2.Max(corner, opposite));
        var fill = list.Rects.ToArray().Single(r => r.Color.X > 0.99f && r.Color.Y < 0.01f);
        Assert.Equal((min.X, min.Y, max.X - min.X, max.Y - min.Y), (fill.X, fill.Y, fill.Width, fill.Height));
        _ = Assert.Single(polylines, p => p.SegmentCount == 4 && p.Style.Width == 2);

        var text = list.Texts.ToArray().Single(t => t.Text == "note");
        Assert.True(Near(text.Position, Pixel(500, 0)));
        Assert.Equal((12f, TextAlign.Left, TextBaseline.Bottom), (text.Style.Size, text.Style.Horizontal, text.Style.Vertical));

        var at = Pixel(600, 0);
        _ = Assert.Single(list.SectorCalls.ToArray(), c => Near(c.Center, at));
        _ = Assert.Single(list.Rects.ToArray(), r => r.Width == 10 && r.Height == 10 && Near(new Vector2(r.X + 5, r.Y + 5), at));
        Assert.Equal(
            2,
            polylines.Count(p =>
                p.SegmentCount == 1
                && p.MaxX - p.MinX <= 9
                && p.MaxY - p.MinY <= 9
                && Near(new Vector2((p.MinX + p.MaxX) / 2, (p.MinY + p.MaxY) / 2), at)
            )
        );
    }

    /// <summary>
    /// Each coordinate is fixed to its own kind (Issue #382 Decisions 5, 13, 18): the data move with a zoom and the panel's
    /// shares do not; a secondary scale's values go through it; under flip x runs up the screen for a share too; polar maps a
    /// point of the data through its transform and a rectangle of shares onto the panel.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Markers at (500, 0) of the data and at shares (0.5, 0.25); a plot with a secondary y over [0, 100] and a marker at
    ///   x 500 of the data and y 50 of it; the line flipped with a marker at x 500 of the data and y 0.25 of the panel; the
    ///   line in polar coordinates with a marker at (250, 10) and a rectangle of shares (0.1, 0.1) to (0.2, 0.2).
    ///
    /// Steps:
    /// - Draw each; zoom the first by 2 notches 200 pixels right of and 150 below the panel's corner, and draw.
    ///
    /// Expected:
    /// - Before the zoom, each marker at its pixel; after it the data's moved and the shares' at the same pixel. The
    ///   secondary: y at the panel's middle. Flipped: y from x's value, x at a quarter of the width from the left. Polar: the
    ///   marker at the engine's pixel of (250, 10); the rectangle at the panel's shares.
    /// </remarks>
    [Fact]
    public void Coordinates_TakeEachAxissKind()
    {
        using var f = new EngineFixture();
        f.Engine.Options.ZoomAnimation = TimeSpan.Zero;
        var data = new AnnotationPoint(AnnotationValue.Data(500), AnnotationValue.Data(0));
        var shares = new AnnotationPoint(AnnotationValue.Panel(0.5), AnnotationValue.Panel(0.25));
        f.Engine.SetPlot(f.Spec with { Annotations = [Annotation.Marker(data), Annotation.Marker(shares)] });
        f.Engine.RenderFrame();
        var guide = f.Engine.Plan!.Guides.PanelAt(0);
        var share = new Vector2(guide.Left + (0.5f * guide.Width), guide.Top + guide.Height - (0.25f * guide.Height));
        Vector2 DataPixel() => f.Engine.ViewToPixel(Panel, ViewRange.FromValues(500.0, 500.0), ViewRange.FromValues(0.0, 0.0));
        var before = DataPixel();
        Assert.Equal(2, Centers(f).Count(c => Near(c, before) || Near(c, share)));
        _ = f.Wheel(new Vector2(guide.Left + 200, guide.Top + 150), 2);
        f.Engine.RenderFrame();
        Assert.False(Near(DataPixel(), before));
        Assert.Equal(2, Centers(f).Count(c => Near(c, DataPixel()) || Near(c, share)));

        var (x, y) = (f.Table.Col<double>("x"), f.Table.Col<float>("y"));
        var secondary = Plot.Create(f.Table)
            .Line(x, y)
            .Line(x, y, l => l.SecondaryY())
            .ScaleY(Scale.Linear().WithDomain(-20, 20))
            .ScaleY2(Scale.Linear().WithDomain(0, 100))
            .Build();
        f.Engine.SetPlot(secondary with { Annotations = [Annotation.Marker(new(AnnotationValue.Data(500), AnnotationValue.Data2(50)))] });
        f.Engine.RenderFrame();
        guide = f.Engine.Plan!.Guides.PanelAt(0);
        Assert.Equal(guide.Top + (guide.Height / 2f), Assert.Single(Centers(f)).Y, 1);

        f.Engine.SetPlot(
            f.Spec with
            {
                Coord = Coord.Cartesian(flip: true),
                Annotations = [Annotation.Marker(new(AnnotationValue.Data(500), AnnotationValue.Panel(0.25)))],
            }
        );
        f.Engine.RenderFrame();
        guide = f.Engine.Plan!.Guides.PanelAt(0);
        var flipped = Assert.Single(Centers(f));
        Assert.Equal(guide.Left + (0.25f * guide.Width), flipped.X, 1);
        Assert.Equal(DataPixel().Y, flipped.Y, 1);

        var polar = new AnnotationPoint(AnnotationValue.Data(250), AnnotationValue.Data(10));
        var corner = new AnnotationPoint(AnnotationValue.Panel(0.1), AnnotationValue.Panel(0.1));
        var opposite = new AnnotationPoint(AnnotationValue.Panel(0.2), AnnotationValue.Panel(0.2));
        f.Engine.SetPlot(f.Spec with { Coord = Coord.Polar(), Annotations = [Annotation.Marker(polar), Annotation.Rect(corner, opposite)] });
        f.Engine.RenderFrame();
        guide = f.Engine.Plan!.Guides.PanelAt(0);
        var expected = f.Engine.ViewToPixel(Panel, ViewRange.FromValues(250.0, 250.0), ViewRange.FromValues(10.0, 10.0));
        Assert.True(Near(Assert.Single(Centers(f)), expected));
        var edges = f.Engine.Plan.List.Polylines.ToArray().Single(p => p.SegmentCount == 4);
        Assert.Equal(guide.Left + (0.1f * guide.Width), edges.MinX, 0);
        Assert.Equal(guide.Top + guide.Height - (0.2f * guide.Height), edges.MinY, 0);
    }

    /// <summary>
    /// A facet draws an annotation of no panel on each, one of a number on that one, and one of a number it does not draw
    /// nowhere, with no exception (Issue #382 Decision 7).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The line wrapped by 2 categories; markers at shares (0.5, 0.5): of no panel, of panel 1, of panel 5.
    ///
    /// Steps:
    /// - Draw.
    ///
    /// Expected:
    /// - Three discs: one at each panel's middle for the first, one at panel 1's middle for the second.
    /// </remarks>
    [Fact]
    public void Facet_DrawsOnItsPanels()
    {
        using var facet = new FacetPlot();
        var f = facet.Fixture;
        var middle = new AnnotationPoint(AnnotationValue.Panel(0.5), AnnotationValue.Panel(0.5));
        f.Engine.SetPlot(
            facet.Spec with
            {
                Annotations = [Annotation.Marker(middle), Annotation.Marker(middle) with { Panel = 1 }, Annotation.Marker(middle) with { Panel = 5 }],
            }
        );
        f.Engine.RenderFrame();
        var centers = Centers(f);
        Assert.Equal(3, centers.Length);
        for (var p = 0; p < 2; p++)
        {
            var g = f.Engine.Plan!.Guides.PanelAt(p);
            var at = new Vector2(g.Left + (g.Width / 2f), g.Top + g.Height - (g.Height / 2f));
            Assert.Equal(p == 0 ? 1 : 2, centers.Count(c => Near(c, at)));
        }
    }

    /// <summary>
    /// New annotations record the plot again and nothing more: no layout, no learning, and the scale's domain does not take
    /// an annotation's value (design §4.8 "差分（行 I4a）", Issue #382 Decision 16).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The fixture's line drawn by a headless renderer (the plan's stages alone, with no engine's views).
    ///
    /// Steps:
    /// - Draw the same spec with a marker at x 5,000 of the data.
    ///
    /// Expected:
    /// - One more recording, the same layouts and learnings; the panel's x domain as before.
    /// </remarks>
    [Fact]
    public void NewAnnotations_RecordAgainAlone()
    {
        using var f = new EngineFixture();
        using var renderer = new HeadlessRenderer(f.Backend, EngineFixture.Width, EngineFixture.Height, f.Pool);
        renderer.Render(f.Spec);
        var plan = renderer.Plan!;
        var (layouts, learnings, recordings, x) = (plan.Layouts, plan.Learnings, plan.Recordings, plan.Guides.PanelAt(0).X.FinalDomain);
        renderer.Render(f.Spec with { Annotations = [Annotation.Marker(new(AnnotationValue.Data(5000), AnnotationValue.Data(0)))] });
        Assert.Equal((layouts, learnings, recordings + 1), (plan.Layouts, plan.Learnings, plan.Recordings));
        Assert.Equal(x, plan.Guides.PanelAt(0).X.FinalDomain);
    }

    /// <summary>
    /// A draggable annotation is the place <c>Panel | Annotation</c> within 4 DIPs of its shape; the last one wins where they
    /// overlap; one that is not draggable is the panel (design §6.3, Issue #382 Decisions 9 and 17).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Draggable: a line (100, −10) to (900, −10), a rectangle (200, 5) to (300, 15), a text "hit" at (500, 10), a marker at
    ///   (700, 10); a marker at (700, 10) that is not draggable over the last, and a draggable one at (800, 0) under a
    ///   draggable rectangle (750, −5) to (850, 5).
    ///
    /// Steps:
    /// - Read the place 3 pixels from each shape and 9 pixels from it; at the overlap.
    ///
    /// Expected:
    /// - 3 pixels: Panel | Annotation with each one's number; 9 pixels: Panel and −1. The marker that is not draggable does
    ///   not hide the draggable one. The overlap: the rectangle's number.
    /// </remarks>
    [Fact]
    public void HitArea_FindsTheDraggableAnnotations()
    {
        using var f = new EngineFixture();
        AnnotationPoint P(double x, double y) => new(AnnotationValue.Data(x), AnnotationValue.Data(y));
        f.Engine.SetPlot(
            f.Spec with
            {
                Annotations =
                [
                    Annotation.Line(P(100, -10), P(900, -10)) with
                    {
                        Draggable = true,
                    },
                    Annotation.Rect(P(200, 5), P(300, 15)) with
                    {
                        Draggable = true,
                    },
                    Annotation.Text(P(500, 10), "hit") with
                    {
                        Draggable = true,
                    },
                    Annotation.Marker(P(700, 10)) with
                    {
                        Draggable = true,
                    },
                    Annotation.Marker(P(700, 10)),
                    Annotation.Marker(P(800, 0)) with
                    {
                        Draggable = true,
                    },
                    Annotation.Rect(P(750, -5), P(850, 5)) with
                    {
                        Draggable = true,
                    },
                ],
            }
        );
        f.Engine.RenderFrame();
        Vector2 Pixel(double x, double y) => f.Engine.ViewToPixel(Panel, ViewRange.FromValues(x, x), ViewRange.FromValues(y, y));
        var near = HitAreaKind.Panel | HitAreaKind.Annotation;
        void Check(Vector2 at, Vector2 away, int index)
        {
            Assert.Equal(new HitArea(near, Panel, index), f.Engine.HitArea(at));
            var off = f.Engine.HitArea(away);
            Assert.Equal((HitAreaKind.Panel, -1), (off.Kind, off.Annotation));
        }

        Check(Pixel(500, -10) + new Vector2(0, 3), Pixel(500, -10) + new Vector2(0, 9), 0);
        var box = Vector2.Max(Pixel(200, 5), Pixel(300, 15));
        Check(box + new Vector2(3, 0), box + new Vector2(9, 0), 1);
        var text = Pixel(500, 10);
        Check(text + new Vector2(1, -3), text + new Vector2(-9, -3), 2);
        var marker = Pixel(700, 10);
        Check(marker + new Vector2(0, 7), marker + new Vector2(0, 14), 3);
        Assert.Equal(new HitArea(near, Panel, 6), f.Engine.HitArea(Pixel(800, 0)));
    }

    /// <summary>
    /// Over an annotation the panel's bindings still match (Issue #382 Decision 17): the hover hits the data and the wheel
    /// zooms; a left drag there moves the annotation, not the view, being bound before the pan.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The fixture's line; a draggable marker on the line's point of row 300; animations off.
    ///
    /// Steps:
    /// - Move over the marker; turn the wheel there; read the bindings; drag the marker by (40, 0).
    ///
    /// Expected:
    /// - The hover holds a hit; the wheel narrows the view; the AnnotationTool's binding comes before the pan's; after the
    ///   drag the view is as the wheel left it and the marker's x moved.
    /// </remarks>
    [Fact]
    public void PanelBindings_StillMatchOverAnAnnotation()
    {
        using var f = new EngineFixture();
        f.Engine.Options.ZoomAnimation = TimeSpan.Zero;
        var y = MathF.Sin(300 * 0.02f) * 10;
        f.Engine.SetPlot(
            f.Spec with
            {
                Annotations = [Annotation.Marker(new(AnnotationValue.Data(300), AnnotationValue.Data(y))) with { Draggable = true }],
            }
        );
        f.Engine.RenderFrame();
        var at = f.Engine.ViewToPixel(Panel, ViewRange.FromValues(300.0, 300.0), ViewRange.FromValues(y, y));
        Assert.Equal(0, f.Engine.HitArea(at).Annotation);
        _ = f.Engine.OnPointer(f.Pointer(PointerEventKind.Move, at));
        Assert.NotEmpty(f.Engine.Hover.ToArray());

        var full = f.View();
        Assert.True(f.Wheel(at, 1));
        f.Engine.RenderFrame();
        var zoomed = f.View();
        Assert.True(zoomed.X.HighValue - zoomed.X.LowValue < full.X.HighValue - full.X.LowValue);

        var bindings = f.Engine.Bindings.ToList();
        Assert.True(bindings.FindIndex(b => b.Tool is AnnotationTool) < bindings.FindIndex(b => b.Tool is PanTool));
        at = f.Engine.ViewToPixel(Panel, ViewRange.FromValues(300.0, 300.0), ViewRange.FromValues(y, y));
        f.Drag(at, at + new Vector2(40, 0));
        f.Engine.RenderFrame();
        Assert.Equal(zoomed, f.View());
        Assert.True(((MarkerAnnotationSpec)f.Engine.Annotation(0, 0)).At.X.Offset > 300 - Math.Floor(300.0));
    }

    /// <summary>
    /// A drag moves the whole annotation, each coordinate in its kind, tells once at the release, and keeps the place while the
    /// spec keeps the node (design §6.5, Issue #382 Decisions 9, 10, 16, 18).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A draggable line from (200, −10) of the data to (0.75, 0.5) of the panel's shares; animations off.
    ///
    /// Steps:
    /// - Drag the line's middle by (60, −36) pixels, counting the notices during the moves and after the release. Read the
    ///   annotation. Set the spec again with the same node; then with a new node of the same values. Read the out-of-range
    ///   numbers.
    ///
    /// Expected:
    /// - No notice during the moves, one (0, 0) after the release. The data end moved by the values under 60 pixels across
    ///   and 36 up; the share end by 60 / width and 36 / height. The same node keeps the moved line; a new node is the spec's.
    ///   Annotation(1, 0), (0, 1), and (−1, 0) throw ArgumentOutOfRangeException.
    /// </remarks>
    [Fact]
    public void Drag_MovesTheWholeAnnotationAndTellsOnce()
    {
        using var f = new EngineFixture();
        f.Engine.Options.ZoomAnimation = TimeSpan.Zero;
        var line = Annotation.Line(
            new AnnotationPoint(AnnotationValue.Data(200), AnnotationValue.Data(-10)),
            new AnnotationPoint(AnnotationValue.Panel(0.75), AnnotationValue.Panel(0.5))
        ) with
        {
            Draggable = true,
        };
        var spec = f.Spec with { Annotations = [line] };
        f.Engine.SetPlot(spec);
        f.Engine.RenderFrame();
        var told = new List<(int, int)>();
        f.Engine.AnnotationChanged += (plot, index) => told.Add((plot, index));
        var guide = f.Engine.Plan!.Guides.PanelAt(0);
        var from = f.Engine.ViewToPixel(Panel, ViewRange.FromValues(200.0, 200.0), ViewRange.FromValues(-10.0, -10.0));
        var to = new Vector2(guide.Left + (0.75f * guide.Width), guide.Top + (0.5f * guide.Height));
        var start = (from + to) / 2;
        var move = new Vector2(60, -36);

        _ = f.Engine.OnPointer(f.Pointer(PointerEventKind.Down, start, PointerButtons.Left));
        for (var i = 1; i <= 4; i++)
        {
            _ = f.Engine.OnPointer(f.Pointer(PointerEventKind.Move, start + (move * i / 4)) with { Buttons = PointerButtons.Left });
            f.Engine.RenderFrame();
        }

        Assert.Empty(told);
        _ = f.Engine.OnPointer(f.Pointer(PointerEventKind.Up, start + move, PointerButtons.Left));
        Assert.Equal([(0, 0)], told);

        var moved = (LineAnnotationSpec)f.Engine.Annotation(0, 0);
        var (x0, y0) = new ToolContext(f.Engine).PixelToView(Panel, from);
        var (x1, y1) = new ToolContext(f.Engine).PixelToView(Panel, from + move);
        Assert.Equal(200 + (x1.LowValue - x0.LowValue), moved.From.X.Origin + moved.From.X.Offset, 6);
        Assert.Equal(-10 + (y1.LowValue - y0.LowValue), moved.From.Y.Origin + moved.From.Y.Offset, 6);
        Assert.Equal(0.75 + (60.0 / guide.Width), moved.To.X.Offset, 6);
        Assert.Equal(0.5 + (36.0 / guide.Height), moved.To.Y.Offset, 6);

        f.Engine.SetPlot(spec with { Title = "again" });
        f.Engine.RenderFrame();
        Assert.Equal(moved.To, ((LineAnnotationSpec)f.Engine.Annotation(0, 0)).To);
        var fresh = line with { };
        f.Engine.SetPlot(spec with { Annotations = [fresh] });
        f.Engine.RenderFrame();
        Assert.Same(fresh, f.Engine.Annotation(0, 0));

        _ = Assert.Throws<ArgumentOutOfRangeException>(() => f.Engine.Annotation(1, 0));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => f.Engine.Annotation(0, 1));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => f.Engine.Annotation(-1, 0));
    }

    /// <summary>
    /// Under flip a share of x moves with the pointer up the screen, a polar point of the data follows the pointer, and Escape
    /// puts a dragged annotation back with no notice (Issue #382 Decisions 13, 16, 18).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The line flipped with a draggable marker at shares (0.5, 0.5); the line in polar coordinates with a draggable marker
    ///   at (250, 10) of the data; animations off.
    ///
    /// Steps:
    /// - Drag the flipped marker by (30, −40): read it. Drag the polar marker by (25, 15): draw and read its disc's center.
    ///   Press on the polar marker, move by (40, 0), press Escape, release.
    ///
    /// Expected:
    /// - Flipped: x's share up by 40 / height, y's by 30 / width. Polar: the disc within 1 pixel of where the pointer let go.
    ///   Escape: the marker back where the second drag left it, and no notice for the third.
    /// </remarks>
    [Fact]
    public void Drag_FollowsFlipAndPolarAndEscapePutsItBack()
    {
        using var f = new EngineFixture();
        f.Engine.Options.ZoomAnimation = TimeSpan.Zero;
        var middle = new AnnotationPoint(AnnotationValue.Panel(0.5), AnnotationValue.Panel(0.5));
        f.Engine.SetPlot(f.Spec with { Coord = Coord.Cartesian(flip: true), Annotations = [Annotation.Marker(middle) with { Draggable = true }] });
        f.Engine.RenderFrame();
        var guide = f.Engine.Plan!.Guides.PanelAt(0);
        var at = new Vector2(guide.Left + (guide.Width / 2f), guide.Top + (guide.Height / 2f));
        f.Drag(at, at + new Vector2(30, -40));
        var flipped = (MarkerAnnotationSpec)f.Engine.Annotation(0, 0);
        Assert.Equal(0.5 + (40.0 / guide.Height), flipped.At.X.Offset, 6);
        Assert.Equal(0.5 + (30.0 / guide.Width), flipped.At.Y.Offset, 6);

        var point = new AnnotationPoint(AnnotationValue.Data(250), AnnotationValue.Data(10));
        f.Engine.SetPlot(f.Spec with { Coord = Coord.Polar(), Annotations = [Annotation.Marker(point) with { Draggable = true }] });
        f.Engine.RenderFrame();
        var polar = Assert.Single(Centers(f));
        f.Drag(polar, polar + new Vector2(25, 15));
        f.Engine.RenderFrame();
        var dropped = Assert.Single(Centers(f));
        Assert.True(Vector2.Distance(dropped, polar + new Vector2(25, 15)) <= 1, $"{dropped} against {polar + new Vector2(25, 15)}");

        var told = 0;
        f.Engine.AnnotationChanged += (_, _) => told++;
        var before = ((MarkerAnnotationSpec)f.Engine.Annotation(0, 0)).At;
        _ = f.Engine.OnPointer(f.Pointer(PointerEventKind.Down, dropped, PointerButtons.Left));
        _ = f.Engine.OnPointer(f.Pointer(PointerEventKind.Move, dropped + new Vector2(40, 0)) with { Buttons = PointerButtons.Left });
        Assert.True(f.Key(PlotKey.Escape));
        _ = f.Engine.OnPointer(f.Pointer(PointerEventKind.Up, dropped + new Vector2(40, 0), PointerButtons.Left));
        Assert.Equal(before, ((MarkerAnnotationSpec)f.Engine.Annotation(0, 0)).At);
        Assert.Equal(0, told);
    }

    /// <summary>Warm frames of the mouse alone and of a drag over a plot with annotations allocate nothing (§10, invariant 3).</summary>
    /// <remarks>
    /// Preconditions:
    /// - The fixture's line with the four kinds, a draggable marker among them; the shaping cache full.
    ///
    /// Steps:
    /// - Rounds of 8 moves drawn after each, and rounds of a drag of the marker over 12 events drawn after each (back and
    ///   forth): 4 of each to warm, then 2 of each counted.
    ///
    /// Expected:
    /// - The counted rounds: 0 bytes.
    /// </remarks>
    [Fact]
    public void Frames_AllocateNothing()
    {
        using var f = new EngineFixture();
        AnnotationPoint P(double x, double y) => new(AnnotationValue.Data(x), AnnotationValue.Data(y));
        f.Engine.SetPlot(
            f.Spec with
            {
                Annotations =
                [
                    Annotation.Line(P(100, -10), P(900, 10)),
                    Annotation.Rect(P(200, -5), P(400, 5)),
                    Annotation.Text(P(500, 0), "note"),
                    Annotation.Marker(P(600, 0)) with
                    {
                        Draggable = true,
                    },
                ],
            }
        );
        f.Engine.RenderFrame();
        for (var i = 0; i < TextEngine.CacheCapacity; i++)
        {
            _ = f.Backend.Text.Measure(
                "filler " + i.ToString(System.Globalization.CultureInfo.InvariantCulture),
                new TextStyle(new Color(0, 0, 0, 255), 9)
            );
        }

        f.Changes.Capacity = 20_000;
        var guide = f.Engine.Plan!.Guides.PanelAt(0);
        long Moves()
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 8; i++)
            {
                _ = f.Engine.OnPointer(f.Pointer(PointerEventKind.Move, new Vector2(guide.Left + 20 + (i * 23), guide.Top + 30 + (i * 7))));
                f.Engine.RenderFrame();
            }

            return GC.GetAllocatedBytesForCurrentThread() - before;
        }

        var events = new PointerEvent[12];
        long Drags()
        {
            var at = Assert.Single(Centers(f));
            events[0] = f.Pointer(PointerEventKind.Down, at, PointerButtons.Left);
            for (var i = 1; i < 11; i++)
            {
                events[i] = f.Pointer(PointerEventKind.Move, at + new Vector2(i < 6 ? i * 4 : (10 - i) * 4, 0)) with
                {
                    Buttons = PointerButtons.Left,
                };
            }

            events[11] = f.Pointer(PointerEventKind.Up, at, PointerButtons.Left);
            var before = GC.GetAllocatedBytesForCurrentThread();
            foreach (var e in events)
            {
                _ = f.Engine.OnPointer(e);
                f.Engine.RenderFrame();
            }

            return GC.GetAllocatedBytesForCurrentThread() - before;
        }

        _ = f.Engine.OnPointer(f.Pointer(PointerEventKind.Leave, default));
        var drags = Warm(Drags);
        var moves = Warm(Moves);
        Assert.Equal((0L, 0L), (moves, drags));

        static long Warm(Func<long> round)
        {
            for (var i = 0; i < 4; i++)
            {
                _ = round();
            }

            return round() + round();
        }
    }

    private static Vector2[] Centers(EngineFixture f) => [.. f.Engine.Plan!.List.SectorCalls.ToArray().Select(c => c.Center)];

    private static bool Near(Vector2 a, Vector2 b) => Vector2.Distance(a, b) < 0.75f;

    /// <summary>A line wrapped by 2 categories with fixed scales.</summary>
    private sealed class FacetPlot : IDisposable
    {
        private readonly Column<float> _y;

        public FacetPlot()
        {
            var names = Enumerable.Range(0, 1000).Select(i => i < 500 ? "a" : "b").ToArray();
            _y = Column.Wrap(Enumerable.Range(0, 1000).Select(i => MathF.Sin(i * 0.02f) * 10).ToArray());
            var table = DataTable.Create().Add("x", Column.Uniform(0, 1, 1000)).Add("y", _y).Add("k", Column.Categorical(names, ["a", "b"])).Build();
            Fixture = new EngineFixture();
            Spec = Plot.Create(table).Line(table.Col<double>("x"), table.Col<float>("y")).FacetWrap(table.Cat("k")).Build();
            Fixture.Engine.SetPlot(Spec);
            Fixture.Engine.RenderFrame();
        }

        public EngineFixture Fixture { get; }

        public PlotSpec Spec { get; }

        public void Dispose()
        {
            Fixture.Dispose();
            _y.Dispose();
        }
    }
}
