namespace RaceDisplay.Core
{
    public enum GateKey
    {
        RemoveGate,  // no longer used (course edit mode)
        Grow,        // default Ctrl+]
        Shrink,      // default Ctrl+[
        ResetTimer,  // default Ctrl+R
        GateOnGrid,  // no longer used (course edit mode); kept so saved settings still load
        ClearBest,   // no longer used (Records tab > Reset)
        AddCheckpointOnGrid,  // no longer used (course edit mode)
        RemoveLastCheckpoint, // no longer used (course edit mode)
        ClearCheckpoints,     // no longer used (course edit mode)
        ToggleMarkers,        // default Ctrl+N: checkpoint/gate markers on or off
        LapCount,    // default J: opens the race setup (Racing Display menu, Race tab)
        OpenControls,         // no longer used: controls are changed from the pause menu (Esc > Racing Display)
        Replay,               // default Ctrl+Shift+L: watch the best-lap replay (press again to stop)
        ResultsMode,          // default Ctrl+B: race results against the course best or this ship's best
        EditMode,             // default Ctrl+F: course edit mode on/off
        EditWheel,            // default R (hold): tool wheel in course edit mode
        ReplayCamera,         // default Ctrl+Shift+C: replay camera orbit (move around, keeps aiming at the ghost) / chase
        Undo,                 // default Ctrl+U: undo the last course edit (edit mode)
        Redo,                 // default Ctrl+Shift+U: redo it
        ReverseCourse,        // default Ctrl+Shift+I: fly the course the other way round (edit mode)
        RaceHudOnly,          // no longer used (race HUD only is a step of the game's HUD key, U)
    }

    public enum TextAlign { Left, Center, Right }

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
        OpenGhostFolder,
        RefreshGhosts,
        SetPilotName,      // Text = name
        AddSector,         // Text = "from|to|name" (route point indexes)
        DeleteSector,      // Row = sector index
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
        /// Velocity (m/s) of the ship being flown. False when not seated in a ship.
        /// Call after TryGetControlledPose in the same frame.
        /// </summary>
        bool TryGetShipVelocity(out Vec3D velocity);

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

        /// <summary>
        /// Takes a copy of the ship you're sitting in (the same copy the game's Ctrl+C makes) for a hologram ghost.
        /// Happens in the background; does nothing if you're not in a ship.
        /// </summary>
        void CopyGhostGrid();

        /// <summary>
        /// Shows the copied ship as a real hologram (the game's paste preview) with its pivot at position and its
        /// local X / Y / Z axes along x / y / z. False if there's no copy yet or the game wouldn't make one
        /// (then draw the outline ghost instead).
        /// </summary>
        bool PlaceGhostGrid(Vec3D position, Vec3D x, Vec3D y, Vec3D z);

        /// <summary>Hides the hologram ghost (call when no ghost is shown this frame). Remove = delete it too.</summary>
        void HideGhostGrid(bool remove);

        /// <summary>Opacity of the solid ship ghost, 0.05 to 1.</summary>
        float GhostGridOpacity { get; set; }

        /// <summary>Name of the ship copied for the hologram ghost, or null.</summary>
        string GhostGridName { get; }

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
