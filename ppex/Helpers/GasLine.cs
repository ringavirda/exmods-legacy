using ExpandedLib.Industry.Pipes;

namespace PipesAndPowerExpanded.Helpers;

/// <summary>
/// The pressure machines read from a gas run: the figure the run's last network tick settled at,
/// standing for the whole second until the next, so every machine reading it in that second reads
/// the same figure whichever order the game ticks them in. The run's own
/// <see cref="PipeNetworkState.Pressure"/> is a live figure instead, moving with every machine that
/// feeds or draws the run.
/// </summary>
public static class GasLine {
  /// <summary>
  /// The pressure (atm) <paramref name="run"/> settled at in its last network tick
  /// (<see cref="PipeNetworkState.SettledPressure"/>). 0 for no run, a run holding nothing and a
  /// water run; 0 also for a run first charged since the last network tick.
  /// </summary>
  public static float Pressure(PipeNetwork? run) =>
    run?.State is { IsLiquid: false } state ? state.SettledPressure : 0f;
}
