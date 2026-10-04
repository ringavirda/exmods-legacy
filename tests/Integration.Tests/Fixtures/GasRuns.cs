using ExpandedLib.Industry.Pipes;

namespace Integration.Tests;

internal static class GasRuns {
  /// <summary>
  /// Settles <paramref name="run"/> at its live pressure, as a network tick with no leaks or vents
  /// would leave it, so a machine ticked by hand next reads what was charged. Mutates the run's
  /// state; a run with no state is left as it is.
  /// </summary>
  public static void Settle(this PipeNetwork run) {
    if (run.State is { } state)
      state.SettledPressure = state.Pressure;
  }
}
