using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using ExpandedLib;
using ExpandedLib.Industry.Pipes;
using Vintagestory.API.Common;

namespace PipesAndPowerExpanded.Helpers;

/// <summary>
/// The pressure machines read from a water run, set by the machines that feed it and standing
/// between their ticks, so a reader gets one figure whichever order the game ticks them in. Every
/// machine that fed the run in its last tick - a pump, a condenser passing water on, an engine
/// returning condensate, a relief valve spilling into its output - records the head it fed at; when
/// any of them left the run brim-full, the run is held at the highest of those heads. A relief valve
/// that discharged the run in its last tick holds it down to its gate. The run's own
/// <see cref="PipeNetworkState.Pressure"/> is a live figure instead: the fill ratio, jumping to the
/// last producer's head the moment a fill brims the run and back the moment anything draws.
/// </summary>
/// <remarks>
/// Server-side state, kept in memory only: a reload starts every run unheld until its machines
/// tick again. A run whose pipe count changes - it gains or loses a pipe, merges or splits - drops
/// every record on it and is held again from the next tick of each machine on it. Each machine
/// feeds at most one run and relieves at most one run. A machine no longer in the world at its
/// position counts for nothing and is dropped when next read.
/// </remarks>
public static class WaterLine {
  /// <summary>One machine's record on a run: a feed at a head, or a relief down to a
  /// gate.</summary>
  private readonly record struct Holder(BlockEntity Machine, bool Relief);

  /// <summary>A record's pressure (atm), and whether the feed left the run brim-full.</summary>
  private readonly record struct Setting(float Pressure, bool Brim);

  /// <summary>The records on one run, made while it had <see cref="Pipes"/> pipes.</summary>
  private sealed class Records {
    public int Pipes;
    public readonly Dictionary<Holder, Setting> Set = [];
  }

  private static readonly ConditionalWeakTable<PipeNetwork, Records> Settings =
    new();

  /// <summary>The run each machine feeds.</summary>
  private static readonly ConditionalWeakTable<BlockEntity, PipeNetwork> Held =
    new();

  /// <summary>The run each relief valve relieves.</summary>
  private static readonly ConditionalWeakTable<BlockEntity, PipeNetwork> Relieved =
    new();

  /// <summary>
  /// Records the end of <paramref name="feeder"/>'s tick: it fed <paramref name="run"/> at
  /// <paramref name="head"/> atm, leaving it brim-full or not, or, with <paramref name="run"/>
  /// null, it fed no run; replacing the feed it recorded before.
  /// </summary>
  /// <param name="feeder">The pump, condenser, engine or valve; the key its feed is kept under.</param>
  /// <param name="run">Its delivery run, or null when it is not delivering.</param>
  /// <param name="head">The pressure it delivers at (atm).</param>
  public static void Hold(BlockEntity feeder, PipeNetwork? run, float head) =>
    Record(new Holder(feeder, false), run, new Setting(head, IsBrimFull(run)));

  /// <summary>
  /// Records <paramref name="valve"/>'s tick: it discharged <paramref name="run"/>, so it holds
  /// the run down to <paramref name="gate"/> atm, or, with <paramref name="run"/> null, it holds
  /// no run down; replacing the relief it recorded before.
  /// </summary>
  public static void Relieve(BlockEntity valve, PipeNetwork? run, float gate) =>
    Record(new Holder(valve, true), run, new Setting(gate, false));

  /// <summary>
  /// The pressure (atm) <paramref name="run"/> is pushed at: the highest head the machines feeding
  /// it fed at, when one of them left it brim-full; else its fill ratio (0 to 1). 0 for a run that
  /// holds no water.
  /// </summary>
  public static float Head(PipeNetwork run) {
    float head = 0f;
    bool brim = false;
    foreach (var (holder, setting) in Present(run))
      if (!holder.Relief) {
        head = Math.Max(head, setting.Pressure);
        brim |= setting.Brim;
      }
    if (brim)
      return head;
    float full = run.Nodes.Count * ExlibValues.LitresPerPipe;
    return run.State is { IsLiquid: true } state && full > 0f
      ? Math.Min(1f, state.Volume / full)
      : 0f;
  }

  /// <summary>
  /// The pressure (atm) a machine drawing water from <paramref name="run"/> reads, and the one a
  /// relief valve opens on: <see cref="Head"/>, held down to the lowest gate among the relief
  /// valves holding it.
  /// </summary>
  /// <param name="run">The run read.</param>
  /// <param name="except">A relief valve whose own relief is left out, or null for none.</param>
  public static float Pressure(PipeNetwork run, BlockEntity? except = null) {
    float pressure = Head(run);
    foreach (var (holder, setting) in Present(run))
      if (holder.Relief && holder.Machine != except)
        pressure = Math.Min(pressure, setting.Pressure);
    return pressure;
  }

  /// <summary>What the machines still in the world recorded on <paramref name="run"/> since its
  /// pipes last changed; drops the rest.</summary>
  private static List<(Holder, Setting)> Present(PipeNetwork run) {
    var present = new List<(Holder, Setting)>();
    if (!Settings.TryGetValue(run, out Records? records))
      return present;
    Current(run, records);
    List<Holder>? gone = null;
    foreach (var (holder, setting) in records.Set)
      if (
        holder.Machine.Api?.World?.BlockAccessor.GetBlockEntity(
          holder.Machine.Pos
        ) == holder.Machine
      )
        present.Add((holder, setting));
      else
        (gone ??= []).Add(holder);
    if (gone != null)
      foreach (Holder holder in gone)
        Record(holder, null, default);
    return present;
  }

  /// <summary>Drops every record on <paramref name="run"/> when its pipe count is not the one they
  /// were made at.</summary>
  private static void Current(PipeNetwork run, Records records) {
    if (records.Pipes == run.Nodes.Count)
      return;
    foreach (Holder holder in records.Set.Keys)
      if (
        (holder.Relief ? Relieved : Held).TryGetValue(
          holder.Machine,
          out PipeNetwork? on
        ) && ReferenceEquals(on, run)
      )
        (holder.Relief ? Relieved : Held).Remove(holder.Machine);
    records.Set.Clear();
    records.Pipes = run.Nodes.Count;
  }

  /// <summary>Whether <paramref name="run"/> carries water up to the brim of its pipes.</summary>
  private static bool IsBrimFull(PipeNetwork? run) =>
    run?.State is { IsLiquid: true } state
    && state.Volume >= run.Nodes.Count * ExlibValues.LitresPerPipe - 0.001f;

  private static void Record(Holder holder, PipeNetwork? run, Setting setting) {
    ConditionalWeakTable<BlockEntity, PipeNetwork> recorded = holder.Relief
      ? Relieved
      : Held;
    if (
      recorded.TryGetValue(holder.Machine, out PipeNetwork? before)
      && !ReferenceEquals(before, run)
    ) {
      if (Settings.TryGetValue(before, out Records? old))
        old.Set.Remove(holder);
      recorded.Remove(holder.Machine);
    }
    if (run == null)
      return;
    Records records = Settings.GetValue(
      run,
      r => new Records { Pipes = r.Nodes.Count }
    );
    Current(run, records);
    records.Set[holder] = setting;
    recorded.AddOrUpdate(holder.Machine, run);
  }
}
