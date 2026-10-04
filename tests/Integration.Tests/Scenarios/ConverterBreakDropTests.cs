using System.Linq;
using ExpandedLib.Industry.Molten;
using ExpandedLib.Testing;
using SteelmakingExpanded.BlockStructures.Converter;
using SteelmakingExpanded.BlockStructures.Converter.BlockEntities;
using SteelmakingExpanded.BlockStructures.Converter.Blocks;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Xunit;

namespace SteelmakingExpanded.Tests;

/// <summary>
/// A converter broken over a charge that has gone solid hands the charge back as metal bits, however
/// the charge got cold: the production tick that latches the charge solid only runs while the whole
/// plant stands aligned, so a vessel broken while its plant is incomplete is judged on the charge's
/// own temperature.
/// </summary>
public class ConverterBreakDropTests {
  private const int Units = 600;
  private const float IronMelt = 1500f;

  // The break mangles up to two bits' worth of the charge.
  private static int MinBits(int units) =>
    (units - 2 * SmexValues.MoltenUnitsPerBit) / SmexValues.MoltenUnitsPerBit;

  private static int MaxBits(int units) => units / SmexValues.MoltenUnitsPerBit;

  private sealed class Plant {
    public readonly ConverterRig Rig = new();
    public readonly BlockPos VesselPos;
    public readonly BlockConverterBessemer VesselBlock;

    public Plant() {
      Rig.World.RegisterItem("game:metalbit-steel");
      Rig.World.Place(Rig.Control.Pos, Rig.Control.Block, Rig.Control);
      VesselPos = (BlockPos)
        ReflectionHelpers.Invoke(Rig.Control, "GetGlobalPos", 0, 0, 2)!;
      VesselBlock = TestBlocks.Configure(
        new BlockConverterBessemer(),
        "smex:converterbessemer-north",
        20,
        ("side", "north")
      );
      var vessel = new BlockEntityConverterBessemer {
        Pos = VesselPos.Copy(),
        Block = VesselBlock,
      };
      Rig.World.Place(VesselPos, VesselBlock, vessel);
      Rig.World.Attach(vessel);
      vessel.LinkControl(Rig.Control.Pos);
    }

    public BlockEntityConverterControl Control =>
      (BlockEntityConverterControl)Rig.World.GetBlockEntity(Rig.Control.Pos)!;

    public ItemStack? Content =>
      ReflectionHelpers.GetField(Control, "_content") as ItemStack;

    public Plant Charge(string code, int units, float temp) {
      ReflectionHelpers.SetField(
        Control,
        "_content",
        MoltenMetal.CreateStack(Rig.World.World, code, temp)
      );
      ReflectionHelpers.SetField(Control, "_contentUnits", units);
      return this;
    }

    public Plant Cool(float temp) {
      MoltenMetal.SetTemperature(Rig.World.World, Content!, temp);
      return this;
    }

    // A tick of the plant as it stands: the rig's vessel has no construction behaviour, so the tick
    // stops at its gates, as it does in a plant with a missing or misaligned part.
    public Plant Tick() {
      ReflectionHelpers.Invoke(Control, "OnProductionTick", 1f);
      return this;
    }

    public void Break() =>
      VesselBlock.OnBlockBroken(Rig.World.World, VesselPos, null);

    public int Bits(string code) =>
      Rig
        .World.Drops.Where(d => d.Collectible?.Code?.ToString() == code)
        .Sum(d => d.StackSize);
  }

  #region Break

  [Theory]
  [InlineData(ConverterOpState.Normal, false)]
  [InlineData(ConverterOpState.Normal, true)]
  [InlineData(ConverterOpState.Filling, false)]
  [InlineData(ConverterOpState.Pouring, true)]
  public void A_charge_that_cooled_while_the_plant_stood_incomplete_drops_its_bits(
    ConverterOpState state,
    bool fromFiller
  ) {
    var plant = new Plant()
      .Charge("game:ingot-iron", Units, 1700f)
      .Tick()
      .Cool(100f)
      .Tick();
    ReflectionHelpers.SetProperty(plant.Control, "OpState", state);

    if (fromFiller) {
      BlockPos cell = plant.VesselPos.AddCopy(0, 1, 0);
      plant.Rig.World.PlaceFiller(cell, principal: plant.VesselPos);
      plant.Rig.World.Filler.OnBlockBroken(plant.Rig.World.World, cell, null!);
    } else
      plant.Break();

    Assert.InRange(
      plant.Bits("game:metalbit-iron"),
      MinBits(Units),
      MaxBits(Units)
    );
    Assert.Null(plant.Content);
  }

  [Theory]
  [InlineData("game:ingot-iron", "game:metalbit-iron", 100f)]
  [InlineData("game:ingot-iron", "game:metalbit-iron", 1400f)]
  [InlineData("game:ingot-steel", "game:metalbit-steel", 100f)]
  [InlineData("game:ingot-steel", "game:metalbit-steel", 1400f)]
  public void Each_metal_drops_its_own_bits_hardened_or_part_cooled(
    string metal,
    string bit,
    float temp
  ) {
    var plant = new Plant().Charge(metal, Units, 1700f).Tick().Cool(temp);

    plant.Break();

    Assert.InRange(plant.Bits(bit), MinBits(Units), MaxBits(Units));
  }

  [Theory]
  [InlineData(1)]
  [InlineData(4)]
  [InlineData(7)]
  public void A_charge_below_one_bit_still_drops_one(int units) {
    var plant = new Plant()
      .Charge("game:ingot-iron", units, 1700f)
      .Tick()
      .Cool(100f);

    plant.Break();

    Assert.Equal(1, plant.Bits("game:metalbit-iron"));
  }

  [Fact]
  public void A_charge_still_molten_when_broken_drops_no_bits() {
    var plant = new Plant().Charge("game:ingot-iron", Units, 1700f).Tick();

    plant.Break();

    Assert.Equal(0, plant.Bits("game:metalbit-iron"));
  }

  [Fact]
  public void A_charge_saved_molten_and_cooled_after_the_reload_drops_its_bits() {
    var plant = new Plant().Charge("game:ingot-iron", Units, 1700f).Tick();
    plant.Rig.World.Reload(plant.Rig.Control.Pos);
    plant.Cool(100f).Tick();

    plant.Break();

    Assert.InRange(
      plant.Bits("game:metalbit-iron"),
      MinBits(Units),
      MaxBits(Units)
    );
  }

  #endregion

  #region Chisel

  [Fact]
  public void A_small_residue_that_hardened_while_the_plant_stood_incomplete_can_be_chiselled() {
    int residue = (int)(
      SmexValues.BessemerChiselMaxFraction
      * SmexValues.BessemerConverterCapacity
      / 2
    );
    var plant = new Plant()
      .Charge("game:ingot-iron", residue, 1700f)
      .Tick()
      .Cool(100f)
      .Tick();

    ItemStack? chipped = plant.Control.ChiselOutContent();

    Assert.Equal("game:metalbit-iron", chipped?.Collectible.Code.ToString());
    Assert.Equal(residue / SmexValues.MoltenUnitsPerBit, chipped!.StackSize);
  }

  #endregion

  #region Explosion

  [Fact]
  public void A_solid_charge_blown_up_with_the_vessel_drops_its_bits() {
    var plant = new Plant()
      .Charge("game:ingot-iron", Units, 1700f)
      .Tick()
      .Cool(100f);

    plant.VesselBlock.OnBlockExploded(
      plant.Rig.World.World,
      plant.VesselPos,
      plant.VesselPos.AddCopy(3, 0, 0),
      EnumBlastType.RockBlast,
      null
    );

    Assert.InRange(
      plant.Bits("game:metalbit-iron"),
      MinBits(Units),
      MaxBits(Units)
    );
    Assert.Null(plant.Content);
  }

  #endregion
}
