namespace HyperPlot.Tests;

public sealed class DataTableTests
{
    /// <summary>Col resolves a name to a typed reference after checking the column's type (design §3.1).</summary>
    /// <remarks>
    /// Preconditions:
    /// - A table of a Column&lt;float&gt; "y", a UniformColumn "t", and a Column&lt;int&gt; "n", 3 elements each.
    ///
    /// Steps:
    /// - Resolve "y" as float, "t" as double, "n" as int; then "y" as double, "t" as float, "n" as long, "Y", "missing", and null.
    ///
    /// Expected:
    /// - The three references name their positions and columns; the others throw ArgumentException whose message names the
    ///   column and its kind (or that no column has the name), and null throws ArgumentNullException.
    /// </remarks>
    [Fact]
    public void Col_TypeAndName_ReturnsReferenceOrThrows()
    {
        var y = Column.Wrap(new float[3]);
        var t = Column.Uniform(0, 1, 3);
        var n = Column.Wrap(new int[3]);
        var table = DataTable.Create().Add("y", y).Add("t", t).Add("n", n).Build();

        var yRef = table.Col<float>("y");
        var tRef = table.Col<double>("t");
        var nRef = table.Col<int>("n");

        Assert.Equal((0, 1, 2), (yRef.Index, tRef.Index, nRef.Index));
        Assert.Same(y, yRef.Column);
        Assert.Same(t, tRef.Column);
        Assert.Same(n, nRef.Column);
        Assert.Same(table, yRef.Table);
        Assert.Contains(
            "\"y\" is a Column<Single>",
            Assert.Throws<ArgumentException>(() => table.Col<double>("y")).Message,
            StringComparison.Ordinal
        );
        Assert.Contains("\"t\" is a UniformColumn", Assert.Throws<ArgumentException>(() => table.Col<float>("t")).Message, StringComparison.Ordinal);
        Assert.Contains("\"n\" is a Column<Int32>", Assert.Throws<ArgumentException>(() => table.Col<long>("n")).Message, StringComparison.Ordinal);
        Assert.Contains("no column named \"Y\"", Assert.Throws<ArgumentException>(() => table.Col<float>("Y")).Message, StringComparison.Ordinal);
        Assert.Contains("no column named", Assert.Throws<ArgumentException>(() => table.Col<float>("missing")).Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentNullException>(() => table.Col<float>(null!));
    }

    /// <summary>The table keeps its columns in order with their names and length; an empty table has length 0.</summary>
    /// <remarks>
    /// Preconditions:
    /// - Two columns of 5 elements; an empty builder.
    ///
    /// Steps:
    /// - Build both tables and read their members; read out-of-range indices.
    ///
    /// Expected:
    /// - Length 5, 2 columns, names and columns in order; the empty table has length 0 and no column; index -1 and 2 throw
    ///   ArgumentOutOfRangeException.
    /// </remarks>
    [Fact]
    public void Build_Columns_KeepsThemInOrder()
    {
        var a = Column.Wrap(new double[5]);
        var b = Column.Uniform(1, 2, 5);

        var table = DataTable.Create().Add("a", a).Add("b", b).Build();
        var empty = DataTable.Create().Build();

        Assert.Equal((5L, 2), (table.Length, table.ColumnCount));
        Assert.Equal(("a", "b"), (table.GetName(0), table.GetName(1)));
        Assert.Same(a, table.GetColumn(0));
        Assert.Same(b, table.GetColumn(1));
        Assert.Equal((0L, 0), (empty.Length, empty.ColumnCount));
        Assert.Throws<ArgumentOutOfRangeException>(() => table.GetName(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => table.GetColumn(2));
    }

    /// <summary>Add and Build refuse what design §3.1 refuses.</summary>
    /// <remarks>
    /// Preconditions:
    /// - Columns of 3 and 4 elements.
    ///
    /// Steps:
    /// - Add a null name, a null column, an empty name, a name twice; build columns of two lengths; use a builder after Build.
    ///
    /// Expected:
    /// - ArgumentNullException, ArgumentNullException, ArgumentException, ArgumentException (naming the name), ArgumentException
    ///   naming both columns and their lengths, and InvalidOperationException for Add and Build after Build.
    /// </remarks>
    [Fact]
    public void AddAndBuild_InvalidInput_Throw()
    {
        var three = Column.Wrap(new float[3]);
        var four = Column.Wrap(new float[4]);

        Assert.Throws<ArgumentNullException>(() => DataTable.Create().Add(null!, three));
        Assert.Throws<ArgumentNullException>(() => DataTable.Create().Add("a", null!));
        Assert.Throws<ArgumentException>(() => DataTable.Create().Add(string.Empty, three));
        Assert.Contains(
            "\"a\"",
            Assert.Throws<ArgumentException>(() => DataTable.Create().Add("a", three).Add("a", four)).Message,
            StringComparison.Ordinal
        );
        var lengths = Assert.Throws<ArgumentException>(() => DataTable.Create().Add("a", three).Add("b", four).Build());
        Assert.Contains("\"b\" has 4 elements and \"a\" has 3", lengths.Message, StringComparison.Ordinal);

        var builder = DataTable.Create().Add("a", three);
        _ = builder.Build();
        Assert.Throws<InvalidOperationException>(() => builder.Add("b", four));
        Assert.Throws<InvalidOperationException>(builder.Build);
    }

    /// <summary>References are equal when they name the same position of the same table; the default one names none.</summary>
    /// <remarks>
    /// Preconditions:
    /// - Two tables over the same two columns.
    ///
    /// Steps:
    /// - Compare references to the same name twice, to the other name, to the same name of the other table; convert typed
    ///   references; read the default reference.
    ///
    /// Expected:
    /// - Equal (with == and the same hash) for the same table and position; not equal (!=) otherwise; the typed and untyped
    ///   forms agree; the default reference throws InvalidOperationException for Table and Column, and differs from the others.
    /// </remarks>
    [Fact]
    public void ColumnRef_Equality_IsTableAndPosition()
    {
        var a = Column.Wrap(new float[2]);
        var b = Column.Wrap(new float[2]);
        var first = DataTable.Create().Add("a", a).Add("b", b).Build();
        var second = DataTable.Create().Add("a", a).Add("b", b).Build();

        var same = first.Col<float>("a");
        var again = first.Col<float>("a");

        Assert.True(same == again);
        Assert.True(same.Equals((object)again));
        Assert.Equal(same.GetHashCode(), again.GetHashCode());
        Assert.True(same != first.Col<float>("b"));
        Assert.True(same != second.Col<float>("a"));
        ColumnRef untyped = same;
        Assert.True(untyped == again.ToColumnRef());
        Assert.Equal(untyped.GetHashCode(), same.GetHashCode());
        Assert.True(untyped != second.Col<float>("a").ToColumnRef());
        Assert.False(untyped.Equals((object)same));

        var none = default(ColumnRef);
        Assert.Throws<InvalidOperationException>(() => none.Table);
        Assert.Throws<InvalidOperationException>(() => none.Column);
        Assert.Throws<InvalidOperationException>(() => default(ColumnRef<float>).Table);
        Assert.True(none != untyped);
        Assert.True(none == default);
    }
}
