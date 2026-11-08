# Race Display (Space Engineers 2 client plugin)

Place a box in the world (or round a checkpoint structure) and race laps through it. The first pass starts the race, and each pass after that completes a lap. The HUD looks like the game's own SPD panel:

- **Timer panel (top centre):** lap number, current lap time in big digits, and total race time. To its left, your **best lap** on this course; to its right, your **best race** (whole race time) on this course at the current lap count. How to start is shown underneath.
- **Lap list (left):** every lap time. Your best lap in this race is green, and the others show how much slower they were in grey (`+4.863 vs best`); real penalties are red.

**HUD drawing:** the panels use the game's own UI system (Avalonia, the same one as the SPD/ALT boxes), so they're properly dark with bright text in the game's font. They hide when you hide the game HUD, and game menus draw over them. If the game UI can't be reached (e.g. after a game update), the plugin falls back to the older debug-draw panels and says why in the log. Set `"UseGameUi": false` to force the fallback.

**Practice laps:** set *Practice laps* on the Settings tab (G > Racing Display). The first pass through the gate starts practice. Each practice lap is timed, with penalties, but isn't added to the lap list. The timer panel reads `PRACTICE 1 / 2`. When you complete the last practice lap at the gate, the race starts on that same pass with **GO!** and lap L1. Practice laps don't count towards the race total or your best times.

**Ghost, racing line and sectors:**
- **Ghost:** once you've set a best lap, a see-through ghost of the ship that set it flies the lap alongside you (same heading and roll), with your gap to it. Turn "Ghost as ship projection" off for a small purple diamond instead.
- **Racing line:** a faint purple line through the world along your best lap's path (near you only).
- **Sectors:** the splits panel has a SECTOR column (time from the previous checkpoint): purple for the best ever, green for the best this race. After the finish the table shows sector times per lap (switchable back to lap times on the Settings tab).
- **Speed graph:** the results show your speed round the lap for your best lap this race against the course record lap. Numbered dashed lines mark the checkpoints, with a dot on each lap's line where it went through them.

**Race stats:** after the finish the splits panel adds G MAX, G AVG, BRAKE and DIST rows per lap, and a separate RACE STATS box in the top corner (Settings > Display > Race stats) (ship, vs ship best, distance, max/avg turn G, max braking, time near top speed, average lap, consistency).

**Ship comparison:** each finished race saves best lap / best race times for the ship you flew (by grid name). Records tab > Ships on this course lists them fastest first with the gap, top speed and max turn G, and a Delete button per ship. The race history also shows which ship set each time.

**Results: overall or this ship:** Ctrl+B switches the race results between the course best (any ship) and the best in the ship you flew (headline, best lap colour, possible lap from that ship's sectors, graph against that ship's best lap). Also Settings > Display > Results against this ship.

**Ship replays:** Records tab > Ships on this course > **Watch** replays that ship's best lap (recorded from this version on).

**Ghost files (share your line):** Records tab > Ghost files. **Export** (course best, or per ship) writes a `.rdghost.json` file to `%AppData%\SpaceEngineers2\AppData\RaceDisplay\Ghosts` (Open folder button). Copy other players' files into that folder and press Reload; each listed ghost can be **Watched** (spectator replay) or **Raced** against (the ghost, racing line and live gap follow it). Set a pilot name to have it on your exports. A ghost only fits the course it was flown on: same world position of the start gate (within 50 m) and the same number of checkpoints.

**Courses are kept in each world's save:** the course, the courses saved on the Courses tab, and which way round it's flown are kept only in the world they were made in, in `RaceDisplayCourses.json` in that world's save folder. They are not stored in `RaceDisplay.json` (which keeps your settings, keys and records), so a course never turns up in another world, even one with the same name. The file is written every time the game saves the world (Save from the menu, or an autosave), a couple of seconds after the save; if you leave without saving, course changes since the last save are lost, like the rest of the world. Save As takes the course to the new save. Courses tab > **Save to world** writes it straight away; **Back to the saved course** puts back the one from the last save. Publish the world and players with the plugin get the course set up automatically. The save folder is found from the game's session objects or by searching `%AppData%\SpaceEngineers2` for a folder named after the world; the log says which folder was used.

**Course edit mode:** Ctrl+F (or Courses tab > Course edit mode). Racing stops; the timer box shows the tool, and the game's debug draw is switched on so the gate and checkpoint boxes show (put back as it was when you leave). Aim at a grid (it's outlined) and **left click** to make it the start/finish gate or the next checkpoint (fitted to its ring opening); **right click** a gate/checkpoint to remove it. Hold **R** for the tool wheel (Start / Finish gate, Start gate, Finish gate, Checkpoint, and the switches In the air and Racing line, shown orange when on): it's the game's own radial menu (like the paint gun's), so the cursor shows and the view stays still; point at a tool and let go of the key (or click it); Esc or a click outside cancels. Edits are written into the world save too when that's on. A separate **finish gate** makes a point-to-point (sprint) course: the race starts at the start gate and ends at the finish gate, one run (lap count and practice laps don't apply); Start / Finish gate turns it back into a circuit. Ctrl+F turns edit mode off.

**Seeing the course while editing:** gates and checkpoints are always outlined in edit mode (no debug draw needed), and white lines join them in flying order with arrows showing the way round and each leg's length. A **COURSE** panel lists the course type, straight-line length (and race length for laps), each leg, your best lap, and warnings: no start gate, checkpoints under 25 m apart, or a ring turned side-on to the way you arrive. **Racing line** on the wheel (or Settings > Gates and checkpoints > Best lap line in edit mode) shows or hides your best lap's line while editing.

**Gates and checkpoints in the air:** turn on **In the air** on the wheel and every tool places in open air instead of on a grid: left click puts the start / finish gate, start gate, finish gate or next checkpoint as a square ring ahead of you along your view (Air gate distance, default 40 m), facing the way you look. The outline shows where it will go. The grow / shrink keys change its size (Air gate size, default 30 m). Right click removes them like any other. Turn In the air off to go back to picking grids.

**Reverse course:** Courses tab > **Reverse**, or **Ctrl+Shift+I** in edit mode. The checkpoints go in the opposite order (the last becomes CP 1), every gate is turned round, and on a point-to-point course the start and finish swap. The reversed course has its own best times, ghost and records; reversing it again brings the original's back. Saved courses and the world save remember which way round they are.

**Undo / redo:** **Ctrl+U** undoes the last course edit in edit mode (placing, removing, resizing), **Ctrl+Shift+U** redoes it; up to 100 steps, kept while the game runs. (Not Ctrl+Z, which the game uses to undo building.)

**Best-lap replay:** Records tab > **Watch best lap**, or Ctrl+Shift+L. When you close the menu, the game's spectator camera follows the ghost of your ship round your best lap at real speed, with a REPLAY timer. It puts you back in your seat at the end, or press Ctrl+Shift+L again to stop early. Racing is paused while it plays.

**Replay camera:** by default the camera orbits: it circles the ghost and always aims at it while moving with it. Move the mouse (or A/D, up/down) to swing round it, and W/S to zoom in and out (3 to 500 m). **Ctrl+Shift+C** switches to a fixed chase camera behind the ghost and back (Settings > Ghost and replay > Orbit camera keeps the choice).

**Leaving a replay:** press **Esc** (the pause menu doesn't open) or the replay key.

**Cinematic camera:** replays switch on the game's cinematic spectator (from the Hostile Encounters update): camera movement is smooth and eased, and the cinematic look-around keys turn the view away from the ghost while it keeps tracking. If you lock the cinematic camera onto a real ship or player with the game's own lock key, the game drives the camera until you unlock it. Your own cinematic setting is put back when the replay ends. Turn it off in Settings > Ghost and replay > Cinematic camera.

**Sector colours:** the checkpoint dots under the timer take the colour of each sector as you fly it, like F1 timing: purple = fastest ever on this course, green = your best this race, yellow = slower (also in the splits). Settings > Display > Sector colours.

**Racing line colours:** Settings > Display > Racing line colours. **Speed**: the best lap's line is red where it was flown slowly, through yellow, to green where it was fastest. **Braking**: red where the lap was slowing down (the harder, the redder), green where it was speeding up, white where it held its speed, so you can see where it braked and how early it got back on the power. **Plain**: magenta.

**Minimap:** a small round map of the course in the top left corner (top right with swapped sides). The course is fitted inside the circle. From 20 m/s it's zoomed in and follows you (Minimap zoom while moving, default 2 km across); below 20 m/s it gradually zooms back out, to the whole course at 4 m/s. The next checkpoint has a pulsing ring and a faint dashed line from you to it; missed checkpoints turn red. There is a label by each gate and checkpoint (S/F or START / FINISH, then 1, 2, 3...); its racing line uses the racing line colours: the route between the gates and checkpoints (dashed; the next checkpoint orange, flown ones green, the start gate cyan, a finish gate purple), the best lap's line, you (the arrow, turning with your ship) and the ghosts (purple, and orange for a 2nd ghost). The start gate faces up the map. Settings > Minimap.

**Race Map:** G (the terminal) > Map > **Race Map** shows the minimap's view of the whole course as big as the page: the route, the racing line in its colours, each gate and checkpoint with its label (the next one ringed), you and the ghosts. It updates live while it's open. Switches above the map show or hide the sectors, checkpoints, their labels, the route, the racing line, the ghosts and you (remembered in `RaceMap.json`).

**Named sectors:** G > Racing Display > Courses > Sectors: pick where a sector starts and ends (the gate, then the checkpoints in order), give it a name ("Canyon run") and press Add. Each sector shows on the minimap and the Race Map as a wide coloured band along the route with its name. They're saved with the course in the world (when the game saves), go with named courses, and turn round with Reverse course.

**2nd ghost:** Records tab > **2nd ghost** on a ship's row (its best lap) or on a ghost file adds a second, orange ghost that flies at the same time as the main one, in races and in replays. Its racing line is drawn too, in orange; while a 2nd ghost is on, the main line is purple (instead of speed colours) so the two lines are easy to tell apart. Press it again (shows "2nd: on") to turn it off; it's cleared when the course changes.

**Ghost looks like my ship:** Settings > Ghost and replay. The ghost becomes a real hologram copy of your ship (the game's own paste preview, block models and all; it has no collisions and is drawn as a hologram, the only way the game draws a ship copy that isn't a real grid; Settings > Ghost and replay > Ship ghost opacity sets how see-through it is) flying along the recorded line, in races and replays. The copy is taken in the background when you sit in the ship (like Ctrl+C, nothing is pasted); the plugin borrows the build tool's hologram maker, so pick up the build tool once per session if the log says it can't find it. If the hologram can't be made it falls back to the outline below. The ghost (while racing and in replays) is drawn as a copy of the ship you're flying (or the last ship you sat in, remembered between sessions), made from its blocks, instead of the ship that set the lap. Useful to see where your new build would sit against an old best.

**Courses tab** (G > Racing Display > Courses):
- **Save / load:** give the current course a name and save it; load or delete saved courses for this world.
- **Checkpoint order:** move any checkpoint up or down, or delete a single one.
- **Auto-build:** finds grids, or antennas on them, named `Start` / `Finish` / `Start/Finish` and `CP1`, `CP 2`, `Checkpoint 3` or just `4`, and builds the course in number order, fitted to their rings.
- **Antennas:** switch every antenna in the world off (their HUD markers disappear) or back on.

**Display features:**
- **Live gap:** the gap under the timer compares you with your best lap continuously (green = ahead, red = behind), using the path of your best lap. It needs one recorded best lap on the course first; until then it updates at each checkpoint.
- **Checkpoint labels and arrow:** each checkpoint and the gate get a HUD label at their place on screen. The one you're heading for is bigger and shows its distance. When it's off screen or behind you, an arrow at the screen edge points to it.
- **Missed checkpoint warning:** fly through a checkpoint after skipping one and you get a red **MISSED CP 3! +5s** straight away. The skipped checkpoint's dot turns into a red ring and its split shows MISSED.
- **F1 timing colours:** in the splits, **purple** is the best ever on this course, **green** is the best in this race, and white is slower. The final table also has TOP and AVG speed rows.
- **Possible lap:** your best sector times added up, shown under BEST LAP and in the results.
- **Results** now sit under the lap list, out of the way of the crosshair. They include possible lap and top/average speed.
- **Lap list penalties** are short (`+10s`), with a red ring per missed checkpoint and a red cross per crash.
- **Records tab** (G > Racing Display): your best 5 races per lap count on this course, with total, best lap, penalties, top speed and date. The reset buttons are here too.
- **Panels:** on the Settings tab you can hide the lap list, the splits or the best boxes, swap sides, and turn the checkpoint labels/arrow and the sounds on or off.
- **Sounds:** beeps for the countdown, GO, checkpoints, laps, missed checkpoints, crashes, new best laps and new records. They use the game's UI sounds. Other game sound events can be set in `RaceDisplay.json` (`SoundCountdown`, `SoundGo`, `SoundCheckpoint`, `SoundLap`, `SoundBest`, `SoundMissed`).

**Race HUD extras:**
- **Live gap:** at each checkpoint, and at the end of each lap, the timer panel shows the gap to your best lap on this course, e.g. `-1.204` in green (ahead) or `+0.380` in red (behind).
- **Splits panel (right):** your time at each checkpoint this lap, with the gap to your best lap's split. After the finish it becomes a table of every lap's time at each checkpoint (last 5 laps), with the fastest time at each checkpoint in green, missed ones as a red `--`, and a LAP row with each lap's time.
- **Progress dots:** one dot per checkpoint under the lap time. Green = hit, orange = next, grey = still to come.
- **Next checkpoint:** during a race, the next checkpoint (lowest number not yet hit) has a bigger, brighter marker with its distance; the others are dimmed. Checkpoints still count in any order.
- **Countdown start:** stop your ship within 50 m of the start gate (`CountdownDistance`) and hold still for a second, and you get 3-2-1-GO. GO is your signal to launch; the timer starts when you fly through the gate. Moving before GO cancels the countdown. After a race, fly away from the gate area before you can count down again. You can always skip the countdown and just fly through the gate.
- **Results panel:** at the finish, the total time, best lap and penalties, plus your record for this course and lap count (`NEW RECORD!` when you beat it). It fades out after 10 seconds (`ResultsSeconds`).
- **Effects:** messages fade in and out; the timer border flashes green on a best lap and red on a penalty.

Records (best lap, splits, best race per lap count) are kept per **course**: the world, the number of checkpoints, and where the start gate is. Change the course and the records start fresh. Records tab > Reset clears the current course's records.

**Fitting rings and arches:** when you place a gate or checkpoint round a grid, the plugin looks at the grid's blocks for an enclosed hole (the opening of a ring, arch or frame). If it finds one, the box fits just that opening, and legs, posts and supports are ignored, so only flying through the hole counts. Grids with no enclosed hole get a box round the whole grid as before. Set `"FitToOpening": false` to always use the whole grid.

**Markers:** every checkpoint has a diamond marker with a small `CP 3` label underneath, orange until you've flown through it this lap and green after. The start/finish gate has a cyan `START / FINISH` marker. Only the place you're heading shows its distance: the nearest checkpoint still to fly through during a race, or the start/finish gate before a race and after the last checkpoint. Markers show through terrain, without debug draw, and with the HUD hidden. They stay the same size on screen at any distance (`MarkerSize` in the settings file). Ctrl+N turns them off.

**Gate and checkpoint outlines** only show when the game's debug draw is on, so turn it on while you build a course and off to race. The timer and lap panels always show.

**Race messages** (GO!, checkpoints, crashes, lap times) drop down in a tab under the timer. If the left column (minimap, lap list and results) is taller than the screen, it's scaled down to fit. Setup messages (gate placed, etc.) appear in the centre.

## Controls

Press **G** (the terminal screen) and pick the **Racing Display** tab after Map; **J** opens it on its Race page (once you've opened the terminal with G in this session). If the terminal tab can't be added (game update), it falls back to a Racing Display button in the Esc pause menu. It opens a screen laid out like the game's Settings, with Race, Records, Courses, Settings and Controls tabs. **Race** (also opened with **J**) sets the laps and practice laps and picks whose lap you race against: your course best, any ship's best lap on this course, or a ghost file. The ghost, racing line and live gap follow it. The other tabs:

- **Controls:** click a shortcut, then press the new key combination (hold Ctrl / Shift / Alt with it if you want them). Esc, or clicking it again, cancels. **Default** next to a changed shortcut puts it back.
- **Settings:** sliders and switches for missed-checkpoint and crash penalties, crash sensitivity, the countdown distance and hold time, laps per race, gate fitting, markers, HUD size, panel darkness and more. Point at a setting to see what it does; the search box at the top finds settings by name or description. Changes apply straight away and are saved.

**Reset to default** resets the tab you're on.

**Guide:** press **F1** (the game's Help screen) and pick **Racing Display**, under Controls. It explains how to set up and race a course and what every colour, dot, marker and panel means. (If the Help screen can't be extended, the guide shows as an Info tab in the Racing Display menu instead.)

A key used by two actions shows in red, a changed key in yellow. Your bindings are saved in `RaceDisplay.json` (`KeyBindings`).

Defaults:

| Keys | Action |
|---|---|
| **Ctrl+F** | Course edit mode on/off (aim + left click to set a gate or checkpoint, right click to remove) |
| **R** (hold, in edit mode) | Tool wheel: Start/Finish, Start gate, Finish gate, Checkpoint, In the air, Racing line |
| **Ctrl+U** / **Ctrl+Shift+U** (edit mode) | Undo / redo course edits |
| **Ctrl+Shift+I** (edit mode) | Reverse the course |
| **Ctrl+]** / **Ctrl+[** (edit mode) | Make the start gate 25% bigger / smaller |
| **Ctrl+R** | Stop and reset the race (outside edit mode) |
| **J** | Race setup: opens the Racing Display menu on its Race tab (laps, practice laps, whose lap the ghost and racing line follow). |
| **U** (the game's HUD key) | Cycles full HUD, minimal, **race HUD only** (the game's own HUD hidden: speed / altitude, toolbar, character status, hints, notifications; only the race HUD shows), then no HUD. Also Settings > Display > Race HUD only. |
| **Ctrl+B** | Results against the course best / this ship's best |
| **Ctrl+Shift+L** | Watch the best-lap replay |
| **Ctrl+Shift+C** | During a replay: orbit / chase camera |
| **Ctrl+N** | Checkpoint and gate markers on/off |

## HUD settings

In `%AppData%\SpaceEngineers2\AppData\RaceDisplay\RaceDisplay.json`:

| Setting | Default | What it does |
|---|---|---|
| `FitToOpening` | true | Fit gates/checkpoints to the hole in a ring or arch instead of the whole grid |
| `ShowMarkers` | true | Checkpoint/gate markers (Ctrl+N toggles) |
| `MarkerSize` | 0.03 | Marker size as a share of the distance to it |
| `UseGameUi` | true | Draw the HUD with the game's real UI. False = debug-draw panels |
| `HudScale` | 1.0 | Overall size of the panels and text |
| `FontHeightPx` | 22 | (Debug-draw fallback only) line height used for the layout |
| `BackgroundOpacity` | 210 | Panel darkness, 0 (clear) to 255 (solid black). With the game UI the text stays bright at any value |
| `MaxLapRows` | 10 | Laps shown before the list scrolls |
| `LapTarget` | 0 | Laps per race (0 = unlimited) |
| `PenaltySeconds` | 5 | Seconds added to a lap for each checkpoint missed |
| `CrashPenaltySeconds` | 5 | Seconds added per crash |
| `CrashMinSpeed` | 20 | Only crashes at or above this speed (m/s) count |
| `CrashDeceleration` | 60 | How sudden the stop must be (m/s²). Raise it if hard braking counts as a crash, lower it if crashes are missed |
| `CrashMinSpeedLoss` | 8 | Minimum speed lost in the impact (m/s), so scrapes don't count |
| `CrashMinSpeedLossFraction` | 0.25 | The impact must also take away this share of your speed (0.25 = 25%) |
| `ResultsSeconds` | 10 | How long the results panel stays up after the finish |
| `CountdownDistance` | 50 | How close to the start gate (m) you must stop for the countdown |
| `CountdownHoldSeconds` | 1 | How long to hold still near the gate before the countdown starts (0 = no countdown) |
| `RearmDistance` | 30 | Metres you must fly away from the gate before the next pass counts as a lap |

Edit the file while the game is closed.

## How it works

- **Detection**: each frame the plugin checks the path from the ship's last position to its current one against the box. A fast ship that skips right over a thin box in one frame still counts.
- **Start/stop**: it only triggers when you *enter* the box, and passes less than 2 seconds apart are ignored, so wobbling on the edge won't stop the clock.
- **Teleports and respawns** (moving more than 5 km in one frame) are ignored.
- **Pausing the game** pauses the stopwatch.
- **Saving**: the gate is saved per world (by world name), along with your best time, in `%AppData%\SpaceEngineers2\AppData\RaceDisplay\RaceDisplay.json`.

## Project layout

```
ClientPlugin/
  Plugin.cs            Entry point (game's IPlugin): sets up Harmony and the per-frame hook
  Patches.cs           Harmony patches (per-frame loop, world load and unload)
  Se2Bridge.cs         Game types: player, grids, input, debug draw
  GameUiHud.cs         HUD panels built with the game's UI system (Avalonia)
  Core/                Plain C#, no game references
    TimingGate.cs      The box: contains-point and path-crosses-box tests
    LapTimer.cs        Armed → Running → Finished state machine
    RaceGateController.cs  Keys, HUD layout, gate outline
    GateSettings.cs    JSON settings and saved gate
    IGameBridge.cs, Vec3D.cs
```

## Game hooks used (taken from the game's own DLLs)

| What | Game code used | Copied from |
|---|---|---|
| Per-frame update | Harmony postfix on `Keen.VRage.Core.VRageCore.Update(TimeSpan)` | the main app loop, which ticks the scene |
| Find the loaded world | Harmony postfix on `WorldSessionComponent.Init/InitAsync/Destroy` | — |
| Local player | `world.OwnedSession.SessionComponents.TryGet<ClientPlayersSessionComponent>().LocalPlayerController` | `ControlledEntityDebugScreen.GetPlayer` |
| Ship or character | `controller.GetTopControllable().Entity`, and if it has a `SeatComponent`, use `CubeBlockComponent.Grid.Entity` instead | `ControlledEntityDebugScreen.GetControlledEntity` |
| Position and facing | `EntityTransformFunctions.GetWorldTransform(entity.Data)` → `.Position`, `.Orientation.GetForward()/GetUp()` | same |
| HUD text | `EngineComponent.FromTag<IDebugDraw>().DebugDraw2D.AddText(...)` (pixel coordinates) | `StatsDebugScreen.Draw`, `AutoTestScenario.DrawMessage` |
| Gate outline | `IDebugDraw.DebugDraw.AddLine(Vector3D, Vector3D, ColorSRGB, depth, timeout)` | — |
| Pause | `WorldSessionComponent.Pause.IsPaused` | — |

## Building

You need the .NET 9 SDK. In this folder (the one with the `.csproj`), run:

```
dotnet build -c Release
```

The build finds Space Engineers 2 itself. It reads Steam's install folder from the registry, then checks every Steam library listed in `steamapps\libraryfolders.vdf`, other drives included, and prints `Space Engineers 2 found: ...`.

If the game isn't installed through Steam, or the search fails, point the build at the game's `Game2` folder (the one with `Game2.Client.dll`) in one of two ways:
- once per build: `dotnet build -c Release -p:Game2Bin="D:\...\SpaceEngineers2\Game2"`
- permanently: set a Windows environment variable `SE2_GAME2` to that folder.

The output goes to `bin\Release\`. You need two files from there: **`RaceDisplay.dll`** and **`0Harmony.dll`**. Players don't need any of this: they only copy those two DLLs and add the launch option below.

## Installing (Steam launch option, no Pulsar)

SE2 has a built-in plugin loader (`Keen.VRage.Core.Plugins.PluginHost`).

1. Copy `RaceDisplay.dll` and `0Harmony.dll` into one folder, for example `C:\SE2Plugins\RaceDisplay\`.
2. In Steam, right-click Space Engineers 2 → Properties → Launch Options, and add:
   ```
   -plugins:C:\SE2Plugins\RaceDisplay\RaceDisplay.dll
   ```
   - For several plugins, separate them with `;`: `-plugins:C:\a\One.dll;C:\b\Two.dll`
   - Avoid spaces in the path. If you can't, put quotes round the whole option: `"-plugins:C:\My Plugins\RaceDisplay.dll"`
3. Start the game. If the path is wrong, the game shows an "incorrect plugin path" message and closes. Check the path, and check the game log for "Plugin NOT loaded".

## If something doesn't work

- **No text appears**: in `Se2Bridge.cs`, change `DrawTimeout` to `TimeSpan.FromMilliseconds(20)`. Debug draws with a zero timeout may be cleared before they're shown.
- **Text is too small or too big**: change the `* 0.5` in `DrawHudText`.
- **Ship doesn't trigger the gate**: the tracked point is the grid's origin, which may not be the centre of the ship. Make the gate bigger with Ctrl+].
- **Shortcut clashes**: change the key codes in `Se2Bridge.PollKeys`.
- **Gate is shared across worlds**: the world-load patch didn't fire. The plugin falls back to a single "default" world and still works.

**Game options followed:** speeds (results, splits, records, crash messages) use the speed unit set in the game's Options > Interface (m/s, km/h or mph); the crash settings stay in m/s. The race panels' background uses the game's UI background opacity unless you turn off Settings > Display > Panel darkness: use the game's.
