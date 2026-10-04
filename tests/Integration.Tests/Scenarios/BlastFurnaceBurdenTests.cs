using System;
using HarmonyLib;
using SteelmakingExpanded;
using SteelmakingExpanded.BlockStructures.BlastFurnace;
using SteelmakingExpanded.Patches;
using Xunit;
using Xunit.Abstractions;

namespace SteelmakingExpanded.Tests;

/// <summary>
/// Where a lit hearth's burden goes in each state the furnace can sit in, with the game clock
/// running and the hearth piles' own burn tick firing as it does in game. The coal pile patches are
/// applied under a test-only Harmony id and removed on dispose.
/// </summary>
public sealed class BlastFurnaceBurdenTests : IDisposable {
  private const string HarmonyId = "legacytest.burdenpiles";
  private const int Piles = 50;
  private const int PileSize = 16;

  private readonly Harmony _harmony = new(HarmonyId);
  private readonly ITestOutputHelper _out;

  public BlastFurnaceBurdenTests(ITestOutputHelper output) {
    _out = output;
    _harmony.CreateClassProcessor(typeof(CoalPileBurdenPatches)).Patch();
  }

  public void Dispose() => _harmony.UnpatchAll(HarmonyId);

  private static BlastFurnaceRig Charged() =>
    new(burden: Piles * PileSize, pileSize: PileSize);

  private (int Lost, float Iron, float Slag) Run(
    BlastFurnaceRig rig,
    int seconds,
    Action<BlastFurnaceRig>? eachSecond = null
  ) {
    int before = rig.HearthBurden;
    float ironBefore = rig.MoltenIron;
    float slagBefore = rig.MoltenSlag;
    rig.RunOnGameClock(seconds, eachSecond);
    var result = (
      before - rig.HearthBurden,
      rig.MoltenIron - ironBefore,
      rig.MoltenSlag - slagBefore
    );
    _out.WriteLine(
      $"{seconds} s, {rig.State}: burden {before} -> {rig.HearthBurden}, iron +{result.Item2}, slag +{result.Item3}, piles {rig.PileCount}"
    );
    return result;
  }

  private static int MeltedBurden(float iron) =>
    (int)Math.Round(
      iron / SmexValues.BfIronPerMeltCycle * SmexValues.BfBurdenPerMeltCycle
    );

  [Fact]
  public void A_melting_furnace_spends_burden_on_its_melt_cycles_and_its_piles_burn() {
    var rig = Charged()
      .FeedBlast(20f)
      .SetState(BlastFurnaceState.Melting)
      .SetTemp(SmexValues.BfNaturalMaxTemp);

    var (lost, iron, _) = Run(rig, 300);

    Assert.Equal(BlastFurnaceState.Melting, rig.State);
    Assert.True(iron > 0f);
    Assert.True(lost > MeltedBurden(iron));
  }

  [Fact]
  public void A_furnace_stalled_on_a_full_reservoir_loses_burden_to_its_piles() {
    var rig = Charged()
      .FeedBlast(20f)
      .SetState(BlastFurnaceState.Melting)
      .SetTemp(SmexValues.BfNaturalMaxTemp)
      .SetMoltenIron(SmexValues.BfMaxMoltenIron);

    var (lost, iron, slag) = Run(rig, 600);

    Assert.Equal(BlastFurnaceState.Melting, rig.State);
    Assert.Equal(0f, iron);
    Assert.Equal(0f, slag);
    Assert.Equal(2 * Piles, lost);
  }

  [Fact]
  public void A_furnace_stalled_on_a_blocked_flue_loses_burden_to_its_piles() {
    var rig = Charged()
      .WithBlockedExhaust()
      .FeedBlast(20f)
      .SetState(BlastFurnaceState.Melting)
      .SetTemp(SmexValues.BfNaturalMaxTemp);

    var (lost, iron, slag) = Run(rig, 600);

    Assert.True(rig.Furnace.IsChoked);
    Assert.Equal(BlastFurnaceState.Melting, rig.State);
    Assert.Equal(0f, iron);
    Assert.Equal(0f, slag);
    Assert.Equal(2 * Piles, lost);
  }

  [Fact]
  public void A_furnace_without_blast_goes_out_and_keeps_its_burden() {
    var rig = Charged()
      .CutBlast()
      .SetState(BlastFurnaceState.Melting)
      .SetTemp(SmexValues.BfNaturalMaxTemp);

    var (lost, iron, _) = Run(rig, 600);

    Assert.Equal(BlastFurnaceState.Idle, rig.State);
    Assert.False(rig.HearthAlight);
    Assert.Equal(0f, iron);
    Assert.Equal(0, lost);
  }

  [Fact]
  public void A_furnace_lit_below_the_melting_point_loses_burden_to_its_piles() {
    var rig = Charged()
      .FeedBlast(20f)
      .SetState(BlastFurnaceState.Firing)
      .SetTemp(1200f);

    var (lost, iron, _) = Run(rig, 300, r => r.SetTemp(1200f));

    Assert.Equal(BlastFurnaceState.Firing, rig.State);
    Assert.Equal(0f, iron);
    Assert.Equal(Piles, lost);
  }
}
