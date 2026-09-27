using System;
using System.IO;
using System.Linq;
using System.Text;

using AwesomeAssertions;

using Homespool.Host.PrintFiles.GCode;

namespace Homespool.Host.Test;

/// <summary>
/// <see cref="PlateLayout"/> - the slicer's <c>objects_info</c> and <c>bed_shape</c> headers, read
/// with limits because firmware relays them without reading them.
/// </summary>
/// <remarks>
/// <b>Position is identity</b>, so most of what is tested here is that nothing ever shifts it: an
/// object that fails to read keeps its place with no outline, and a plate too large to read is not
/// read at all rather than read in part.
/// </remarks>
public class PlateLayoutTests
{
    /// <summary>
    /// Six 25 mm cubes at the six places the two-plate experiment used, declared in plate 1's
    /// scrambled order - F, B, E, C, A, D - so no ordering but the declared one reproduces the ids.
    /// </summary>
    private static readonly string SixCubes = ObjectsInfo(
        ("F", 30.0, 156.4), ("B", 181.7, 147.8), ("E", 66.3, 62.2),
        ("C", 136.5, 57.9), ("A", 206.7, 53.6), ("D", 100.2, 152.1));

    private static string ObjectsInfo(params (string name, double x, double y)[] cubes)
    {
        StringBuilder json = new("""{"objects":[""");

        for (int i = 0; i < cubes.Length; i++)
        {
            (string name, double x, double y) = cubes[i];

            json.Append(i == 0 ? string.Empty : ",")
                .Append(System.Globalization.CultureInfo.InvariantCulture,
                        $$"""{"name":"{{name}}","polygon":[[{{x + 12.5}},{{y + 12.5}}],[{{x - 12.5}},{{y + 12.5}}],[{{x - 12.5}},{{y - 12.5}}],[{{x + 12.5}},{{y - 12.5}}]]}""");
        }

        return json.Append("]}").ToString();
    }

    /// <summary>
    /// Every object keeps the id its position gives it - the join the printer's ids depend on, and
    /// the one both hardware experiments confirmed.
    /// </summary>
    [Fact]
    public void ObjectsAreNumberedByTheirPositionInTheHeader()
    {
        PlateLayout layout = PlateLayout.Parse(SixCubes, "0x0,250x0,250x210,0x210")!;

        layout.Objects.Select(o => (o.Id, o.Name)).Should().Equal(
            (0, "F"), (1, "B"), (2, "E"), (3, "C"), (4, "A"), (5, "D"));
        layout.Objects[4].Outline.Should().HaveCount(4);
    }

    /// <summary>The bed's extents come from the file's own corners.</summary>
    [Fact]
    public void TheBedIsTheBoundsOfItsCorners()
    {
        PlateLayout.ParseBed("0x0,250x0,250x210,0x210").Should().Be(new PlateBounds(0, 0, 250, 210));
    }

    /// <summary>
    /// The headers as PrusaSlicer actually writes them, read out of the tracked MK3.5 fixture rather
    /// than typed out here.
    /// </summary>
    [Fact]
    public void TheHeadersOfARealExportRead()
    {
        string[] lines = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "metadata-mk35-04-pla.gcode"));
        string Header(string key)
        {
            return lines.Single(line => line.StartsWith("; " + key + " = ", StringComparison.Ordinal))
                        .Substring(key.Length + 5);
        }

        PlateLayout layout = PlateLayout.Parse(Header("objects_info"), Header("bed_shape"))!;

        layout.Objects.Should().ContainSingle().Which.Name.Should().Be("cube.stl");
        layout.Bed.Should().Be(new PlateBounds(0, 0, 250, 210));
    }

    /// <summary>
    /// A <c>.bgcode</c> relays no bed shape, and the plate still reads - the drawing fits itself to
    /// the objects instead.
    /// </summary>
    [Fact]
    public void APlateReadsWithoutABed()
    {
        PlateLayout layout = PlateLayout.Parse(SixCubes, bedShape: null)!;

        layout.Objects.Should().HaveCount(6);
        layout.Bed.Should().BeNull();
    }

    /// <summary>
    /// A name taken from a file name arrives percent-encoded, as the Core One capture shows, and is
    /// shown as the name somebody gave it.
    /// </summary>
    [Fact]
    public void APercentEncodedNameIsDecoded()
    {
        PlateLayout layout = PlateLayout.Parse("""{"objects":[{"name":"GF%20dremel%20bit%20storage.stl","polygon":[]}]}""", null)!;

        layout.Objects[0].Name.Should().Be("GF dremel bit storage.stl");
    }

    /// <summary>
    /// <b>A broken object keeps its place.</b> Dropping it would shift every later object onto its
    /// neighbour's id, and a cancel pressed beside the name would stop a different part.
    /// </summary>
    [Theory]
    [InlineData("""{"name":"B","polygon":[[1,1],[2,2]]}""")]
    [InlineData("""{"name":"B","polygon":[[1,1],[2,"x"],[3,3]]}""")]
    [InlineData("""{"name":"B","polygon":[[1,1],[2,2,2],[3,3]]}""")]
    [InlineData("""{"name":"B","polygon":[[1,1],[2,1e308],[3,3]]}""")]
    [InlineData("""{"name":"B"}""")]
    [InlineData("""7""")]
    public void AnObjectThatWillNotDrawKeepsItsPlace(string broken)
    {
        string json = $$"""{"objects":[{"name":"A","polygon":[[0,0],[1,0],[1,1]]},{{broken}},{"name":"C","polygon":[[0,0],[1,0],[1,1]]}]}""";

        PlateLayout layout = PlateLayout.Parse(json, null)!;

        layout.Objects.Should().HaveCount(3);
        layout.Objects[1].Outline.Should().BeEmpty();
        layout.Objects[2].Name.Should().Be("C", "the object after the broken one is still object 2");
    }

    /// <summary>A name that is not something to show is read as no name, never as a shortened one.</summary>
    [Theory]
    [InlineData("""{"name":"   "}""")]
    [InlineData("""{"name":"line\nbreak"}""")]
    [InlineData("""{"name":42}""")]
    [InlineData("""{}""")]
    public void ANameNotWorthShowingIsNone(string entry)
    {
        PlateLayout.Parse($$"""{"objects":[{{entry}}]}""", null)!.Objects[0].Name.Should().BeNull();
    }

    /// <summary>An over-long name is read as unnamed rather than cut short into a claim the file did not make.</summary>
    [Fact]
    public void AnOverLongNameIsNone()
    {
        string name = new('x', PlateLayout.MaxNameLength + 1);

        PlateLayout.Parse($$"""{"objects":[{"name":"{{name}}"}]}""", null)!.Objects[0].Name.Should().BeNull();
    }

    /// <summary>
    /// Anything that is not a plate description at all is no layout, so the page lists the objects
    /// by number as the printer's menu does.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("""{"objects":{}}""")]
    [InlineData("""{"things":[]}""")]
    [InlineData("""[]""")]
    public void SomethingThatIsNotAPlateIsNoLayout(string? objectsInfo)
    {
        PlateLayout.Parse(objectsInfo, "0x0,250x0,250x210,0x210").Should().BeNull();
    }

    /// <summary>
    /// <b>A plate past the limits is not read in part.</b> Past the object ceiling or the total point
    /// budget there is no honest subset to show, since a subset would still have to be positional.
    /// </summary>
    [Fact]
    public void APlatePastTheLimitsIsNoLayout()
    {
        string tooMany = "{\"objects\":[" + string.Join(',', Enumerable.Repeat("{}", PlateLayout.MaxObjects + 1)) + "]}";

        string corners = string.Join(',', Enumerable.Repeat("[1,1]", PlateLayout.MaxOutlinePoints));
        string tooManyPoints = "{\"objects\":[" +
                               string.Join(',', Enumerable.Repeat($$"""{"polygon":[{{corners}}]}""", (PlateLayout.MaxTotalPoints / PlateLayout.MaxOutlinePoints) + 1)) +
                               "]}";

        PlateLayout.Parse(tooMany, null).Should().BeNull();
        PlateLayout.Parse(tooManyPoints, null).Should().BeNull();
    }

    /// <summary>
    /// A bed shape that does not describe a bed is no bed, and costs only the bed - the objects still
    /// read.
    /// </summary>
    [Theory]
    [InlineData("0x0,250x0")]
    [InlineData("0x0,250x0,250xabc,0x210")]
    [InlineData("0x0,0x0,0x0,0x0")]
    [InlineData("0x0,99999x0,99999x210,0x210")]
    [InlineData("250,0,250,210")]
    public void ABedThatIsNotABedIsNone(string bedShape)
    {
        PlateLayout layout = PlateLayout.Parse(SixCubes, bedShape)!;

        layout.Bed.Should().BeNull();
        layout.Objects.Should().HaveCount(6);
    }
}
