# Race Display

A Space Engineers 2 client plugin for racing: build a course of gates and checkpoints, race laps against the clock and your own ghost, and show races off with replays and cinematics.

Everything is set up in game: press **G** and pick the **Racing Display** tab. The full guide is in the game's Help screen (**F1 > Racing Display**), and the **?** beside each section of the menu opens the right part of it.

## Installing

**With SE2 Forge:** put the `RaceDisplay` folder (holding `RaceDisplay.dll` and `0Harmony.dll`) in `%AppData%\SE2Forge\Load\Game Plugins\`, tick Race Display on the Game tab and press Launch.

**By hand:**

1. Copy **`RaceDisplay.dll`** and **`0Harmony.dll`** into one folder.
2. In Steam, right-click **Space Engineers 2 > Properties > Launch Options** and add `-plugins:` followed by the full path to `RaceDisplay.dll`. Several plugins: separate them with `;`. Avoid spaces in the path (or put quotes round the whole option).
3. Start the game. If the path is wrong the game says so and closes: check the path.

## Getting started

1. **Ctrl+F** turns on course edit mode. Aim at a ring you've built and left click: it becomes the start / finish gate.
2. Hold **Shift+R**, pick **Checkpoint**, and left click each checkpoint ring in order. **Ctrl+F** again when you're done.
3. Fly through the start gate to start the race. Save the game to keep the course with the world.

No rings built? **Shift+T** in edit mode changes the placement to **In the air**: gates and checkpoints are placed in open air ahead of you, as rings or squares, and are drawn in the world for you to fly through (arrows run through them the way to fly, and the next one has guide rails leading in).

## Keys

| Key | What it does |
|---|---|
| G | The menu (Racing Display tab) |
| Shift+J | The menu's Race page (laps, ghosts, spotlight) |
| Ctrl+R | Reset the race |
| Ctrl+F | Course edit mode |
| Shift+R (hold) | Edit mode tool wheel |
| Shift+T | Edit mode placement: on a grid / pick blocks / in the air |
| Ctrl+] / Ctrl+[ | Air gate bigger / smaller |
| Alt+] / Alt+[ | Air gate taller / shorter |
| Alt+= / Alt+- | Air gate further ahead / nearer |
| Shift+O | Air gate: ring / square |
| Ctrl+Shift+L | Watch your best lap |
| Ctrl+Shift+O | Orbit camera |
| Ctrl+Shift+P | Play / stop the cinematic |
| Ctrl+Shift+' | Autopilot |
| Esc | Leave a replay, camera or spotlight |

Every key can be changed on the menu's **Controls** tab.

## How things look

- **Air gates**: Settings > Course editing > Air gate look picks Lines, Glow or Glow (game 3D UI), which draws through the layer the game uses for its build previews.
- **Gates on grids**: Courses tab > This course > Grid checkpoint glow decides whether a course's built gates get the glowing frame as well (saved with the course).
- **Markers**: Settings > Markers > Marker icon picks a ring, diamond, chevron or beacon.
- **Gravity and lights**: a gravity panel with direction arrows sits in the bottom right corner (Settings > HUD > Gravity indicator), and a lamp on the speedometer shows while the ship's lights are on.

## Settings and presets

Settings are on the menu's **Settings** tab. Two presets come ready made:
- **Racing**: clean and easy to follow for filmed race days.
- **Practice**: everything on, to work on your line and your ship.

## Files

Settings, records, history, cinematics and race files are kept in `%AppData%\SpaceEngineers2\AppData\RaceDisplay\`. Courses are kept in each world's save. If something goes wrong, `RaceDisplay.log` there says what happened.

## Building

The source lives in the SE2 Forge workspace: `%AppData%\SE2Forge\Projects\Game Plugins\RaceDisplay\`, with `RaceDisplay.csproj` and the code in `src\`.

Build it with **SE2 Forge**: on the Game tab, tick Race Display and press **Build & Launch** (close the game first: it locks the DLLs). SE2 Forge copies `RaceDisplay.dll` and `0Harmony.dll` to `%AppData%\SE2Forge\Load\Game Plugins\RaceDisplay\`, which is the folder to share. Errors are in its output and in `build.log` next to the `.csproj`.

The build finds Space Engineers 2 through Steam. If it can't, set the `SE2_GAME2` environment variable to the game's `Game2` folder.
