using System.Diagnostics;
using System.Threading;
using HarmonyLib;
using NSubstitute;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Xunit;

namespace Integration.Tests.Coexistence;

/// <summary>
/// A Harmony patch on <c>Block.GetDrops</c> applied after <c>Block.SpawnDropsAndRemoveBlock</c> ran
/// hot for one block type still runs when that type is broken, as the closed line's drops postfix
/// must once earlier tests have broken thousands of blocks.
/// </summary>
/// <remarks>Meaningful only while nothing earlier in the process patched <c>Block.GetDrops</c> or
/// tiered up <c>SpawnDropsAndRemoveBlock</c>: run it alone.</remarks>
public class LatePatchTests {
  private class Warmed : Block { }

  private static int _ran;

  private static void Postfix() => Interlocked.Increment(ref _ran);

  // Fails when the test process runs with tiered PGO: SpawnDropsAndRemoveBlock tiers up with
  // Block.GetDrops inlined behind a guard on Warmed, and the postfix never runs for a Warmed block.
  [Fact]
  public void A_drops_patch_runs_where_the_break_tiered_up_before_it() {
    var world = Substitute.For<IWorldAccessor>();
    var api = Substitute.For<ICoreAPI>();
    api.World.Returns(world);
    world.Api.Returns(api);
    world.Side.Returns(EnumAppSide.Server);
    var block = new Warmed { Code = new AssetLocation("tests:warmed") };
    Traverse.Create(block).Field("api").SetValue(api);
    var pos = new BlockPos(0, 1, 0);
    var clock = Stopwatch.StartNew();
    while (clock.ElapsedMilliseconds < 3000) {
      for (int i = 0; i < 100; i++)
#if GAME_GE_1_22
        block.SpawnDropsAndRemoveBlock(world, pos, null, 1f);
#else
        block.OnBlockBroken(world, pos, null, 1f);
#endif
      Thread.Sleep(1);
    }

    var harmony = new Harmony("tests.latepatch");
    harmony.Patch(
      AccessTools.Method(
        typeof(Block),
        nameof(Block.GetDrops),
        [
          typeof(IWorldAccessor),
          typeof(BlockPos),
          typeof(IPlayer),
          typeof(float),
        ]
      ),
      postfix: new HarmonyMethod(typeof(LatePatchTests), nameof(Postfix))
    );
    try {
      _ran = 0;
#if GAME_GE_1_22
      block.SpawnDropsAndRemoveBlock(world, pos, null, 1f);
#else
      block.OnBlockBroken(world, pos, null, 1f);
#endif
      Assert.Equal(1, _ran);
    } finally {
      harmony.UnpatchAll("tests.latepatch");
    }
  }
}
