using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ExpandedLib.Testing;
using Vintagestory.API.Common;

namespace Integration.Tests.Setups;

/// <summary>
/// Places a test world's tick listeners in a chosen order and runs the network tick at a chosen slot
/// among them, the way listener phases fall in a live server's second.
/// </summary>
internal static class TickSlots {
  private const BindingFlags Private =
    BindingFlags.Instance | BindingFlags.NonPublic;

  /// <summary>
  /// Moves every tick listener <paramref name="owners"/> registered to the end of
  /// <paramref name="world"/>'s firing order, in the order given; the rest keep their order ahead
  /// of them.
  /// </summary>
  public static void Order(TestWorld world, params BlockEntity[] owners) {
    IDictionary listeners = Listeners(world);
    var entries = listeners
      .Keys.Cast<long>()
      .Select(id => (Id: id, Listener: listeners[id]!))
      .ToList();
    var moved = owners
      .SelectMany(o => entries.Where(e => Owner(e.Listener) == o))
      .ToList();
    listeners.Clear();
    foreach (var (id, listener) in entries.Except(moved).Concat(moved))
      listeners.Add(id, listener);
  }

  /// <summary>
  /// The slot of the first tick listener <paramref name="owner"/> registered: a network tick run
  /// at it runs just before that machine.
  /// </summary>
  /// <exception cref="ArgumentException"><paramref name="owner"/> registered no listener.</exception>
  public static int Before(TestWorld world, BlockEntity owner) {
    int slot = Owners(world).IndexOf(owner);
    return slot >= 0
      ? slot
      : throw new ArgumentException("no listener of that block entity", nameof(owner));
  }

  /// <summary>
  /// One second of <paramref name="world"/>: every tick listener once, in order, with the network
  /// tick run before the listener at <paramref name="slot"/>, or after all of them when the slot is
  /// past the last.
  /// </summary>
  public static void Step(TestWorld world, int slot) {
    var callbacks = Values(world).Select(Callback).ToList();
    for (int i = 0; i < callbacks.Count; i++) {
      if (i == slot)
        world.Tick(1);
      callbacks[i](1f);
    }
    if (slot >= callbacks.Count)
      world.Tick(1);
  }

  private static List<BlockEntity?> Owners(TestWorld world) =>
    Values(world).Select(Owner).ToList();

  private static List<object> Values(TestWorld world) {
    IDictionary listeners = Listeners(world);
    return listeners.Keys.Cast<long>().Select(id => listeners[id]!).ToList();
  }

  private static IDictionary Listeners(TestWorld world) =>
    (IDictionary)
      typeof(TestWorld).GetField("_tickListeners", Private)!.GetValue(world)!;

  private static Action<float> Callback(object listener) =>
    (Action<float>)listener.GetType().GetProperty("Callback")!.GetValue(listener)!;

  /// <summary>The block entity a listener's callback belongs to, or null.</summary>
  private static BlockEntity? Owner(object listener) =>
    Callback(listener).Target switch {
      BlockEntity be => be,
      BlockEntityBehavior behavior => behavior.Blockentity,
      _ => null,
    };
}
