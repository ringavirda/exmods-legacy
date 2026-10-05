# Recent changes

What plays differently after moving a world or a build to a newer release of Pipes and Power
Expanded (`ppex`) and Steelmaking Expanded (`smex`), newest release first. Each change links to the
page that describes it as it stands now. The full list of changes, fixes included, is on the
[Changelog](/current/Changelog/).

## Moving to ppex 0.7.1 and smex 0.10.1 (2026-09-30)

- A boiler's fire needs a chimney or a smoke stack on its exhaust run. Open pipe ends carry the
  exhaust away but give no draught, so a boiler that vented through an open end is choked and its
  fire goes out after 10 s. See [Boilers](/current/ppex/mechanics/boilers/#fire-and-exhaust).
- Every open pipe end leaks: 8 L/s of gas at 1 atm, in proportion to the run's pressure, and
  10 L/s of water. A boiler on a steam run with an open end, or with an unpiped steam outlet, blows
  down to about 1 atm, so an engine on that run never engages. See
  [Pipes and pressure](/current/ppex/mechanics/pipes-and-pressure/#open-ends).
- A blast furnace without blast climbs only while a chimney or a smoke stack stands on one of its
  exhaust runs. See [Blast furnace](/current/smex/mechanics/blast-furnace/#exhaust).
- A blowing Cowper stove passes on only the air its blowers deliver. A furnace that ran on the
  stove's extra air now needs blowers that carry its whole draw. See
  [Hot blast](/current/smex/mechanics/hot-blast/#discharging).
- A mold on a mold pedestal or under a canal tap hardens about as fast as the same mold filled by
  hand, and a mold lifted off keeps that pace. Metal standing in the canals still holds its heat.
  See [Casting](/current/smex/mechanics/casting/#cooling).

## Moving to ppex 0.7.0 and smex 0.10.0 (2026-09-29)

- Back up a world before moving it to these versions: it cannot go back to ppex 0.6.9 and smex
  0.9.9 afterwards.
- A part turned out of its layout stops the structure until it is turned back. This holds for
  boilers, blast furnaces, Cowper stoves and Bessemer converters already standing in a world: a
  boiler's coke oven door faces out and its flue passthroughs run along the flue, and each part of
  the furnace, the stove and the converter faces the way its layout sets. ctrl+shift+right-click
  marks a turned part red in the build outline. See [Cornish Boiler](/current/ppex/structures/cornish/)
  and [Blast furnace](/current/smex/mechanics/blast-furnace/).
- A wrench turns the blast furnace tap, the Cowper heat sink and the converter's gas intake and
  transmission a quarter turn, keeping what the part holds. See
  [Blast Furnace Tap](/current/smex/blocks/blastfurnace/tap/).
- A construction stage takes one kind of material: four planks of one wood, not two oak and two
  pine. Machines already part-built follow the same rule.
- Steam, air and exhaust in a pipe run cool 2 C a second toward 20 C. Pressure, and with it what an
  engine makes, is unchanged; a Cowper stove charged through a long exhaust run heats a little
  slower. See [Hot blast](/current/smex/mechanics/hot-blast/#charging).
- On game 1.20 and 1.21, metal in a canal, a mold, a barrel and the converter cools and plugs, as it
  does on 1.22. See [Molten metal](/current/smex/mechanics/molten-metal/#temperature).
- Adding Iron Industry Expanded or Steel Industry Expanded to a world closes ppex and smex: what is
  built keeps working, nothing new can be built, and their items are removed. See
  [Compatibility](/current/ppex/mechanics/compatibility/#iron-industry-expanded-and-steel-industry-expanded).
- Furnace parts that close a wall now close a room: the blast furnace tap on every face, the smoke
  stack intake around its pipe ends, the hopper bell's four sides, the converter transmission's top,
  and a canal's floor and the sides its run does not pass through. A room walled in part by them
  counts as enclosed, as a cellar or a greenhouse needs.

## Moving to ppex 0.6.9 and smex 0.9.9 (2026-09-23)

- The [Watt Engine](/current/ppex/blocks/engine/watt/) recipe costs its two gears, in the top-left
  slot beside the hammer.
- The [Mechanical Fluid Pump](/current/ppex/blocks/mpfluidpump/) and
  [Twin-Tub Blower](/current/smex/blocks/blastfurnace/mpblower/) recipes, and the blower's beam
  stage, take wooden support beams only.

## Moving to ppex 0.6.8 and smex 0.9.8 (2026-08-13)

- Roasted iron ore counts 14 ore units in the burden against raw ore's 12. See
  [Blast furnace](/current/smex/mechanics/blast-furnace/#the-charge).
- With Industrial Story installed, the furnace takes that mod's crushed and roasted ores and its
  roasted nuggets, and no longer takes vanilla crushed iron. See
  [Compatibility](/current/smex/mechanics/compatibility/).

## Moving to ppex 0.6.7 and smex 0.9.7 (2026-08-09)

- A blast furnace melts iron on cold blast. Cowper stoves raise the hearth's ceiling and speed the
  melt; they are no longer needed to make iron. See
  [Blast furnace](/current/smex/mechanics/blast-furnace/#heat-and-melt-rate).
- Melting is a rate, set by how far the hearth is over iron's melting point and how much of the
  blast it asks for arrives; the look-at panel shows both.
- The furnace yields 102 units of iron per melt cycle, against a bloomery's 60 for the same ore,
  and takes raw limonite, hematite and magnetite nuggets beside crushed ore. See
  [Blast furnace](/current/smex/mechanics/blast-furnace/#the-charge).
- A fed furnace runs until it is stopped. A full reservoir or a blocked flue stalls it until it is
  tapped or the exhaust reopens, and burden is never turned to slag.
- The furnace needs blast from the first pile that catches, and losing blast puts it out after the
  disruption grace. See [Blast furnace](/current/smex/mechanics/blast-furnace/#the-blast).
- The converter remelts iron and steel scrap charged at its upper hatch. Scrap cools the bath, and
  more blast pressure carries more of it and runs the blow faster. See
  [Bessemer converter](/current/smex/mechanics/bessemer-converter/#pressure-is-the-throttle).
- Iron bits no longer crush into crushed iron; scrap goes to the converter. See
  [Slag](/current/smex/mechanics/slag/#scrap).
- Canals deliver over longer runs. See
  [Molten metal](/current/smex/mechanics/molten-metal/#how-a-run-flows).
- Gearing up the [Twin-Tub Blower](/current/smex/blocks/blastfurnace/mpblower/) is the intended
  build, with a falling return. The steam Air Blower feeds several furnaces.
- Cowper stoves cool while idle and drain faster under a heavy blast. See
  [Hot blast](/current/smex/mechanics/hot-blast/#running-a-pair).
- Blast mix is called burden; existing stacks convert on load.
- The blast furnace door, tuyeres and molten metal taps come in the refractory tier of the brick
  their recipe spends, and carry the tier in their name. Doors, tuyeres and taps placed before
  became tier 3. See [Blast Furnace Door](/current/smex/blocks/blastfurnace/door/).

## Moving to ppex 0.6.6 and smex 0.9.6 (2026-08-09)

- A blast furnace runs without steam power. The
  [Twin-Tub Blower](/current/smex/blocks/blastfurnace/mpblower/) is driven by an axle from a
  waterwheel or windmill and raises its run to 2 atm; the furnace fires at 1.5 atm. The Bessemer
  converter still needs 2.5 atm, so steel still needs steam. See
  [Blast furnace](/current/smex/mechanics/blast-furnace/#the-blast).
- The furnace burns charcoal as well as coke, four charcoal where two coke would do, and takes coke
  whole. Crushed coke turns into coke as chunks load.
- Every refractory structure but the Bessemer converter takes any brick tier.
- A blocked flue stalls the furnace instead of putting it out. See
  [Blast furnace](/current/smex/mechanics/blast-furnace/#exhaust).
- The [Mechanical Fluid Pump](/current/ppex/blocks/mpfluidpump/) fills a boiler from an axle,
  without steam.
- Engines sharing a shaft carry more load together than one alone. See
  [Steam engines](/current/ppex/mechanics/steam-engines/).

Older releases are on the [Changelog](/current/Changelog/).
