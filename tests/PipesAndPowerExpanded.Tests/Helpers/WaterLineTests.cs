using ExpandedLib;
using ExpandedLib.Industry.Pipes;
using ExpandedLib.Testing;
using PipesAndPowerExpanded.BlockNetworkPipe.BlockEntities;
using PipesAndPowerExpanded.Helpers;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Xunit;

namespace PipesAndPowerExpanded.Tests;

/// <summary>
/// What a water run reads from the machines that fed it: held at the highest head among them when
/// any left it brim-full, else its fill, and nothing from before its pipes changed.
/// </summary>
public class WaterLineTests {
  private static (TestWorld World, PipeNetwork Line) Rig() {
    var world = new TestWorld();
    world.RegisterNetwork("pipe", sys => new PipeNetwork(sys));
    var pipe = PipeTestWorld.MakePipe(orientation: "ns");
    var rock = TestBlocks.Configure(new Block(), "game:rock", 99);
    world.Place(new BlockPos(0, 0, -1), rock);
    world.Place(new BlockPos(0, 0, 2), rock);
    for (int z = 0; z <= 1; z++) {
      world.Place(new BlockPos(0, 0, z), pipe);
      world.AddNode(new BlockPos(0, 0, z), "pipe");
    }
    return (world, (PipeNetwork)world.NetworkAt(new BlockPos(0, 0, 0))!);
  }

  /// <summary>A machine off the line, in the world, to record feeds under.</summary>
  private static BlockEntity Machine(TestWorld world, int x) {
    var machine = new BlockEntitySteamCondenser();
    world.Place(new BlockPos(x, 5, 5), new Block(), machine);
    world.Attach(machine);
    return machine;
  }

  private static void Fill(TestWorld world, PipeNetwork line, float litres) =>
    line.TryProduceLiquid(litres, 20f, 0f, world.Accessor);

  private static float Brim(PipeNetwork line) =>
    line.Nodes.Count * ExlibValues.LitresPerPipe;

  // Fails when only a feed that left the line brim-full counts toward its head (the head taken
  // over every feed in WaterLine.Head): the line would read the second pump's 1.5 atm.
  [Fact]
  public void A_line_one_feed_brims_is_held_at_the_highest_head_that_fed_it() {
    var (world, line) = Rig();
    Fill(world, line, Brim(line) / 2f);
    WaterLine.Hold(Machine(world, 10), line, 3f);
    Fill(world, line, Brim(line));
    WaterLine.Hold(Machine(world, 11), line, 1.5f);

    Assert.Equal(3f, WaterLine.Head(line), 3);
  }

  // Fails when a line is held whether or not a feed left it brim-full (the brim test in
  // WaterLine.Head): the line would read the pump's 3 atm.
  [Fact]
  public void A_line_no_feed_brims_reads_its_fill() {
    var (world, line) = Rig();
    Fill(world, line, Brim(line) / 2f);
    WaterLine.Hold(Machine(world, 10), line, 3f);

    Assert.Equal(0.5f, WaterLine.Head(line), 3);
  }

  // Fails when a line keeps its records once its pipes change (WaterLine.Current): the line,
  // now short of its brim, would still read the pump's 3 atm.
  [Fact]
  public void A_line_that_gains_a_pipe_reads_its_fill_until_fed_again() {
    var (world, line) = Rig();
    Fill(world, line, Brim(line));
    WaterLine.Hold(Machine(world, 10), line, 3f);
    Assert.Equal(3f, WaterLine.Head(line), 3);

    world.Place(
      new BlockPos(0, 0, 2),
      PipeTestWorld.MakePipe(orientation: "ns")
    );
    world.Place(
      new BlockPos(0, 0, 3),
      TestBlocks.Configure(new Block(), "game:rock", 99)
    );
    world.AddNode(new BlockPos(0, 0, 2), "pipe");

    Assert.Same(line, world.NetworkAt(new BlockPos(0, 0, 2)));
    Assert.Equal(2f / 3f, WaterLine.Head(line), 3);
  }
}
