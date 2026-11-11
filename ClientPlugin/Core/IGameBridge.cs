using System.Collections.Generic;
namespace RaceDisplay.Core
{
    public enum GateKey
    {
        Grow,        // default Ctrl+]
        Shrink,      // default Ctrl+[
        ResetTimer,  // default Ctrl+R
        ToggleMarkers,        // default Ctrl+N: checkpoint/gate markers on or off
        LapCount,    // default Shift+J: opens the race setup (Racing Display menu, Race tab)
        Replay,               // default Ctrl+Shift+L: watch the best-lap replay (press again to stop)
        ResultsMode,          // default Ctrl+B: race results against the course best or this ship's best
        EditMode,             // default Ctrl+F: course edit mode on/off
        EditWheel,            // default Shift+R (hold): tool wheel in course edit mode
        ReplayCamera,         // default Ctrl+Shift+C: replay camera orbit (move around, keeps aiming at the ghost) / chase
        Undo,                 // default Ctrl+U: undo the last course edit (edit mode)
        Redo,                 // default Ctrl+Shift+U: redo it
        ReverseCourse,        // default Ctrl+Shift+I: fly the course the other way round (edit mode)
        PlacementMode,        // default Shift+T: edit mode places on a grid / by picking blocks / in the air
        EditLine,             // default Shift+L: show or hide the best lap line while editing
        MoveCheckpoint,       // default Shift+M: aim at a checkpoint, then left click its new place (keeps its number)
        ResultsPrevLap,       // default Shift+[: the results graphs and map show the lap before
        ResultsNextLap,       // default Shift+]: ... the next lap
        SetStartSpot,         // default Ctrl+T: the reset race teleport goes to where your seat is now
    }

    public enum TextAlign { Left, Center, Right }

    /// <summary>A block picked by aiming at it (course edit mode, Pick blocks): its grid and its cells.</summary>
    public sealed class BlockPick
    {
        public object Grid;          // the game's grid (only compared, never used outside the bridge)
        public string GridName;
        public int[] Min, Max;       // the block's cells on its grid (Min inclusive, Max exclusive)
        public double Distance;      // from the eye along the look ray (m)
    }

    /// <summary>A grid with the names it goes by (grid name, its antennas' names) and its box.</summary>
    public sealed class NamedGrid
    {
        public readonly System.Collections.Generic.List<string> Names = new System.Collections.Generic.List<string>();
        public GridBox Box;
    }

    /// <summary>What the player clicked in the Racing Display controls menu.</summary>
    public enum MenuAction
    {
        Opened,
        Change,        // start changing the shortcut in Row
        Default,       // put the shortcut in Row back to its default
        ResetAll,      // all shortcuts back to default
        SetValue,      // setting Row (index into SettingDefs.All) = Value
        ResetSettings, // all Settings-tab values back to default
        SaveCourse,    // Text = name
        LoadCourse,    // Text = name
        DeleteCourse,  // Text = name
        CheckpointUp,  // Row = checkpoint index
        CheckpointDown,
        DeleteCheckpoint,
        AutoBuild,     // gate and checkpoints from grid / antenna names
        AntennasOff,
        AntennasOn,
        WatchReplay,   // best-lap replay (starts when the menu closes)
        ClearCourseRecords, // best lap / best races for the course you're on
        ClearAllRecords,    // best times for every course
        DeleteShip,
        ToggleEditMode,
        ReverseCourse,
        SecondGhostShip,   // Text = ship name: race / watch its best lap as the 2nd ghost (again = off)
        SecondGhostFile,   // Text = ghost file path
        SaveToWorld,
        LoadFromWorld,
        WatchShipReplay,   // Text = ship name
        ExportGhost,       // Text = ship name, or null for the course best
        WatchGhostFile,    // Text = file path
        RaceGhostFile,     // Text = file path
        DeleteGhostFile,   // Text = file path
        RaceCourseBest,
        RaceShipLast,      // Text = ship name: race against the last lap flown in that ship
        WatchShipLast,     // Text = ship name: replay the last lap flown in that ship
        ShipAnalyser,      // enter ship analyser mode (Esc leaves it)
        SetRpiValue,       // RPI designer: text = block name, value = its RPI (in the class in use)
        SetRpiBudget, SetRpiMinWeight,
        RpiNewClass, RpiSelectClass, RpiRenameClass, RpiDeleteClass,   // text = class name
        RpiExport, RpiImport, RpiOpenFolder,                           // import: text = file path
        SpotlightExport, SpotlightStart, SpotlightStop, SpotlightOpenFolder,   // race spotlight (start: text = file path)
        ResultsLapPrev, ResultsLapNext,   // Race Map: the results' lap shown (and replayed)
        RpiDuplicateClass,   // text = the copy's name (empty = "<name> copy")
        RpiImportText,       // text = a class as JSON (pasted)
        SetRpiLimit,         // text = block name, value = most per ship (0 = banned, -1 = no limit)
        RpiAddStep,          // value = weight in kg, text = budget at that weight
        RpiRemoveStep,       // value = weight in kg
        RacePossible,      // the ghost, racing line and live gap follow the possible lap (best sectors joined up)
        OpenGhostFolder,
        RefreshGhosts,
        SavePreset,        // Text = name: all Settings-tab values under that name
        LoadPreset,        // Text = name
        DeletePreset,      // Text = name
        ExportPreset,      // Text = name: write it to a file in the presets folder (to share)
        ImportPreset,      // Text = file path: add the preset in that file
        OpenPresetFolder,
        ClearCourse,       // remove this world's gates, checkpoints and sectors (saved courses stay)
        RenameCourse,      // Text = "old name\nnew name"
        SetPilotName,      // Text = name
        AddSector,         // Text = "from|to|name" (route point indexes)
        DeleteSector,      // Row = sector index
        SetStartSpot,      // the reset race teleport goes to where you are now
        ClearStartSpot,    // back to just behind the start gate
        RaceShip,          // Text = ship name: the main ghost, racing line and live gap use its best lap
        Close,
    }

    public struct MenuCommand
    {
        public MenuAction Action;
        public int Row; // KeyBindings.Actions index (Change / Default) or SettingDefs.All index (SetValue)
        public double Value;
        public string Text;
        public MenuCommand(MenuAction action, int row = -1, double value = 0, string text = null) { Action = action; Row = row; Value = value; Text = text; }
    }

    /// <summary>A grid's oriented bounding box in world space.</summary>
    /// <summary>Ship analyser: a grid's stats (worked out from its blocks).</summary>
    public sealed class ShipStats
    {
        public string Name;
        public int Blocks, Pcu, Thrusters, Gyros, Inventories;
        public double Mass;                  // kg
        public double SizeX, SizeY, SizeZ;   // m (width, height, length)
        /// <summary>Max thrust (N) pushing the grid each way: forward, backward, left, right, up, down (the grid's own axes).</summary>
        public readonly double[] Thrust = new double[6];
        public double GyroTorque;            // N m, all gyroscopes together
        public double StorageMax, StorageUsed;   // kg the inventories can hold / hold now
        /// <summary>Most each kind of resource the grid's producers can give (by resource name), e.g. power.</summary>
        public readonly Dictionary<string, double> Output = new Dictionary<string, double>();
        public readonly Dictionary<string, string> OutputUnits = new Dictionary<string, string>();
        /// <summary>Batteries and tanks, by resource: held now, most they can hold, how many, and the unit.</summary>
        public readonly Dictionary<string, double> StoredNow = new Dictionary<string, double>(), StoredMax = new Dictionary<string, double>();
        public readonly Dictionary<string, int> Containers = new Dictionary<string, int>();
        public readonly Dictionary<string, string> StoreUnits = new Dictionary<string, string>();
        /// <summary>How many of each block type (by the game's internal block name), and each type's kind, size in metres and name as shown in game.</summary>
        public readonly Dictionary<string, int> BlockCounts = new Dictionary<string, int>();
        public readonly Dictionary<string, (string Kind, double Size, string Name)> BlockInfo = new Dictionary<string, (string, double, string)>();
        public bool FromServer;              // parts read from the server's copy of the grid
        /// <summary>Where it is: centre of mass, and the grid's forward, up and right in the world.</summary>
        public Vec3D CenterOfMass, Forward, Up, Right, BoxCenter;
        public double ComOffset;             // m from the middle of its box
        public object Grid;                  // the game's grid (to analyse again / draw on)
    }

    public struct GridBox
    {
        public string Name;
        public Vec3D Center;
        public Vec3D Forward; // grid's local Z axis
        public Vec3D Up;      // grid's local Y axis
        public double Width;  // size along local X
        public double Height; // size along local Y
        public double Depth;  // size along local Z
        public double Distance; // from the player to the nearest point of the box (approx.)
        /// <summary>True if the box is the grid's opening (the hole in a ring), not the whole grid.</summary>
        public bool IsOpening;
        /// <summary>For an opening: the axis you fly through (0 = width/X, 1 = height/Y, 2 = depth/Z).</summary>
        public int ThroughAxis;
    }

    /// <summary>
    /// Everything the plugin needs from Space Engineers 2. Only Se2Bridge.cs touches game code,
    /// so a game update that renames something only needs fixing there.
    /// All screen coordinates are in pixels from the top-left corner.
    /// </summary>
    public interface IGameBridge
    {
        /// <summary>True while a world is loaded and the player is in it (not in the main menu).</summary>
        bool InWorld { get; }

        /// <summary>Opens the Racing Display menu on its Race tab (via the pause menu). False if it couldn't.</summary>
        bool OpenRaceMenu();

        /// <summary>Goes up each time the game saves the world (the course is written into the save then).</summary>
        int WorldSaveCount { get; }

        /// <summary>The game's HUD key turned race HUD only on (true) or off (false) since the last call; null = no change.
        /// raceHudOnly: whether it's on now (so the HUD key knows where it is in its cycle).</summary>
        bool? TakeHudCycle(bool raceHudOnly);

        /// <summary>True while the terminal's Race Map page (G > Map > Race Map) is on screen.</summary>
        bool RaceMapShown { get; }
        /// <summary>The map data for the Race Map page.</summary>
        void ShowRaceMap(HudModel.MinimapPanel map);

        /// <summary>The loaded world's session object (same object = same world, even if Save As renames it).</summary>
        object WorldHandle { get; }

        /// <summary>Name/ID of the loaded world, so each save keeps its own gate.</summary>
        string WorldId { get; }

        /// <summary>Game time in seconds (stops when paused).</summary>
        double TimeSeconds { get; }

        /// <summary>
        /// Position and facing of whatever the player controls: the ship (grid) when seated in a
        /// cockpit, otherwise the character. Returns false if there's nothing to track.
        /// </summary>
        bool TryGetControlledPose(out Vec3D position, out Vec3D forward, out Vec3D up);

        /// <summary>
        /// The way you're facing: the seat's (cockpit's) forward in a ship, else the character's. Unlike the pose's
        /// forward (the grid's own axis, which depends on how the ship was built), this is the way the ship points.
        /// </summary>
        bool TryGetFacing(out Vec3D forward);

        /// <summary>The Workshop item the current world was just started from (its ids as text), or null.</summary>
        string[] WorkshopStartIds { get; }
        void ClearWorkshopStart();

        /// <summary>
        /// Velocity (m/s) of the ship being flown. False when not seated in a ship.
        /// Call after TryGetControlledPose in the same frame.
        /// </summary>
        bool TryGetShipVelocity(out Vec3D velocity);

        /// <summary>Moves the ship you're flying so your seat is at the position, facing that way, and stops it. False if not in a ship.</summary>
        bool TeleportShip(Vec3D seatPosition, Vec3D forward, Vec3D up);

        /// <summary>Asks the game to stop the ship you're flying.</summary>
        void StopShip();
        /// <summary>Gives the ship this velocity (m/s, world). False if it can't be done here (not the host).</summary>
        bool SetShipVelocity(Vec3D velocity);
        /// <summary>Turns target based gyro off on the ship's cockpits that have it on, and remembers them; then back on.</summary>
        int TargetGyroOff();
        void TargetGyroRestore();
        /// <summary>Which way gravity pulls the ship (unit vector). False in free space.</summary>
        bool TryGetGravity(out Vec3D down);

        /// <summary>Ship analyser: the grid the ray hits first (null = none); with includeOwn, the ship you're in can be picked too.</summary>
        object LookedAtGrid(Vec3D origin, Vec3D direction, double maxDistance, bool includeOwn);

        /// <summary>The ship you're flying (null on foot).</summary>
        object OwnGrid { get; }

        /// <summary>Ship analyser: a grid's stats, or null if it's gone.</summary>
        ShipStats AnalyseGrid(object grid);

        /// <summary>Your seat's position and facing (forward, up) while flying a ship. False on foot.</summary>
        bool TryGetSeatPose(out Vec3D position, out Vec3D forward, out Vec3D up);

        /// <summary>
        /// Middle of the ship you're flying (centre of its blocks' bounding box) in the world. The grid's own
        /// position is its pivot, which can be far off to one side of the ship. False on foot.
        /// </summary>
        bool TryGetShipCentre(out Vec3D centre);

        /// <summary>Opens the edit tool wheel with the cursor (via the pause menu). wheelKey: let go of it to pick.</summary>
        /// <param name="highlights">Slices to tint (index -> colour), e.g. a switch that's on.</param>
        void OpenEditWheel(string[] labels, int current, int wheelKey, System.Collections.Generic.IDictionary<int, (byte R, byte G, byte B)> highlights = null);
        /// <summary>The tool picked on the wheel since the last call, if any.</summary>
        bool TryTakeWheelPick(out int tool);
        /// <summary>The wheel key was let go: pick the tool under the cursor and close the wheel.</summary>
        void ReleaseEditWheel();
        bool WheelFailed { get; set; }
        bool EditWheelOpen { get; }

        /// <summary>Switches the game's debug draw on (true) or back to how it was before (false).</summary>
        void SetDebugDraw(bool on);

        bool TryGetLookRay(out Vec3D origin, out Vec3D direction, out Vec3D right, out Vec3D up);

        /// <summary>The nearest other grid the look ray hits, within maxDistance (fitted to its opening if asked).</summary>
        bool TryGetLookedAtGrid(Vec3D origin, Vec3D direction, double maxDistance, bool fitToOpening, out GridBox box);

        /// <summary>The nearest block (on another grid) the look ray hits, within maxDistance.</summary>
        bool TryGetLookedAtBlock(Vec3D origin, Vec3D direction, double maxDistance, out BlockPick block);

        /// <summary>
        /// The box from one block to another on the same grid (both included), lined up with the grid, like the
        /// game's partial copy. IsOpening is set and ThroughAxis is its thinnest side, the way you fly through it.
        /// </summary>
        bool TryGetBlockSpan(BlockPick a, BlockPick b, out GridBox box);

        /// <summary>True while the game window has keyboard focus.</summary>
        bool HasFocus { get; }

        /// <summary>True while the key (Windows virtual-key code) is held down.</summary>
        bool IsKeyDown(int virtualKey);

        /// <summary>Screen size in pixels (0 if unknown).</summary>
        int ScreenWidth { get; }
        int ScreenHeight { get; }

        /// <summary>
        /// Shows the HUD with the game's real UI (Avalonia). Pass null to hide it.
        /// Returns false if the game UI isn't available; the caller then falls back to debug draw.
        /// </summary>
        bool ShowHud(HudModel model);

        /// <summary>Text this frame. (x, y) is the top of the text; x is its left, centre or right edge depending on align.</summary>
        void DrawText(string text, double x, double y, double scale, HudColor color, TextAlign align);

        /// <summary>Filled rectangle this frame.</summary>
        void DrawRect(double x, double y, double width, double height, HudColor fill);

        /// <summary>Rectangle outline this frame.</summary>
        void DrawRectOutline(double x, double y, double width, double height, HudColor color);

        /// <summary>World-space line this frame (gate outlines). Only visible with the game's debug draw on.</summary>
        void DrawWorldLine(Vec3D from, Vec3D to, HudColor color);

        /// <summary>World-space marker line this frame. Always visible (no debug draw needed, HUD hidden or not).</summary>
        void DrawMarkerLine(Vec3D from, Vec3D to, HudColor color);

        /// <summary>
        /// Filled triangles (corners in threes, one colour per triangle; alpha makes them see-through), always
        /// visible like the markers. depth = hidden behind terrain and grids.
        /// </summary>
        /// <summary>Lines (points in pairs) in one colour, always drawn like the markers. depth = hidden behind terrain and grids.</summary>
        void DrawMarkerLines(System.Collections.Generic.IReadOnlyList<Vec3D> points, HudColor colour, bool depth);

        void DrawMarkerTriangles(System.Collections.Generic.IReadOnlyList<Vec3D> corners, System.Collections.Generic.IReadOnlyList<HudColor> colours, bool depth);

        /// <summary>World-space marker label this frame, centred on <paramref name="position"/>. Always visible.</summary>
        void DrawMarkerText(Vec3D position, string text, HudColor color, double scale);

        /// <summary>True while the Racing Display controls menu (opened from the pause menu) is showing.</summary>
        bool ControlsMenuOpen { get; }

        /// <summary>Next click from the controls menu, if any.</summary>
        bool TryTakeMenuCommand(out MenuCommand command);

        /// <summary>Updates what the controls menu shows. Ignored while it's closed.</summary>
        void ShowControlsMenu(HudModel.ControlsPanel panel);

        /// <summary>Closes the controls menu (back to the pause menu).</summary>
        void CloseControlsMenu();

        /// <summary>
        /// Projects a world point onto the screen with the game camera: x, y from 0 to 1 (top-left is 0, 0).
        /// For a point behind the camera, x and y still give the direction to it from the screen centre.
        /// False if the camera isn't available.
        /// </summary>
        bool TryProjectToScreen(Vec3D world, out double x, out double y, out bool behind);

        /// <summary>Name of the ship (grid) you're flying, or null when not in one.</summary>
        string ShipName { get; }

        /// <summary>
        /// Every grid (except the one being flown) that has a name <paramref name="wanted"/> accepts, looking at
        /// the grid's name and the names of its antennas. Boxes are fitted to ring openings if asked.
        /// </summary>
        System.Collections.Generic.List<NamedGrid> FindNamedGrids(System.Func<string, bool> wanted, bool fitToOpening);

        /// <summary>True while the game is paused (pause menu open in single player).</summary>
        bool IsPaused { get; }

        /// <summary>Switches the player to the game's spectator camera at this view, remembering what they were controlling. False if it can't.</summary>
        bool StartSpectator(Vec3D position, Vec3D forward, Vec3D up);

        /// <summary>Moves the spectator camera. False until the spectator exists (it takes a moment after StartSpectator).</summary>
        bool SetSpectatorView(Vec3D position, Vec3D forward, Vec3D up);

        /// <summary>Moves the spectator camera without stopping its own movement (so the player can still fly it).</summary>
        bool SetSpectatorView(Vec3D position, Vec3D forward, Vec3D up, bool stopMotion);

        /// <summary>
        /// Turns the game's cinematic spectator mode on or off for the replay camera (smooth, accelerating movement
        /// and its own camera-rotate keys). Off puts back whatever the player had before. False if the game has no such mode.
        /// </summary>
        bool SetSpectatorCinematic(bool on);


        /// <summary>Where the spectator camera is and which way it faces now.</summary>
        bool TryGetSpectatorView(out Vec3D position, out Vec3D forward, out Vec3D up);

        /// <summary>True while the player is in the spectator camera (it doesn't race through gates).</summary>
        bool InSpectator { get; }

        /// <summary>Leaves the spectator camera and takes control of what the player was controlling before (their cockpit).</summary>
        void EndSpectator();

        /// <summary>The flown ship's axes in the world (its grid's local X, Y, Z). False on foot.</summary>
        bool TryGetShipAxes(out Vec3D x, out Vec3D y, out Vec3D z);

        /// <summary>The flown ship's blocks as boxes in grid coordinates [min x, y, z, max x, y, z] (metres), biggest first, at most maxBoxes.</summary>
        System.Collections.Generic.List<double[]> GetShipShape(int maxBoxes);

        /// <summary>Turns every antenna in the world on or off. Returns how many were changed; total = how many antennas there are.</summary>
        int SetAntennasEnabled(bool enabled, out int total);

        /// <summary>True while the game's HUD is shown (the player hasn't hidden it).</summary>
        bool HudVisible { get; }

        /// <summary>Plays a game sound event (e.g. "event:/UI/HudClick"). Does nothing if it can't.</summary>
        void PlaySound(string eventPath);

        /// <summary>Folder for the plugin's settings file.</summary>
        string ConfigFolder { get; }

        /// <summary>The loaded world's save folder (where the game keeps its files), or null if it can't be found.</summary>
        string WorldSaveFolder { get; }
    }

    public readonly struct HudColor
    {
        public readonly byte R, G, B, A;
        public HudColor(byte r, byte g, byte b, byte a = 255) { R = r; G = g; B = b; A = a; }

        public HudColor WithAlpha(byte a) => new HudColor(R, G, B, a);

        public static readonly HudColor White = new HudColor(255, 255, 255);
        public static readonly HudColor Green = new HudColor(80, 230, 120);
        public static readonly HudColor Yellow = new HudColor(255, 210, 60);
        public static readonly HudColor Cyan = new HudColor(70, 200, 255, 200);
        public static readonly HudColor Grey = new HudColor(180, 180, 180, 220);
        public static readonly HudColor Red = new HudColor(255, 110, 100);
        public static readonly HudColor Orange = new HudColor(255, 160, 40, 220);
        /// <summary>F1 'purple': best ever (all-time course record).</summary>
        public static readonly HudColor Purple = new HudColor(200, 120, 255);

        // Panel style from the game's SPD box: near-black see-through fill, thin light border.
        public static readonly HudColor PanelFill = new HudColor(10, 12, 14, 170);
        public static readonly HudColor PanelBorder = new HudColor(225, 230, 235, 200);
    }
}
