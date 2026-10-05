using System;
using ExpandedLib.Industry.Molten;
using SteelmakingExpanded;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Xunit;

namespace Integration.Tests.Pins;

/// <summary>
/// What a player expects of a cast on the published smex: a mold cast on a pedestal or under a tap
/// cools as the same mold poured by hand does, a lifted pedestal mold keeps that pace, and metal
/// standing in the canals keeps the slow rate.
/// </summary>
public class MoltenCastingTests {
  private const int Units = 200;
  private const float PourTemp = 1850f;

  #region Cooling

  // The hand pour is vanilla's tool mold on the ground taking the same steel at the temperature the
  // fitting's cast has on the tick its mold fills. Either is hardened below 30% of the melting point.
  // Returns the ticks from the fill to hardened for each; 0 for the fitting when it has not hardened
  // in 2400 ticks.
  private static (int byHand, int onFitting) Race(
    CanalLine line,
    Func<ItemStack?> cast,
    Func<bool> full
  ) {
    var world = line.Scene.World.World;
    var hand = MoltenClock.MoldOnTheGround(
      line.Scene.World,
      new BlockPos(2, 0, 0),
      MoltenClock.DoubleIngotMold()
    );
    int left = Units;
    int filled = 0;
    int byHand = 0;
    int onFitting = 0;
    for (int t = 1; t <= 2400 && onFitting == 0; t++) {
      if (left > 0)
        left -= MoltenClock.Pour(
          line.Scene.World,
          line[0],
          MoltenClock.Steel,
          left,
          PourTemp
        );
      line.Step();
      if (filled == 0) {
        if (!full())
          continue;
        filled = t;
        float temp = MoltenMetal.GetTemperature(world, cast()!);
        int amount = Units;
        hand.ReceiveLiquidMetal(
          MoltenMetal.CreateStack(world, MoltenClock.Steel, temp)!,
          ref amount,
          temp
        );
        continue;
      }
      if (byHand == 0 && hand.IsHardened)
        byHand = t - filled;
      if (MoltenMetal.IsHardened(world, cast()!))
        onFitting = t - filled;
    }
    Assert.Equal(Units, hand.FillLevel);
    return (byHand, onFitting);
  }

  private static CanalLine PedestalLine() {
    var line = new CanalLine("start", "pedestal");
    line.Pedestal.AddMold(new ItemStack(MoltenClock.DoubleIngotMold()));
    return line;
  }

  private static CanalLine TapLine() {
    var line = new CanalLine("start", "tap");
    line.Tap.AddMold(new ItemStack(MoltenClock.DoubleIngotMold()));
    line.Tap.TryTogglePouring();
    return line;
  }

  private static float CooldownSpeedOf(ItemStack stack) =>
    (stack.Attributes["temperature"] as ITreeAttribute)?.GetFloat(
      "cooldownSpeed"
    ) ?? 0f;

  // Fails while the pedestal stamps its cast with the canal rate of 24 per game hour: it hardens
  // in about 1409 ticks, against 113 by hand.
  [Fact]
  public void A_mold_cast_on_a_pedestal_hardens_no_later_than_the_same_mold_poured_by_hand() {
    var line = PedestalLine();

    var (byHand, onPedestal) = Race(
      line,
      () => line.Pedestal.MoldMetalContent,
      () => line.Pedestal.MoldCurrentUnits >= line.Pedestal.MoldMaxUnits
    );

    Assert.InRange(byHand, 1, 2400);
    Assert.InRange(onPedestal, 1, byHand);
  }

  // Fails while the tap stamps its cast with the canal rate of 24 per game hour.
  [Fact]
  public void A_mold_cast_under_a_tap_hardens_no_later_than_the_same_mold_poured_by_hand() {
    var line = TapLine();

    var (byHand, underTap) = Race(
      line,
      () => line.Tap.MoldMetalContent,
      () => line.Tap.MoldCurrentUnits >= line.Tap.MoldMaxUnits
    );

    Assert.InRange(byHand, 1, 2400);
    Assert.InRange(underTap, 1, byHand);
  }

  // Fails while the cast carries the canal rate of 24 into the lifted mold.
  [Fact]
  public void A_pedestal_mold_lifted_off_keeps_the_vanilla_rate() {
    var line = PedestalLine();
    for (int t = 0; t < 600 && line.Pedestal.MoldCurrentUnits < Units; t++) {
      MoltenClock.Pour(line.Scene.World, line[0], MoltenClock.Steel, Units, PourTemp);
      line.Step();
    }
    Assert.Equal(Units, line.Pedestal.MoldCurrentUnits);

    var lifted = line.Pedestal.RemoveMold();

    var (cast, units) = MoltenContents.Read(
      lifted,
      MoltenContents.MoldUnitsKey,
      line.Scene.World.World
    );
    Assert.Equal(Units, units);
    Assert.Equal(
      MoltenMetal.VanillaMoldCooldownSpeed * SmexValues.MoldPedestalCooldownCoefficient,
      CooldownSpeedOf(cast!)
    );
  }

  // Fails if the canal cells cool at the mold rate: their steel solidifies in about 17 ticks
  // instead of about 209, against about 113 for the hand-filled mold to harden.
  [Fact]
  public void Metal_standing_in_the_canals_cools_slower_than_a_hand_filled_mold() {
    var line = new CanalLine("start", "straight");
    var world = line.Scene.World.World;
    var hand = MoltenClock.MoldOnTheGround(
      line.Scene.World,
      new BlockPos(2, 0, 0),
      MoltenClock.DoubleIngotMold()
    );
    int amount = Units;
    hand.ReceiveLiquidMetal(
      MoltenMetal.CreateStack(world, MoltenClock.Steel, PourTemp)!,
      ref amount,
      PourTemp
    );
    int left = Units;
    int byHand = 0;
    int inCanal = 0;
    for (int t = 1; t <= 2400 && inCanal == 0; t++) {
      if (left > 0)
        left -= MoltenClock.Pour(
          line.Scene.World,
          line[0],
          MoltenClock.Steel,
          left,
          PourTemp
        );
      line.Step();
      if (byHand == 0 && hand.IsHardened)
        byHand = t;
      if (line.Cells.Exists(c => c.Solidified))
        inCanal = t;
    }

    Assert.InRange(byHand, 1, 2400);
    Assert.InRange(inCanal, byHand + 1, 2400);
  }

  #endregion
}
