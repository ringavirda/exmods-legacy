using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ExpandedLib.Industry.Pipes;
using ExpandedLib.Testing;
using Integration.Tests.Saves;
using Newtonsoft.Json.Linq;
using PipesAndPowerExpanded;
using PipesAndPowerExpanded.BlockNetworkPipe.BlockEntities;
using PipesAndPowerExpanded.BlockNetworkPipe.Blocks;
using PipesAndPowerExpanded.BlockStructures.Engine.BlockEntities;
using PipesAndPowerExpanded.BlockStructures.Engine.Blocks;
using PipesAndPowerExpanded.BlockStructures.MpPump.BlockEntities;
using PipesAndPowerExpanded.BlockStructures.MpPump.Blocks;
using PipesAndPowerExpanded.Tests;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;
using Vintagestory.GameContent.Mechanics;
using AssetLocation = Vintagestory.API.Common.AssetLocation;
using Block = Vintagestory.API.Common.Block;
using BlockEntity = Vintagestory.API.Common.BlockEntity;
using Item = Vintagestory.API.Common.Item;
using ItemStack = Vintagestory.API.Common.ItemStack;
using BlockPipePassthrough = PipesAndPowerExpanded.BlockNetworkPipe.Blocks.BlockPipePassthrough;
using BoilerState = PipesAndPowerExpanded.BlockStructures.Boiler.BlockEntityBoiler.BoilerState;

namespace Integration.Tests.Setups;

/// <summary>
/// The steam MP power setup as the wiki draws it, every machine under its shipped code: the starter's
/// fired Cornish boiler, chimney, steam main and 3.5 atm relief valve, its Watt engine turning an MP
/// generator, and an axle line east from the generator through <see cref="Hammers"/> toggles, each
/// working a helve hammer, to a mechanical fluid pump at its end. The pump lifts a pond intake into
/// the feed main, which takes the engine's condensate into the boiler past a 0.5 atm relief valve.
/// The axle line is one vanilla <see cref="MechanicalNetwork"/> joined by hand, stepped as the game's
/// mechanical power tick steps it after each second's block-entity and pipe ticks. Machines are built
/// in tick order, the engine, the generator, the pump and the water valve last, unless
/// <see cref="Slots"/> names another order.
/// </summary>
internal sealed class SteamMpPowerPlant {
  /// <summary>The file name of the setup's recording.</summary>
  public const string Setup = "steam-mp-power";

  /// <summary>Helve hammers on the axle line.</summary>
  public const int Hammers = 4;

  /// <summary>Server ticks of the mechanical power system per second (20 ms each).</summary>
  private const int MpTicksPerSecond = 50;

  /// <summary>One second of the run, read after its tick.</summary>
  public readonly record struct Second(
    int At,
    bool Choked,
    float BoilerPressure,
    float Water,
    int PipesLost,
    bool EngineBroken,
    bool EngineRunning,
    float InletPressure,
    float Speed,
    float PumpOutput
  );

  public readonly Scene Scene = new Scene().Network(
    "pipe",
    s => new PipeNetwork(s)
  );
  public readonly BoilerFixture Boiler;
  public readonly BlockEntityEngineWatt Engine;
  public readonly BlockEntityEngineMpGenerator Generator;
  public readonly BlockEntityMpFluidPump Pump;
  public readonly BlockEntityFluidIntake Intake;
  public readonly BlockEntityPressureValve SteamValve;
  public readonly BlockEntityPressureValve WaterValve;

  /// <summary>The axle line, from the generator to the pump.</summary>
  public readonly MechanicalNetwork Line = new();

  /// <summary>Every second run so far, from 0.</summary>
  public readonly List<Second> Seconds = [];

  private readonly BEBehaviorMpPumpDrive _drive;
  private readonly List<BEBehaviorMPToggle> _toggles = [];
  private readonly BlockPos _exhaust;
  private readonly BlockPos _steam;
  private readonly BlockPos _feed;
  private readonly BlockPos _pond;
  private readonly List<BlockPos> _pipes = [];
  private readonly List<(BlockPos Pos, string Code)> _placed = [];
  private readonly List<(BlockPos Pos, string Code)> _line = [];
  private int _nextId = 200;
  private long _mpTick;
  private SetupRecording? _recording;
  private float _lastWater;
  private float _feedDraw;
  private BlockEntity?[]? _slots;

  /// <param name="pumpOnShaft">The pump's drive joined to the axle line; without it the pump stands.</param>
  public SteamMpPowerPlant(bool pumpOnShaft = true) {
    Scene.World.BreakRunsBlockHooks = false;

    Boiler = new BoilerFixture(Scene, new BlockPos(0, 8, 0));

    _exhaust = Boiler.Block.ExhaustOutletWorldPos(Boiler.Be.Pos);
    var outlet = new BlockEntityPipeOutlet();
    BlockPipeOutlet outletBlock = PpexScenes.UpOutlet(NextId());
    Scene.World.Place(_exhaust, outletBlock, outlet);
    Scene.World.Initialize(outlet);
    _placed.Add((_exhaust, outletBlock.Code.ToString()));
    Scene.Block(_exhaust.UpCopy(), PpexScenes.Chimney(NextId()));

    // Steam main: up off the boiler's port, east, down to the engine's inlet on the ground. The
    // port filler under the first pipe is a cap here.
    _steam = Boiler.SteamPipeAttachPos;
    BlockPos tee = _steam.AddCopy(1, 0, 0);
    BlockPos corner = _steam.AddCopy(2, 0, 0);
    BlockPos foot = new(corner.X, Boiler.Be.Pos.Y, corner.Z);
    BlockPos inlet = foot.EastCopy();
    Scene.Block(_steam.DownCopy(), PpexScenes.Cap(NextId()));
    Place(
      new StarterSteamPowerPlant.Main()
        .Open(_steam, BlockFacing.DOWN)
        .Lay(_steam, corner, foot, inlet)
        .Open(tee, BlockFacing.SOUTH)
        .Open(inlet, BlockFacing.NORTH)
    );
    SteamValve = NewValve(tee.SouthCopy(), "ns");
    Install(SteamValve, StarterSteamPowerPlant.SteamGate);

    BlockPos enginePos = inlet.NorthCopy();
    var engineBlock = TestBlocks.Configure(
      new BlockEngineWatt(),
      "ppex:enginewatt-north",
      NextId(),
      ("side", "north")
    );
    Engine = new BlockEntityEngineWatt {
      Pos = enginePos.Copy(),
      Block = engineBlock,
    };

    BlockPos generatorPos = engineBlock.SubmachinePos(enginePos);
    var generatorBlock = TestBlocks.Configure(
      new BlockEngineMPGenerator(),
      "ppex:enginempgenerator-east",
      NextId(),
      ("side", "east")
    );
    Generator = new BlockEntityEngineMpGenerator {
      Pos = generatorPos.Copy(),
      Block = generatorBlock,
    };

    // Axle line: east off the generator, a toggle on every other cell with its helve hammer to the
    // south, the pump at the far end with its drive face to the line.
    BlockPos at = generatorPos.EastCopy();
    for (int h = 0; h < Hammers; h++) {
      Axle(at);
      at = at.EastCopy();
      Toggle(at);
      at = at.EastCopy();
    }
    Axle(at);
    BlockPos pumpPos = at.EastCopy();
    var pumpBlock = TestBlocks.Configure(
      new BlockMpFluidPump(),
      "ppex:mpfluidpump-north",
      NextId(),
      ("side", "north")
    );
    pumpBlock.Attributes = new JsonObject(
      JObject.Parse(
        File.ReadAllText(
          Path.Combine(
            SaveGoldens.RepoRoot(),
            "ppex",
            "assets",
            "ppex",
            "blocktypes",
            "mpfluidpump.json"
          )
        )
      )["attributes"]
    );
    Pump = new BlockEntityMpFluidPump { Pos = pumpPos.Copy(), Block = pumpBlock };
    _drive = new BEBehaviorMpPumpDrive(Pump);

    // Pond run: a bend under the pump's source port, the intake beside it on the pond, facing it.
    _pond = pumpBlock.SourceWorldPos(pumpPos).AddCopy(BlockMpFluidPump.SourceFace);
    Place(
      new StarterSteamPowerPlant.Main()
        .Open(_pond, BlockMpFluidPump.SourceFace.Opposite)
        .Open(_pond, BlockFacing.EAST)
    );
    BlockPos intakePos = _pond.EastCopy();
    var intakeBlock = TestBlocks.Configure(
      new BlockFluidIntake(),
      "ppex:pipe-fluidintake-w",
      NextId(),
      ("type", "fluidintake"),
      ("orientation", "w")
    );
    ReflectionHelpers.SetProperty(intakeBlock, "Orientation", "w");
    Intake = new BlockEntityFluidIntake {
      Pos = intakePos.Copy(),
      Block = intakeBlock,
    };
    Scene.Node(intakePos, intakeBlock, Intake, "pipe");
    ReflectionHelpers.SetProperty(Intake, nameof(Intake.HasWater), true);
    ReflectionHelpers.SetProperty(
      Intake,
      nameof(Intake.NetworkSystem),
      Scene.World.Networks
    );

    // Feed main: from the pump's delivery port west over the axle line and down beside the
    // generator to the water valve's tee,
    // then as the starter's, down a course, south along the boiler's east side and round behind the
    // firebox, north through the structure's two fireclay passthroughs to its feed face. The
    // engine's condensate leaves south of the axle line and joins it under the steam inlet.
    BlockPos delivery = pumpBlock.OutletWorldPos(pumpPos).AddCopy(pumpBlock.OutputFace);
    BlockPos waterTee = new(generatorPos.X - 1, generatorPos.Y, delivery.Z);
    BlockPos condensate = enginePos.AddCopy(engineBlock.WaterOutletFace);
    BlockPos under = new(condensate.X, condensate.Y - 1, inlet.Z);
    _feed = Boiler.Be.Pos.DownCopy();
    BlockPos behind = _feed.AddCopy(0, 0, 3);
    BlockPos east = new(waterTee.X, _feed.Y, delivery.Z);
    Place(
      new StarterSteamPowerPlant.Main()
        .Lay(
          delivery,
          new BlockPos(generatorPos.X, delivery.Y, delivery.Z),
          new BlockPos(generatorPos.X, waterTee.Y, delivery.Z),
          waterTee,
          waterTee.WestCopy(),
          new BlockPos(waterTee.X - 1, _feed.Y, delivery.Z),
          east,
          new BlockPos(east.X, _feed.Y, behind.Z),
          behind,
          _feed
        )
        .Lay(
          condensate,
          new BlockPos(condensate.X, condensate.Y, inlet.Z),
          under,
          new BlockPos(east.X, under.Y, under.Z)
        )
        .Open(delivery, pumpBlock.OutputFace.Opposite)
        .Open(condensate, engineBlock.WaterOutletFace.Opposite)
        .Open(waterTee, BlockFacing.NORTH)
        .Open(_feed, BlockFacing.UP),
      _feed,
      _feed.SouthCopy(),
      _feed.SouthCopy(2)
    );
    WaterValve = NewValve(waterTee.NorthCopy(), "sn");

    Scene.Machine(enginePos, engineBlock, Engine);
    RccFake.Complete(Engine);
    Scene.Machine(generatorPos, generatorBlock, Generator);
    var generatorDrive = new BEBehaviorEngineMPGenerator(Generator);
    MechPower.Attach(Generator, generatorDrive, Line);
    ReflectionHelpers.SetField(Generator, "_mp", generatorDrive);
    Line.nodes[generatorPos.Copy()] = generatorDrive;
    Scene.Machine(pumpPos, pumpBlock, Pump);
    RccFake.Complete(Pump);
    MechPower.Attach(Pump, _drive, pumpOnShaft ? Line : null);
    if (pumpOnShaft)
      Line.nodes[pumpPos.Copy()] = _drive;
    Install(WaterValve, StarterSteamPowerPlant.WaterGate);
  }

  /// <summary>
  /// Every pipe and pipe fitting the plant laid, with its placed code: the steam, pond and feed mains
  /// and the exhaust outlet. The valves, the intake and the cap under the steam main are not listed.
  /// </summary>
  public IReadOnlyList<(BlockPos Pos, string Code)> Placed => _placed;

  /// <summary>Every axle, toggle and helve hammer the axle line laid, with its vanilla code.</summary>
  public IReadOnlyList<(BlockPos Pos, string Code)> LineBlocks => _line;

  /// <summary>The axle line's speed, without its sign.</summary>
  public float Speed => Math.Abs(Line.Speed);

  /// <summary>Resistance the helve hammers put on the line.</summary>
  public float HammerLoad => _toggles.Sum(t => t.GetResistance());

  /// <summary>Resistance the pump puts on the line while it is on it.</summary>
  public float PumpLoad => Line.nodes.ContainsValue(_drive) ? _drive.GetResistance() : 0f;

  /// <summary>
  /// Records the plant's runs and machines under the drawing's ids, for <paramref name="seconds"/>.
  /// </summary>
  public SetupRecording Record(int seconds) {
    _recording = new SetupRecording(Setup, Scene, seconds);
    _recording.Run("steam", _steam);
    _recording.Run("exhaust", _exhaust);
    _recording.Run("feed", _feed);
    _recording.Run("pond", _pond);

    _recording
      .Machine("boiler", "ppex:boilercornish-north")
      .State(BoilerWord)
      .Value("water", "L", () => Water)
      .Value("pressure", "atm", () => Boiler.Be.InternalPressure)
      .Value("feed", "l/s", () => _feedDraw)
      .Constant("max", "atm", PpexValues.CornishBoilerMaxOutputPressure)
      .Constant("boostAbove", "atm", 1f);
    _recording
      .Machine("chimney", PpexScenes.ChimneyCode)
      .State(() => Draws ? "drawing" : "idle");
    _recording
      .Machine("engine", "ppex:enginewatt-north")
      .State(() =>
        Engine.IsBroken ? "broken"
        : Engine.IsRunning ? "running"
        : "idle"
      )
      .Value("pressure", "atm", () => Engine.InletPressure)
      .Constant("engage", "atm", PpexValues.WattEngineEngagePressure)
      .Constant("break", "atm", PpexValues.WattEngineBreakPressure)
      .Constant("steam", "l/s", PpexValues.WattEngineSteamRate)
      .Constant("water", "l/s", PpexValues.WattEngineWaterRate)
      .Constant("efficiency", "", PpexValues.SteamEngineEfficiency);
    _recording
      .Machine("generator", "ppex:enginempgenerator-east")
      .State(() => Speed > 0.001f ? "turning" : "idle")
      .Value("speed", "", () => Speed)
      .Value("cap", "", () => Engine.ShaftSpeed)
      .Value("budget", "", () => Engine.MpPowerBudget)
      .Value("load", "", () => Line.NetworkResistance);
    for (int h = 0; h < _toggles.Count; h++) {
      BEBehaviorMPToggle toggle = _toggles[h];
      _recording
        .Machine($"hammer-{h + 1}", "game:helvehammerbase-north")
        .State(() => Speed > 0.001f ? "hammering" : "idle")
        .Value("load", "", toggle.GetResistance)
        .Value("power", "", () => toggle.GetResistance() * Speed);
    }
    _recording
      .Machine("pump", "ppex:mpfluidpump-north")
      .State(() => PumpDraws ? "drawing" : "idle")
      .Value("speed", "", () => _drive.DriveSpeed)
      .Value("output", "l/s", () => Pump.OutputPerSecond)
      .Value("power", "", () => PumpLoad * Speed)
      .Constant("head", "atm", PpexValues.MpPumpDeliveryPressure)
      .Constant("load", "", BEBehaviorMpPumpDrive.PumpResistance);
    _recording
      .Machine("intake", "ppex:pipe-fluidintake-w")
      .State(() => PumpDraws ? "drawing" : "idle");
    RecordValve("steam-valve", SteamValve);
    RecordValve("water-valve", WaterValve);
    return _recording;
  }

  /// <summary>
  /// Primes the boiler idle with the starter's water and no steam, its pile burning, then runs
  /// <paramref name="seconds"/> seconds, each one the block-entity and pipe ticks and then a second
  /// of the axle line, sampling the recording after each when there is one.
  /// </summary>
  public SteamMpPowerPlant Run(int seconds) {
    Scene.Build();
    if (_slots != null)
      TickSlots.Order(Scene.World, [.. _slots.OfType<BlockEntity>()]);
    Boiler.Prime(BoilerState.Idle, water: StarterSteamPowerPlant.PrimeWater, steam: 0f);
    _lastWater = Water;
    for (int t = 0; t < seconds; t++) {
      float boiled = Boiled();
      if (_slots == null)
        Scene.Step(1);
      else
        TickSlots.Step(Scene.World, NetworkSlot());
      TurnLine();
      float water = Water;
      _feedDraw = water - _lastWater + boiled;
      _lastWater = water;
      _recording?.Sample(t);
      Seconds.Add(
        new Second(
          t,
          Choked,
          Boiler.Be.InternalPressure,
          Water,
          _pipes.Count(p => Scene.World.GetBlock(p).Id == 0),
          Engine.IsBroken,
          Engine.IsRunning,
          Engine.InletPressure,
          Speed,
          Pump.OutputPerSecond
        )
      );
    }
    return this;
  }

  /// <summary>
  /// Fires the machines in <paramref name="order"/> after every other tick listener in each second,
  /// in that order, and runs the network tick in the place of the null entry among them, or after
  /// all of them when there is none. Taken by <see cref="Run"/>.
  /// </summary>
  public SteamMpPowerPlant Slots(params BlockEntity?[] order) {
    _slots = order;
    return this;
  }

  /// <summary>The first second <paramref name="failed"/> holds, or null when none does.</summary>
  public int? First(Func<Second, bool> failed, int from = 0) =>
    Seconds.Where(s => s.At >= from && failed(s)).Select(s => (int?)s.At).FirstOrDefault();

  /// <summary>
  /// One second of vanilla MechanicalNetwork.ServerTick without its client broadcast: the angle
  /// every tick, the torque and resistance solve every fifth.
  /// </summary>
  private void TurnLine() {
    for (int i = 0; i < MpTicksPerSecond; i++) {
      _mpTick++;
      Line.UpdateAngle(Line.Speed * (1f / MpTicksPerSecond) * 50f);
      if (_mpTick % 5 == 0)
        Line.updateNetwork(_mpTick);
    }
  }

  private int NetworkSlot() {
    int at = Array.IndexOf(_slots!, null);
    return at < 0 || at == _slots!.Length - 1
      ? int.MaxValue
      : TickSlots.Before(Scene.World, _slots[at + 1]!);
  }

  private BlockEntityCoalPile? Pile =>
    Scene.EntityAt<BlockEntityCoalPile>(Boiler.Block.FuelWorldPos(Boiler.Be.Pos));

  private bool Choked => (bool)ReflectionHelpers.GetField(Boiler.Be, "_choked")!;

  private float Water => (float)ReflectionHelpers.GetField(Boiler.Be, "_waterVolume")!;

  /// <summary>
  /// Water (L) the boiler turns to steam in the coming second, read before its tick: its full rate
  /// while boiling with water at or above its floor and pressure below its limit, else none.
  /// </summary>
  private float Boiled() =>
    (BoilerState)ReflectionHelpers.GetField(Boiler.Be, "_state")! == BoilerState.Boiling
    && Water >= PpexValues.CornishBoilerMinBoilWater
    && Boiler.Be.InternalPressure < PpexValues.CornishBoilerMaxOutputPressure
      ? PpexValues.CornishBoilerSteamPerSecond / PpexValues.SteamExpansionFactor
      : 0f;

  private bool PumpDraws => (bool)ReflectionHelpers.GetField(Pump, "_drawingWater")!;

  private bool Draws =>
    Pile?.IsBurning == true
    && Scene.NetworkAt<PipeNetwork>(_exhaust)?.HasDraught(Scene.World.Accessor) == true;

  private string BoilerWord() {
    if (Choked)
      return "choked";
    if ((bool)ReflectionHelpers.GetField(Boiler.Be, "_steamLeaking")!)
      return "blowing";
    var state = (BoilerState)ReflectionHelpers.GetField(Boiler.Be, "_state")!;
    return state.ToString().ToLowerInvariant();
  }

  private void RecordValve(string id, BlockEntityPressureValve valve) {
    _recording!
      .Machine(id, valve.Block.Code.ToString())
      .State(() => Vent(valve) > 0f ? "venting" : "shut")
      .Value("vent", "l/s", () => Vent(valve))
      .Constant("gate", "atm", valve.GatePressure);
  }

  private static float Vent(BlockEntityPressureValve valve) =>
    (float)ReflectionHelpers.GetField(valve, "_lastVentVolume")!;

  private int NextId() => _nextId++;

  /// <summary>A vanilla axle along the line at <paramref name="pos"/>, joined to it.</summary>
  private void Axle(BlockPos pos) {
    var be = LineEntity(pos, "game:woodenaxle-we", ("rotation", "we"));
    Join(pos, be, new BEBehaviorMPAxle(be));
  }

  /// <summary>
  /// A vanilla toggle on the line at <paramref name="pos"/> with a helve hammer base to its south
  /// holding a hammer head, so the toggle carries a working hammer's resistance.
  /// </summary>
  private void Toggle(BlockPos pos) {
    var be = LineEntity(pos, "game:woodentoggle-we", ("orientation", "we"));
    var toggle = new BEBehaviorMPToggle(be) { Api = Scene.World.Api };
    var sides = (BlockPos[])ReflectionHelpers.GetField(toggle, "sides")!;
    sides[0] = pos.NorthCopy();
    sides[1] = pos.SouthCopy();
    Join(pos, be, toggle);
    _toggles.Add(toggle);

    BlockPos basePos = pos.SouthCopy();
    var baseBlock = TestBlocks.Configure(
      new Block(),
      "game:helvehammerbase-north",
      NextId(),
      ("side", "north")
    );
    var hammer = new BEHelveHammer { Pos = basePos.Copy(), Block = baseBlock };
    Scene.World.Place(basePos, baseBlock, hammer);
    ReflectionHelpers.SetField(
      hammer,
      "hammerStack",
      new ItemStack(new Item { Code = new AssetLocation("game:helvehammer") })
    );
    _line.Add((basePos, baseBlock.Code.ToString()));
  }

  private BlockEntity LineEntity(
    BlockPos pos,
    string code,
    (string, string) variant
  ) {
    var block = TestBlocks.Configure(new Block(), code, NextId(), variant);
    var be = new BlockEntityGeneric { Pos = pos.Copy(), Block = block };
    Scene.World.Place(pos, block, be);
    _line.Add((pos, code));
    return be;
  }

  private void Join(BlockPos pos, BlockEntity be, BEBehaviorMPBase node) {
    MechPower.Attach(be, node, Line);
    Line.nodes[pos.Copy()] = node;
  }

  /// <summary>An iron pressure-relief valve for <paramref name="pos"/>, input face first in
  /// <paramref name="orientation"/>, its output open to air; placed by <see cref="Install"/>.</summary>
  private BlockEntityPressureValve NewValve(BlockPos pos, string orientation) {
    var block = TestBlocks.Configure(
      new BlockPressureValve(),
      $"ppex:pipe-pressurevalve-{orientation}-iron",
      NextId(),
      ("type", "pressurevalve"),
      ("orientation", orientation),
      ("material", "iron")
    );
    ReflectionHelpers.SetProperty(block, "Type", "pressurevalve");
    ReflectionHelpers.SetProperty(block, "Orientation", orientation);
    return new BlockEntityPressureValve { Pos = pos.Copy(), Block = block };
  }

  /// <summary>Places <paramref name="valve"/>, which starts its tick, and dials it to
  /// <paramref name="gate"/> atm in the valve's own steps.</summary>
  private void Install(BlockEntityPressureValve valve, float gate) {
    Scene.Machine(valve.Pos, valve.Block, valve);
    ReflectionHelpers.SetProperty(
      valve,
      nameof(valve.NetworkSystem),
      Scene.World.Networks
    );
    while (valve.GatePressure < gate - 0.001f && valve.AdjustGatePressure(true)) { }
    while (valve.GatePressure > gate + 0.001f && valve.AdjustGatePressure(false)) { }
  }

  /// <summary>
  /// Lays <paramref name="main"/> in iron pipe, except the cells in <paramref name="passthroughs"/>,
  /// which are fireclay passthroughs (a bend where their two faces are not opposite).
  /// </summary>
  private void Place(StarterSteamPowerPlant.Main main, params BlockPos[] passthroughs) {
    foreach (var (pos, faces) in main.Cells) {
      string orientation = new("udnsew".Where(c => faces.Contains(c)).ToArray());
      if (passthroughs.Contains(pos))
        Passthrough(pos, orientation);
      else {
        EnginePlant.Pipe(Scene, pos, orientation, NextId());
        _placed.Add((pos, Scene.World.GetBlock(pos).Code.ToString()));
      }
      _pipes.Add(pos);
    }
  }

  private void Passthrough(BlockPos pos, string orientation) {
    string type = orientation is "ns" or "we" or "ud" ? "passthrough" : "passthroughbend";
    string code = $"ppex:pipe-{type}-fire-{orientation}";
    var block = TestBlocks.Configure(
      new BlockPipePassthrough(),
      code,
      NextId(),
      ("type", type),
      ("brick", "fire"),
      ("orientation", orientation)
    );
    ReflectionHelpers.SetProperty(block, "Type", type);
    ReflectionHelpers.SetProperty(block, "Orientation", orientation);
    Scene.Node(
      pos,
      block,
      new BlockEntityPipePassthrough { Pos = pos.Copy(), Block = block },
      "pipe"
    );
    _placed.Add((pos, code));
  }
}
