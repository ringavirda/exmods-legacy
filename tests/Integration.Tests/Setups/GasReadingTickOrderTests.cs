using System;
using ExpandedLib;
using ExpandedLib.Industry.Pipes;
using ExpandedLib.Testing;
using PipesAndPowerExpanded;
using PipesAndPowerExpanded.BlockNetworkPipe.BlockEntities;
using PipesAndPowerExpanded.Helpers;
using PipesAndPowerExpanded.Tests;
using SteelmakingExpanded;
using SteelmakingExpanded.BlockStructures.Converter.BlockEntities;
using SteelmakingExpanded.BlockStructures.SmokeStack.BlockEntities;
using SteelmakingExpanded.Tests;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Xunit;
using BoilerState = PipesAndPowerExpanded.BlockStructures.Boiler.BlockEntityBoiler.BoilerState;

namespace Integration.Tests.Setups;

/// <summary>
/// Every machine that decides by a gas main's pressure reads it as the main's last network tick
/// settled it, so another machine on the same main changing it earlier in the same second changes
/// nothing the reader sees or does. Slots spell one second: R the reading machine, O another machine
/// on its main, in the order they tick.
/// </summary>
public class GasReadingTickOrderTests {
  #region Fixtures

  public static TheoryData<string> Slots() => new() { "RO", "OR" };

  /// <summary>Gives <paramref name="run"/> <paramref name="atm"/> of <paramref name="medium"/>,
  /// settled as a network tick would leave it.</summary>
  private static void Settle(
    PipeNetwork run,
    string medium,
    float atm,
    float temperature = 20f
  ) {
    float max = run.Nodes.Count * ExlibValues.LitresPerPipe;
    run.RestoreState(
      new PipeNetworkState {
        Volume = atm * max,
        MaxVolume = max,
        MediumType = medium,
        Temperature = temperature,
        Pressure = atm,
      }
    );
  }

  private static float Max(PipeNetwork run) =>
    run.Nodes.Count * ExlibValues.LitresPerPipe;

  /// <summary>Runs one second of <paramref name="slots"/>.</summary>
  private static void Second(string slots, Action reader, Action other) {
    foreach (char slot in slots)
      (slot == 'R' ? reader : other)();
  }

  private static void ProductionTick(BlockEntity be) =>
    ReflectionHelpers.Invoke(be, "OnProductionTick", 1f);

  #endregion

  #region Steam

  /// <summary>A regulated engine plant whose main stands settled above the valve's gate.</summary>
  private static (Scene, RegulatedEnginePlant, PipeNetwork) EnginePlant() {
    var scene = new Scene().Network("pipe", s => new PipeNetwork(s));
    var enginePos = new BlockPos(0, 8, 0);
    var plant = new RegulatedEnginePlant(scene, enginePos, gateAtm: 2.5f);
    scene.Build();
    PipeNetwork main = scene.NetworkAt<PipeNetwork>(
      enginePos.AddCopy(plant.EngineBlock.SteamInletFace)
    )!;
    Settle(main, "Steam", 3f, 150f);
    return (scene, plant, main);
  }

  // The engine ahead of or behind the main's relief valve, the network tick after both. Fails in VE
  // when the engine reads the inlet's live pressure (inlet.State.Pressure in
  // BlockEntityEngine.OnProductionTick): the valve has vented the main down first.
  [Theory]
  [InlineData("EV")]
  [InlineData("VE")]
  public void An_engine_reads_its_inlet_settled_in_every_tick_order(
    string slots
  ) {
    var (scene, plant, main) = EnginePlant();
    TickSlots.Order(
      scene.World,
      slots == "EV"
        ? new BlockEntity[] { plant.Engine, plant.Valve }
        : new BlockEntity[] { plant.Valve, plant.Engine }
    );

    TickSlots.Step(scene.World, int.MaxValue);

    Assert.Equal(3f, plant.InletPressure, 3);
    Assert.True(plant.Engine.IsRunning, $"{slots}: the engine stood still");
  }

  // Fails in EV when the valve judges its input on the run's live volume (inState.Volume in
  // BlockEntityPressureValve.OverflowGas): the engine has drawn the main down first and the valve
  // vents less.
  [Theory]
  [InlineData("EV")]
  [InlineData("VE")]
  public void A_relief_valve_vents_a_gas_main_settled_in_every_tick_order(
    string slots
  ) {
    var (scene, plant, main) = EnginePlant();
    float mainMax = Max(main);
    TickSlots.Order(
      scene.World,
      slots == "EV"
        ? new BlockEntity[] { plant.Engine, plant.Valve }
        : new BlockEntity[] { plant.Valve, plant.Engine }
    );

    TickSlots.Step(scene.World, int.MaxValue);

    Assert.Equal((3f - 2.5f) * mainMax, plant.DrainVolume, 2);
  }

  // The main stands under the engage pressure by less than the engine's own draw over the run's
  // capacity. Fails when a running engine judges the settled figure alone (pressure >= EngagePressure
  // in BlockEntityEngine.OnProductionTick): it stops on the dip its own steam made.
  [Fact]
  public void A_running_engine_stays_engaged_while_its_own_draw_holds_the_main_under_the_engage_pressure() {
    var (_, plant, main) = EnginePlant();
    ProductionTick(plant.Engine);
    Assert.True(plant.Engine.IsRunning, "the engine never started");
    float engage = PpexValues.WattEngineEngagePressure;
    float own = PpexValues.WattEngineSteamRate / Max(main);
    Settle(main, "Steam", engage - 0.5f * own, 150f);

    ProductionTick(plant.Engine);

    Assert.Equal(engage - 0.5f * own, plant.InletPressure, 3);
    Assert.True(plant.Engine.IsRunning, "the engine stopped on its own dip");
  }

  // Fails when the engage figure is lowered for an engine that is not running (the _running guard on
  // the own draw in BlockEntityEngine.OnProductionTick): an idle engine starts under the engage pressure.
  [Fact]
  public void An_idle_engine_still_needs_the_full_engage_pressure() {
    var (_, plant, main) = EnginePlant();
    float engage = PpexValues.WattEngineEngagePressure;
    float own = PpexValues.WattEngineSteamRate / Max(main);
    Settle(main, "Steam", engage - 0.5f * own, 150f);

    ProductionTick(plant.Engine);

    Assert.False(plant.Engine.IsRunning, "the engine started under its engage pressure");
  }

  /// <summary>A fired boiler with a chimney on its exhaust outlet, primed to boil.</summary>
  private static (BoilerFixture, PipeNetwork) ChimneyedBoiler() {
    var scene = new Scene().Network("pipe", s => new PipeNetwork(s));
    var boiler = new BoilerFixture(scene, new BlockPos(0, 8, 0));
    BlockPos outlet = boiler.Block.ExhaustOutletWorldPos(boiler.Be.Pos);
    var outletBe = new BlockEntityPipeOutlet();
    scene.World.Place(outlet, PpexScenes.UpOutlet(96), outletBe);
    scene.World.Initialize(outletBe);
    scene.Block(outlet.UpCopy(), PpexScenes.Chimney(97));
    scene.Build();
    boiler.Prime(BoilerState.Boiling, water: 600f, steam: 0f);
    return (boiler, scene.NetworkAt<PipeNetwork>(outlet)!);
  }

  // O is a second boiler on the same flue filling it to the vent cap. Fails in OR when the boiler
  // reads its exhaust run's live pressure (exhaustNet.State.Pressure in BlockEntityBoiler): the fire
  // finds the flue backed up and goes out.
  [Theory]
  [MemberData(nameof(Slots))]
  public void A_boiler_reads_its_flue_settled_in_every_tick_order(string slots) {
    var (boiler, flue) = ChimneyedBoiler();
    float cap = PpexValues.ExhaustMaxOutputPressure;
    Settle(flue, "Exhaust", cap - 0.25f, 300f);

    Second(
      slots,
      () => ProductionTick(boiler.Be),
      () =>
        flue.TryProduceGas(
          Max(flue),
          300f,
          "Exhaust",
          boiler.Be.Api.World.BlockAccessor,
          maxOutputPressure: cap
        )
    );

    Assert.True(
      (bool)ReflectionHelpers.GetField(boiler.Be, "_burning")!,
      $"{slots}: the fire went out"
    );
  }

  #endregion

  #region Blast and exhaust

  // O is a second converter drawing the same blast main. Fails in OR when the converter reads the
  // intake's live pressure (pipeNet.State.Pressure in BlockEntityConverterControl.BlastNetwork).
  [Theory]
  [MemberData(nameof(Slots))]
  public void A_converter_reads_its_blast_settled_in_every_tick_order(
    string slots
  ) {
    var rig = new ConverterRig();
    var blast = (PipeNetwork)ReflectionHelpers.GetField(rig, "_blast")!;
    float atm = SmexValues.BlastPressureThreshold + 0.25f;
    Settle(blast, "Air", atm);
    float reading = -1f;
    object? receiving = null;

    Second(
      slots,
      () => {
        reading = (float)
          ReflectionHelpers.Invoke(rig.Control, "BlastPressure", 1f)!;
        receiving = ReflectionHelpers.Invoke(rig.Control, "BlastNetwork", 1f);
      },
      () => blast.TryConsumeGas(0.5f * Max(blast), rig.World.Accessor)
    );

    Assert.Equal(atm, reading, 3);
    Assert.Same(blast, receiving);
  }

  // O is a second stove drawing the same blast main. Fails in OR when the furnace reads its
  // tuyeres' live pressure (pipe.Pressure in BlockEntityBlastFurnace's tuyere loop).
  [Theory]
  [MemberData(nameof(Slots))]
  public void A_blast_furnace_reads_its_tuyeres_settled_in_every_tick_order(
    string slots
  ) {
    var rig = new BlastFurnaceRig();
    var tuyeres = (PipeNetwork[])ReflectionHelpers.GetField(rig, "_tuyeres")!;
    float atm = SmexValues.BfBlastPressureThreshold + 0.1f;
    foreach (PipeNetwork tuyere in tuyeres) {
      Settle(tuyere, "Air", atm, 950f);
      tuyere.BroadcastUpdate(rig.World.Accessor);
    }

    Second(
      slots,
      () => ProductionTick(rig.Furnace),
      () => {
        foreach (PipeNetwork tuyere in tuyeres) {
          tuyere.TryConsumeGas(0.5f * atm * Max(tuyere), rig.World.Accessor);
          tuyere.BroadcastUpdate(rig.World.Accessor);
        }
      }
    );

    Assert.Equal(
      atm,
      (float)ReflectionHelpers.GetField(rig.Furnace, "_blastPressure")!,
      3
    );
  }

  // The blast main stands under the gate by half the converter's own draw over the run's capacity.
  private static (ConverterRig, PipeNetwork) ConverterUnderTheGate(
    bool blewLastTick
  ) {
    var rig = new ConverterRig();
    var blast = (PipeNetwork)ReflectionHelpers.GetField(rig, "_blast")!;
    ReflectionHelpers.SetField(rig.Control, "_blewLastTick", blewLastTick);
    float own =
      SmexValues.BessemerBlastPerSecond
      * SmexValues.BessemerSpeedMin
      / Max(blast);
    Settle(blast, "Air", SmexValues.BlastPressureThreshold - 0.5f * own);
    return (rig, blast);
  }

  // Fails when a blowing converter judges the settled figure alone (pressure >= BlastPressureThreshold
  // in BlockEntityConverterControl.BlastNetwork): it stops on the dip its own air made.
  [Fact]
  public void A_blowing_converter_stays_plumbed_in_while_its_own_draw_holds_the_main_under_the_gate() {
    var (rig, blast) = ConverterUnderTheGate(blewLastTick: true);

    object? receiving = ReflectionHelpers.Invoke(rig.Control, "BlastNetwork", 1f);

    Assert.Same(blast, receiving);
  }

  // Fails when the own draw is counted for a converter that did not blow (the _blewLastTick guard in
  // BlockEntityConverterControl.BlastNetwork): an idle converter starts under the gate.
  [Fact]
  public void An_idle_converter_still_needs_the_full_gate() {
    var (rig, _) = ConverterUnderTheGate(blewLastTick: false);

    object? receiving = ReflectionHelpers.Invoke(rig.Control, "BlastNetwork", 1f);

    Assert.Null(receiving);
  }

  // O is a second stove drawing the same blast main. Fails in OR when the stove reads the
  // passthrough's live pressure (passthrough.Pressure in BlockEntityCowperStove): its hot outlet's
  // ceiling falls below what the outlet holds and the stove passes nothing on.
  [Theory]
  [MemberData(nameof(Slots))]
  public void A_cowper_stove_passes_blast_on_at_its_settled_pressure_in_every_tick_order(
    string slots
  ) {
    var rig = new CowperRig();
    for (int i = 0; i < 20; i++)
      rig.ChargeFromExhaust(1200f);
    var exhaust = (PipeNetwork)ReflectionHelpers.GetField(rig, "_exhaust")!;
    var airIn = (PipeNetwork)ReflectionHelpers.GetField(rig, "_airInNet")!;
    var hotOut = (PipeNetwork)ReflectionHelpers.GetField(rig, "_hotOut")!;
    exhaust.TryConsumeGas(float.MaxValue, rig.World.Accessor);
    Settle(airIn, "Air", 3f);
    airIn.BroadcastUpdate(rig.World.Accessor);
    Settle(hotOut, "Air", 2.5f, 900f);

    Second(
      slots,
      () => ProductionTick(rig.Stove),
      () => {
        airIn.TryConsumeGas(Max(airIn), rig.World.Accessor);
        airIn.BroadcastUpdate(rig.World.Accessor);
      }
    );

    Assert.Equal(3f * Max(hotOut), hotOut.State!.Volume, 2);
  }

  // O is a furnace drawing the stack's run. Fails in OR when the stack reports its run's live
  // pressure (gasNet.State.Pressure in BlockEntitySmokeStack.Pressure).
  [Theory]
  [MemberData(nameof(Slots))]
  public void A_smoke_stack_reads_its_run_settled_in_every_tick_order(
    string slots
  ) {
    var (world, net) = PipeTestWorld.Run(4, capEnds: true);
    Settle(net, "Exhaust", 1.5f, 400f);
    var stack = new BlockEntitySmokeStack {
      Pos = new BlockPos(0, 0, 0),
      Block = TestBlocks.Configure(
        new Block(),
        "smex:smokestack-north",
        70,
        ("orientation", "north")
      ),
    };
    world.Attach(stack);
    ReflectionHelpers.SetField(stack, "_system", world.Networks);
    float reading = -1f;

    Second(
      slots,
      () => reading = stack.Pressure,
      () => net.TryConsumeGas(Max(net), world.Accessor)
    );

    Assert.Equal(1.5f, reading, 3);
  }

  #endregion
}
