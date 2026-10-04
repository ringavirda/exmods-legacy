using System.Text;
using ExpandedLib;
using ExpandedLib.Helpers;
using ExpandedLib.Industry.Helpers;
using ExpandedLib.Industry.Pipes;
using ExpandedLib.Machines;
using ExpandedLib.Registries;
using ExpandedLib.Structures;
using PipesAndPowerExpanded;
using PipesAndPowerExpanded.BlockNetworkPipe.BlockEntities;
using PipesAndPowerExpanded.Helpers;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace SteelmakingExpanded.BlockStructures.CowperStove.BlockEntities;

/// <summary>
/// Block entity for the cowper-stove multiblock - a regenerative heat exchanger.
/// It absorbs heat from the furnace exhaust into its brick core (faster with a
/// burning coal pile below), then reheats air passing through into hot blast that
/// boosts the blast furnace.
/// </summary>
[BlockEntityRegister]
public class BlockEntityCowperStove : BlockEntityMultiblockMachine {
  private BlockFacing _connectorFace = BlockFacing.SOUTH;
  private float _internalTemperature = 20f;
  private string _lastStatus = Lang.Get("smex:cowperstove-status-idle");

  // Whether the last tick soaked up furnace exhaust; synced for the roar.
  private bool _soaking;

  // Low roar of the regenerator soaking up furnace exhaust (client only).
  private readonly ExSoundLoop _heatSound = new(ExSounds.Fire, 0.4f, 24f);

  // Cached config tunables (see SmexValues) - read once at init instead of
  // re-reading the static config every production tick.
  private float _factorAnthracite;
  private float _factorOtherCoal;
  private float _factorDefault;
  private float _coolingSpeedExhaust;
  private float _coolingSpeedAir;
  private float _idleCoolingSpeed;
  private float _maxTemperature;
  private float _ambientTemperature;
  private float _intakeVolume;

  public override void Initialize(ICoreAPI api) {
    base.Initialize(api);

    CacheTunables();

    // The exhaust connector sits on the stove's local-south face, rotated with the block.
    _connectorFace = ExOrientation.RotateFacing(
      BlockFacing.SOUTH,
      ExOrientation.AngleFromSide(Block.Variant["side"])
    );
    UpdateHeatSound();
  }

  public override void OnBlockRemoved() {
    _heatSound.Dispose();
    base.OnBlockRemoved();
  }

  public override void OnBlockUnloaded() {
    _heatSound.Dispose();
    base.OnBlockUnloaded();
  }

  /// <summary>Plays the roar while the synced stove soaks up exhaust, stops it otherwise. Client
  /// only.</summary>
  private void UpdateHeatSound() =>
    _heatSound.Update(Api, Pos, StructureComplete && _soaking);

  // Pulls the gameplay tunables off the live config. Re-run each production tick (not just at load)
  // so a `/exmod config smex ...` change applies immediately, not only after the chunk reloads.
  private void CacheTunables() {
    _factorAnthracite = SmexValues.CowperHeatingSpeedAnthracite;
    _factorOtherCoal = SmexValues.CowperHeatingSpeedOtherCoal;
    _factorDefault = SmexValues.CowperHeatingSpeedDefault;
    _coolingSpeedExhaust = SmexValues.CowperCoolingSpeedExhaust;
    _coolingSpeedAir = SmexValues.CowperCoolingSpeedAir;
    _idleCoolingSpeed = SmexValues.CowperIdleCoolingSpeed;
    _maxTemperature = SmexValues.CowperMaxTemperature;
    _ambientTemperature = ExClimate.AmbientAt(this);
    _intakeVolume = SmexValues.CowperIntakeVolume;
  }

  #region Abstract implementations

  protected override void UpdateStructureRotation() {
    if (Block == null)
      return;

    // The cowper's structure layout faces opposite its "side" variant (the same +180
    // convention as the boiler body).
    SetStructureAngle(
      (ExOrientation.AngleFromSide(Block.Variant["side"]) + 180) % 360
    );
  }

  protected override string GetIncompleteMessage(int missingCount) =>
    Lang.Get("smex:structure-incomplete-count", missingCount);

  protected override string GetCompleteMessage() =>
    Lang.Get("smex:cowperstove-complete");

  #endregion

  #region Production tick

  protected override void OnProductionTick(float dt) {
    if (!StructureComplete)
      return;

    // Pick up any live `/exmod config` tunable change this tick.
    CacheTunables();

    // The stove is a fixed machine port: only draw from a run whose pipe actually
    // presents a connector back at the stove's exhaust face - a pipe merely routed
    // through the adjacent cell with its connectors pointing elsewhere is not
    // plumbed in (same reciprocity rule as the converter intake and the engines).
    var consumedExhaustVol = 0f;
    float inputExhaustTemp = _ambientTemperature;
    bool isReceivingExhaust = false;
    if (this.ConnectedNetwork<PipeNetwork>(_connectorFace) is { } exhaustNet) {
      inputExhaustTemp = exhaustNet.State?.Temperature ?? _ambientTemperature;
      consumedExhaustVol = exhaustNet.TryConsumeGas(
        _intakeVolume,
        Api.World.BlockAccessor
      );
      isReceivingExhaust = consumedExhaustVol > 0;
    }

    bool isAnthracite = false;
    bool hasOtherCoal = false;

    Block blockBelow = Api.World.BlockAccessor.GetBlock(Pos.DownCopy());
    if (blockBelow.Code?.Path.StartsWith("coalpile") == true) {
      if (
        Api.World.BlockAccessor.GetBlockEntity(Pos.DownCopy())
          is BlockEntityItemPile pile
        && pile.inventory != null
        && !pile.inventory[0].Empty
      ) {
        string? path = pile.inventory[0]?.Itemstack?.Collectible?.Code?.Path;
        if (path != null) {
          if (path.Contains("anthracite"))
            isAnthracite = true;
          else
            hasOtherCoal = true;
        }
      }
    }

    float airTemp = _ambientTemperature;
    string inGasType = "Air";

    BlockPos passthroughPos = GetGlobalPos(0, 1, 2);
    var passthrough =
      Api.World.BlockAccessor.GetBlockEntity(passthroughPos)
      as BlockEntityPipePassthrough;

    float passthroughVol = passthrough?.Volume ?? 0f;
    if (passthroughVol > 0) {
      airTemp = passthrough!.Temperature;
      inGasType = passthrough.Medium;
    }

    string newStatus = Lang.Get("smex:cowperstove-status-idle");
    bool soaking = false;

    if (isReceivingExhaust && passthroughVol > ExlibValues.LitresPerPipe) {
      // Air and exhaust both present. Closing the air valve cuts its supply but leaves the gas
      // already in the passthrough stranded there - and a pressurised run holds well over one pipe's
      // worth - which used to latch the stove in "mixing" forever and refuse to charge. Vent that
      // stranded gas: with the valve shut nothing refills it, so it clears within a tick and the
      // stove charges next tick; only a still-open valve keeps refilling it and stays flagged.
      newStatus = Lang.Get("smex:cowperstove-status-exhaustmix");
      passthrough?.TryConsume(passthroughVol);
    } else if (isReceivingExhaust) {
      newStatus = Lang.Get("smex:cowperstove-status-heatingup");
      float tempDiff = inputExhaustTemp - _internalTemperature;
      if (tempDiff > 0) {
        float factor = isAnthracite
          ? _factorAnthracite
          : (hasOtherCoal ? _factorOtherCoal : _factorDefault);
        // Heat-transfer rates are per-second; scale by dt for tick-independence.
        _internalTemperature += tempDiff * factor * dt;
        _internalTemperature = System.Math.Min(
          _internalTemperature,
          _maxTemperature
        );
        inputExhaustTemp -= tempDiff * _coolingSpeedExhaust * dt;
      }

      SpawnHeatingParticles();
      soaking = true;

      BlockPos exhaustOutletPos2 = GetGlobalPos(0, 0, 2);
      if (
        Api.World.BlockAccessor.GetBlockEntity(exhaustOutletPos2)
        is IPipeNode outlet2
      )
        outlet2.TryProduce(
          consumedExhaustVol,
          System.Math.Max(
            _ambientTemperature,
            inputExhaustTemp * SmexValues.CowperSpentExhaustTempFraction
          ),
          "Exhaust"
        );
    } else if (passthroughVol > 0) {
      newStatus = Lang.Get("smex:cowperstove-status-heating", inGasType);
      float tempDiff = _internalTemperature - airTemp;
      if (tempDiff > 0)
        airTemp = _internalTemperature;

      // The stove offers the hot outlet everything standing in the passthrough and takes from the
      // passthrough exactly what the outlet accepted.
      float passed = 0f;
      BlockPos hotAirOutletPos = GetGlobalPos(0, 1, 0);
      if (
        Api.World.BlockAccessor.GetBlockEntity(hotAirOutletPos)
        is IPipeNode hotOutlet
      ) {
        // The run is read as settled after this stove's draw; the ceiling adds that draw back (as a
        // pressure over the run's capacity) so the stove does not hold its own outlet under the main.
        PipeNetwork? passthroughRun = this.NetworkAt<PipeNetwork>(passthroughPos);
        float own =
          passthroughRun?.State is { MaxVolume: > 0f } runState
            ? _intakeVolume / runState.MaxVolume
            : 0f;
        float inputPressure =
          passthrough != null
            ? GasLine.Pressure(passthroughRun) + own
            : 1f;
        PipeNetwork? hotRun = this.NetworkAt<PipeNetwork>(hotAirOutletPos);
        float hotBefore = hotRun?.State?.Volume ?? 0f;
        hotOutlet.TryProduce(
          passthroughVol,
          airTemp,
          inGasType,
          maxOutputPressure: inputPressure > 1f ? inputPressure : 1f
        );
        passed = System.Math.Max(0f, (hotRun?.State?.Volume ?? 0f) - hotBefore);
        if (passed > 0f)
          passthrough!.TryConsume(passed);
      }

      if (tempDiff > 0) {
        // Scaled by the air that passed through the brickwork this second against the rated intake:
        // a full intake or more costs the rated heat, a trickle proportionally less, and a second
        // the outlet refused costs none.
        float drawn = System.Math.Min(passed, _intakeVolume);
        float flowShare = _intakeVolume > 0f ? drawn / _intakeVolume : 1f;
        _internalTemperature -= tempDiff * _coolingSpeedAir * flowShare * dt;
      }
    }

    // Ambient loss, applied whatever the stove is doing. The three branches above cover mixing,
    // charging and delivering and have no else, so a stove with neither gas flowing never touched
    // its temperature at all and held its charge across days and reloads - a regenerator that could
    // be filled once and banked. Brickwork this size still holds heat for a long while; it just
    // cannot hold it forever.
    _internalTemperature -=
      (_internalTemperature - _ambientTemperature) * _idleCoolingSpeed * dt;
    if (_internalTemperature < _ambientTemperature)
      _internalTemperature = _ambientTemperature;

    if (_lastStatus != newStatus || _soaking != soaking) {
      _lastStatus = newStatus;
      _soaking = soaking;
      MarkDirty(true);
    }

    UpdateHeatsinks();
  }

  private void SpawnHeatingParticles() {
    // Spawn the heat column over the central interior column (structure-local
    // (0, *, 1) - the heatsink stack), rotated the same way GetGlobalPos resolves
    // it. Mirrors InitForUse(_currentAngle) applied to (x:0, z:1).
    Vec3i d = ExOrientation.RotateOffset(0, 0, 1, _currentAngle);
    int dx = d.X,
      dz = d.Z;

    Vec3d minPos = new(Pos.X + dx + 0.1, Pos.Y + 0.1, Pos.Z + dz + 0.1);
    Vec3d maxPos = new(Pos.X + dx + 0.9, Pos.Y + 3.9, Pos.Z + dz + 0.9);

    ExParticles.RisingPlume(
      Api.World,
      ExParticles.GlowSpark,
      minPos,
      maxPos,
      new Vec3f(-0.2f, 0.5f, -0.2f),
      new Vec3f(0.2f, 1.5f, 0.2f),
      4,
      8,
      1f,
      -0.05f,
      0.2f,
      0.5f,
      new EvolvingNatFloat(EnumTransformFunction.LINEAR, -150f),
      new EvolvingNatFloat(EnumTransformFunction.LINEAR, -0.2f)
    );
  }

  private void UpdateHeatsinks() {
    for (int y = 0; y <= 3; y++) {
      BlockPos hsPos = GetGlobalPos(0, y, 1);
      if (
        Api.World.BlockAccessor.GetBlockEntity(hsPos) is BlockEntityHeatSink hs
        && System.Math.Abs(hs.Temperature - _internalTemperature) > 1f
      ) {
        hs.Temperature = _internalTemperature;
        hs.MarkDirty(true);
      }
    }
  }

  #endregion

  #region HUD

  public override void GetBlockInfo(IPlayer forPlayer, StringBuilder dsc) {
    if (!StructureComplete) {
      dsc.AppendLine(Lang.Get("smex:structure-incomplete"));
      return;
    }
    dsc.AppendLine(Lang.Get("smex:cowperstove-info-status", _lastStatus));
  }

  #endregion

  #region Serialization

  public override void ToTreeAttributes(ITreeAttribute tree) {
    base.ToTreeAttributes(tree);
    tree.SetFloat("internalTemperature", _internalTemperature);
    tree.SetString("lastStatus", _lastStatus);
    tree.SetBool("soaking", _soaking);
  }

  public override void FromTreeAttributes(
    ITreeAttribute tree,
    IWorldAccessor worldForResolving
  ) {
    base.FromTreeAttributes(tree, worldForResolving);
    _internalTemperature = tree.GetFloat("internalTemperature");
    _lastStatus = tree.GetString(
      "lastStatus",
      Lang.Get("smex:cowperstove-status-idle")
    );
    _soaking = tree.GetBool("soaking");
    UpdateHeatSound();
  }

  #endregion
}
