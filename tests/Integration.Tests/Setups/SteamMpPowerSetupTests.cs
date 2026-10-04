using System;
using System.Collections.Generic;
using System.Linq;
using ExpandedLib.Testing;
using PipesAndPowerExpanded;
using PipesAndPowerExpanded.BlockNetworkPipe.Blocks;
using Xunit;
using Xunit.Abstractions;
using BlockEntity = Vintagestory.API.Common.BlockEntity;

namespace Integration.Tests.Setups;

/// <summary>
/// The steam MP power setup builds as drawn, fires from cold and settles where the drawing says: the
/// boiler never chokes or reaches its limit, the Watt engine holds its band and turns the axle line,
/// four helve hammers and the mechanical pump turn on it, and the pump keeps the boiler's water
/// steady. Its recording is the one the wiki plays.
/// </summary>
public class SteamMpPowerSetupTests(ITestOutputHelper output) {
  #region Fixtures

  private const int RunSeconds = 1200;

  /// <summary>The steam run's flow the drawing prints (L/s).</summary>
  private const double DrawnSteamFlow = 32.0;

#if GAME_GE_1_22
  /// <summary>The axle line's speed the drawing prints.</summary>
  private const double DrawnSpeed = 0.391;

  /// <summary>What the pump lifts in the drawing (L/s).</summary>
  private const double DrawnPumpOutput = 7.81;
#else
  // Before 1.22 a toggle working a hammer adds 5 exp(2.8 speed - 5) to its 0.125, so the line
  // carries more load and turns slower.

  /// <summary>The axle line's speed the drawing prints.</summary>
  private const double DrawnSpeed = 0.336;

  /// <summary>What the pump lifts in the drawing (L/s).</summary>
  private const double DrawnPumpOutput = 6.71;
#endif

  /// <summary>What the boiler draws from its feed main in the drawing (L/s).</summary>
  private const double DrawnFeed = 2.0;

  private const float BandLow = 2f;
  private const float BandHigh = 4f;

  /// <summary>
  /// Fails <paramref name="letter"/> at the first second from <paramref name="from"/> that
  /// <paramref name="failed"/> holds, naming the second and its readings.
  /// </summary>
  private static void Never(
    SteamMpPowerPlant plant,
    string letter,
    Func<SteamMpPowerPlant.Second, bool> failed,
    int from = 0
  ) {
    int? at = plant.First(failed, from);
    Assert.True(
      at == null,
      $"{letter} at second {at}: {(at == null ? "" : plant.Seconds[at.Value].ToString())}"
    );
  }

  /// <summary>
  /// Fails in order: the boiler never chokes; it stays below its limit, no pipe is lost and the
  /// engine never breaks; through the window the engine's inlet holds the 2-4 atm band, the engine
  /// runs and the line turns; the boiler's water is steady and above its floor.
  /// </summary>
  private static void HoldsAToD(
    SteamMpPowerPlant plant,
    SetupRecording recording
  ) {
    int window = recording.SteadyFrom;
    Never(plant, "a: the boiler choked", s => s.Choked);
    Never(
      plant,
      "b: the boiler reached its limit, a pipe burst or the engine broke",
      s =>
        s.BoilerPressure >= PpexValues.CornishBoilerMaxOutputPressure
        || s.PipesLost > 0
        || s.EngineBroken
    );
    Never(
      plant,
      "c: the engine's inlet left its band, the engine stopped or the line stood",
      s =>
        s.InletPressure < BandLow
        || s.InletPressure > BandHigh
        || !s.EngineRunning
        || s.Speed <= 0.001f,
      window
    );
    Never(
      plant,
      "d: the boiler's water fell to its floor",
      s => s.Water <= PpexValues.CornishBoilerMinBoilWater,
      window
    );
    Assert.False(
      recording.Unsteady().Contains("boiler.water"),
      "d: the boiler's water was not steady"
    );
  }

  /// <summary>
  /// Tick slots, one letter each: E the engine, P the mechanical pump, W the water valve, B the
  /// boiler and N the network tick. Every order of the four machines with the network tick after
  /// them, and the network tick in each earlier place in the drawn order EPWB and in its reverse
  /// BWPE. The axle line turns after every slot.
  /// </summary>
  public static TheoryData<string> Slots() {
    var slots = new TheoryData<string>();
    foreach (string order in Permutations("EPWB"))
      slots.Add(order + "N");
    foreach (string order in new[] { "EPWB", "BWPE" })
      for (int n = 0; n < order.Length; n++)
        slots.Add(order.Insert(n, "N"));
    return slots;
  }

  private static IEnumerable<string> Permutations(string letters) =>
    letters.Length <= 1
      ? [letters]
      : letters.SelectMany(
        (c, i) => Permutations(letters.Remove(i, 1)).Select(rest => c + rest)
      );

  private static SteamMpPowerPlant Slotted(
    SteamMpPowerPlant plant,
    string slots
  ) =>
    plant.Slots(
      [
        .. slots.Select(c =>
          c switch {
            'E' => plant.Engine,
            'P' => plant.Pump,
            'W' => plant.WaterValve,
            'B' => plant.Boiler.Be,
            _ => (BlockEntity?)null,
          }
        ),
      ]
    );

  #endregion

  #region Building it in game

  // Fails when a pipe's faces form a shape no shipped pipe has.
  [Fact]
  public void Every_pipe_the_plant_lays_has_a_shipped_shape() {
    var plant = new SteamMpPowerPlant();

    List<string> unshipped = [];
    foreach (var (pos, code) in plant.Placed) {
      if (plant.Scene.World.GetBlock(pos) is not BlockPipe pipe)
        continue;
      string faces = Sorted(pipe.Orientation);
      if (!pipe.AllowedOrientations.Values.SelectMany(o => o).Any(o => Sorted(o) == faces))
        unshipped.Add($"{pos} {code}");
    }

    Assert.True(unshipped.Count == 0, string.Join("\n", unshipped));
  }

  private static string Sorted(string faces) =>
    new(faces.OrderBy(c => c).ToArray());

  // Fails when two of the plant's blocks are laid in one cell: the later one replaces the earlier
  // in the world, so a pipe or axle laid through a machine leaves the machine without its block.
  [Fact]
  public void No_two_blocks_the_plant_lays_share_a_cell() {
    var plant = new SteamMpPowerPlant();
    var cells = plant
      .Placed.Concat(plant.LineBlocks)
      .Select(p => p.Pos)
      .Concat(
        [
          plant.Engine.Pos,
          plant.Generator.Pos,
          plant.Pump.Pos,
          plant.Intake.Pos,
          plant.SteamValve.Pos,
          plant.WaterValve.Pos,
        ]
      )
      .ToList();

    List<string> shared = cells
      .GroupBy(p => p)
      .Where(g => g.Count() > 1)
      .Select(g => g.Key.ToString())
      .ToList();

    Assert.True(shared.Count == 0, string.Join("\n", shared));
  }

  #endregion

  #region The setup

  [Fact]
  public void The_steam_mp_power_setup_runs_as_drawn() {
    var plant = new SteamMpPowerPlant();
    SetupRecording recording = plant.Record(RunSeconds);
    plant.Run(RunSeconds);
    IReadOnlyDictionary<string, object> steady = recording.Steady();
    foreach (var (key, value) in steady)
      output.WriteLine($"{key} {value}");

    HoldsAToD(plant, recording);
    double flow = (double)steady["steam.flow"];
    double speed = (double)steady["generator.speed"];
    double lifted = (double)steady["pump.output"];
    double feed = (double)steady["boiler.feed"];
    Assert.True(
      Math.Abs(flow - DrawnSteamFlow) <= 0.5
        && Math.Abs(speed - DrawnSpeed) <= 0.01
        && Math.Abs(lifted - DrawnPumpOutput) <= 0.2
        && Math.Abs(feed - DrawnFeed) <= 0.1,
      $"e: the steam run carried {flow} L/s, the line turned at {speed}, the pump lifted"
        + $" {lifted} L/s and the boiler drew {feed} L/s"
    );
    IReadOnlyList<string> unsteady = recording.Unsteady();
    Assert.True(
      unsteady.Count == 0,
      "f: not steady: " + string.Join(", ", unsteady)
    );
    recording.Save();
  }

  [Theory]
  [MemberData(nameof(Slots))]
  public void The_setup_holds_in_every_tick_order(string slots) {
    var plant = Slotted(new SteamMpPowerPlant(), slots);
    SetupRecording recording = plant.Record(RunSeconds);
    plant.Run(RunSeconds);

    HoldsAToD(plant, recording);
    IReadOnlyDictionary<string, object> steady = recording.Steady();
    double flow = (double)steady["steam.flow"];
    double speed = (double)steady["generator.speed"];
    double feed = (double)steady["boiler.feed"];
    output.WriteLine(
      $"{slots}: steam.flow {flow} l/s, generator.speed {speed},"
        + $" pump.output {steady["pump.output"]} l/s, boiler.feed {feed} l/s,"
        + $" water-valve.vent {steady["water-valve.vent"]} l/s"
    );
    Assert.True(
      Math.Abs(flow - DrawnSteamFlow) <= 0.5
        && Math.Abs(speed - DrawnSpeed) <= 0.01
        && Math.Abs(feed - DrawnFeed) <= 0.1,
      $"e: the steam run carried {flow} L/s, the line turned at {speed} and the boiler drew"
        + $" {feed} L/s"
    );
  }

  // Fails when the pump's drive turns while it is off the line (a pump that moves water with no
  // shaft), or when the condensate alone keeps the boiler fed.
  [Fact]
  public void With_the_pump_off_the_shaft_the_boiler_starves() {
    var plant = new SteamMpPowerPlant(pumpOnShaft: false).Run(RunSeconds);

    Assert.All(plant.Seconds, s => Assert.Equal(0f, s.PumpOutput));
    Assert.NotNull(
      plant.First(s => s.Water <= PpexValues.CornishBoilerMinBoilWater)
    );
  }

  #endregion
}
