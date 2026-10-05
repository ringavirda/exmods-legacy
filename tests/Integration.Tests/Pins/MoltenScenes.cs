using System.Collections.Generic;
using System.IO;
using System.Linq;
using ExpandedLib.Industry.Molten;
using ExpandedLib.Testing;
using Integration.Tests.Saves;
using Newtonsoft.Json.Linq;
using NSubstitute;
using SteelmakingExpanded.BlockNetworkMolten.BlockEntities;
using SteelmakingExpanded.BlockNetworkMolten.Blocks;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace Integration.Tests.Pins;

/// <summary>
/// A straight canal run along +Z from the origin built from real smex blocks and entities: a start
/// cell (<c>start</c>, facing south into the run), straights (<c>straight</c>, north-south) and a
/// mold pedestal (<c>pedestal</c>, facing north back up the run). Every block is
/// placed first and every entity then initialised. Each <see cref="Step"/> advances the calendar by
/// <see cref="MoltenClock.HoursPerTick"/>, so the metal cools as it runs.
/// </summary>
internal sealed class CanalLine {
  public readonly Scene Scene = new Scene().Network(
    "molten",
    s => new MoltenNetwork(s)
  );

  /// <summary>The run's cells, from the origin outward.</summary>
  public readonly List<BlockEntityMoltenCanal> Cells = [];

  private int _tick;

  public CanalLine(params string[] kinds) {
    MoltenClock.RegisterMetals(Scene.World);
    for (int z = 0; z < kinds.Length; z++) {
      var pos = new BlockPos(0, 0, z);
      var (block, be) = MoltenClock.Cell(kinds[z], z + 1);
      Scene.World.Place(pos, block, be);
      Cells.Add(be);
    }
    foreach (var be in Cells)
      Scene.World.Initialize(be);
    Scene.Build();
  }

  public BlockEntityMoltenCanal this[int index] => Cells[index];

  public BlockEntityMoltenCanalMoldPedestal Pedestal =>
    Cells.OfType<BlockEntityMoltenCanalMoldPedestal>().Single();

  public BlockEntityMoltenCanalTap Tap =>
    Cells.OfType<BlockEntityMoltenCanalTap>().Single();

  /// <summary>Pours up to <paramref name="units"/> of iron at <paramref name="temp"/> into cell <paramref name="index"/>.</summary>
  public int Pour(int index, int units, float temp = 1700f) =>
    MoltenClock.Pour(Scene.World, Cells[index], MoltenClock.Iron, units, temp);

  /// <summary>One server second at the next calendar hour: entity ticks, then the molten network.</summary>
  public void Step() {
    _tick++;
    MoltenClock.SetTick(Scene.World, _tick);
    Scene.Step(1);
  }

  /// <summary>Records every cell of the line at <paramref name="tick"/>.</summary>
  public void Record(Trace trace, int tick) =>
    MoltenTrace.Cells(trace, tick, Cells);
}

/// <summary>The calendar, metals and cell builders the canal scenes share.</summary>
internal static class MoltenClock {
  public const string Iron = "game:ingot-iron";
  public const string Copper = "game:ingot-copper";
  public const string Steel = "game:ingot-steel";

  /// <summary>Game hours one scene tick advances the calendar by.</summary>
  public const double HoursPerTick = 1.0 / 120.0;

  /// <summary>Registers iron (melting at 1500 C), copper (1084 C) and steel (1502 C) in <paramref name="world"/>.</summary>
  public static void RegisterMetals(TestWorld world) {
    world.RegisterItem(Iron, 1500f);
    world.RegisterItem(Copper, 1084f);
    world.RegisterItem(Steel, 1502f);
  }

  /// <summary>
  /// The fired double-ingot tool mold with the attributes smex ships for it (200 units, two ingots),
  /// read from its block type. The block's id is 900, clear of the cells' ids.
  /// </summary>
  public static Block DoubleIngotMold() {
    var type = JObject.Parse(
      File.ReadAllText(
        Path.Combine(
          SaveGoldens.RepoRoot(),
          "smex/assets/smex/blocktypes/molds/toolmoldfired.json"
        )
      )
    );
    var block = TestBlocks.Configure(
      new BlockToolMold(),
      "smex:toolmold-black-fired-doubleingot",
      900,
      ("color", "black"),
      ("materialtype", "fired"),
      ("tooltype", "doubleingot")
    );
    block.Attributes = new JsonObject(
      type["attributesByType"]!["toolmold-*-fired-doubleingot"]!
    );
    return block;
  }

  /// <summary>A vanilla tool mold of <paramref name="mold"/> standing on the ground at <paramref name="pos"/>, initialised, for a pour by hand.</summary>
  public static BlockEntityToolMold MoldOnTheGround(
    TestWorld world,
    BlockPos pos,
    Block mold
  ) {
    var be = new BlockEntityToolMold();
    world.Place(pos, mold, be);
    world.Initialize(be);
    return be;
  }

  /// <summary>Sets the calendar to <paramref name="tick"/> scene ticks from zero.</summary>
  public static void SetTick(TestWorld world, int tick) =>
    world.Calendar.TotalHours.Returns(tick * HoursPerTick);

  /// <summary>Pours <paramref name="units"/> of <paramref name="metal"/> at <paramref name="temp"/> into <paramref name="cell"/>; returns what it took.</summary>
  public static int Pour(
    TestWorld world,
    BlockEntityMoltenCanal cell,
    string metal,
    int units,
    float temp
  ) =>
    cell.PushMetal(
      units,
      MoltenMetal.CreateStack(world.World, metal, temp)!,
      world.World
    );

  /// <summary>
  /// A block and a fresh entity for a canal cell. <paramref name="kind"/> is <c>start</c>,
  /// <c>pedestal</c>, <c>tap</c> or <c>straight</c> (north-south), or <c>type-orientation</c> for any other
  /// canal type, such as <c>bend-nw</c> or <c>start-e</c>.
  /// </summary>
  public static (BlockMoltenCanal block, BlockEntityMoltenCanal be) Cell(
    string kind,
    int id
  ) {
    string[] parts = kind.Split('-');
    string type = parts[0] == "pedestal" ? "moldpedestal" : parts[0];
    string orientation =
      parts.Length > 1 ? parts[1]
      : type == "start" ? "s"
      : type == "moldpedestal" || type == "tap" ? "n"
      : "ns";
    return type switch {
      "start" => (
        Configure(new BlockMoltenCanalStart(), type, orientation, id),
        new BlockEntityMoltenCanalStart()
      ),
      "moldpedestal" => (
        Configure(new BlockMoltenCanalMoldPedestal(), type, orientation, id),
        new BlockEntityMoltenCanalMoldPedestal()
      ),
      "tap" => (
        Configure(new BlockMoltenCanalTap(), type, orientation, id),
        new BlockEntityMoltenCanalTap()
      ),
      _ => (
        Configure(new BlockMoltenCanal(), type, orientation, id),
        new BlockEntityMoltenCanal()
      ),
    };
  }

  private static BlockMoltenCanal Configure(
    BlockMoltenCanal block,
    string type,
    string orientation,
    int id
  ) {
    TestBlocks.Configure(
      block,
      $"smex:moltencanal-{type}-{orientation}",
      id,
      ("type", type),
      ("orientation", orientation)
    );
    ReflectionHelpers.SetProperty(block, "Type", type);
    ReflectionHelpers.SetProperty(block, "Orientation", orientation);
    return block;
  }
}

/// <summary>The canal observables a trace records: per cell, its metal, amount, temperature and whether it froze.</summary>
internal static class MoltenTrace {
  public static void Cells(
    Trace trace,
    int tick,
    IEnumerable<BlockEntityMoltenCanal> cells
  ) {
    foreach (var c in cells.OrderBy(c => (c.Pos.X, c.Pos.Y, c.Pos.Z)))
      trace.Line(tick, Cell(c));
  }

  private static string[] Cell(BlockEntityMoltenCanal c) {
    var fields = new List<string>
    {
      $"cell {c.Pos.X},{c.Pos.Y},{c.Pos.Z}",
      "metal=" + (c.CellMetalType.Length > 0 ? c.CellMetalType : "-"),
      "amount=" + c.CellAmount,
      "temp=" + Trace.Celsius(c.CellTemperature),
      "solid=" + Trace.Flag(c.Solidified),
    };
    if (c is BlockEntityMoltenCanalMoldPedestal p)
      fields.Add("mold=" + p.MoldCurrentUnits);
    return fields.ToArray();
  }
}
