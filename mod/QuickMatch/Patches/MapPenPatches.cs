using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using QuickMatch.MapPen;

namespace QuickMatch.Patches;

/// <summary>
/// Map drawing on a touchscreen (<see cref="MapPenInput"/>). Without a touchscreen the map screen
/// gets no <see cref="MapPenInput"/> and every patch here lets the game run as before. Only mouse
/// events emulated from touch are held back; a real mouse is never touched.
/// </summary>
public static class MapPenPatches
{
    [HarmonyPatch(typeof(NMapScreen), nameof(NMapScreen._Ready))]
    public static class Attach
    {
        public static void Postfix(NMapScreen __instance)
        {
            MapPenInput.Attach(__instance);
        }
    }

    /// <summary>
    /// With the pen tool on, the game draws from the left mouse button, which on a phone is the first
    /// finger as soon as it lands. The gesture draws instead, so a two-finger scroll or a tap on the
    /// pen button to leave leaves no mark.
    /// </summary>
    [HarmonyPatch(typeof(NMouseModeMapDrawingInput), nameof(NMouseModeMapDrawingInput._Input))]
    public static class PenToolInput
    {
        public static bool Prefix(InputEvent inputEvent)
        {
            return MapPenInput.Current == null || !IsFromTouch(inputEvent);
        }
    }

    /// <summary>
    /// The map's own input: a press that reaches it landed on the map or a map point (points pass
    /// presses on to the map), where drawing may start, never on a button or another screen.
    /// While a quick stroke is drawn, the finger's movement must not also scroll the map.
    /// </summary>
    [HarmonyPatch(typeof(NMapScreen), nameof(NMapScreen._GuiInput))]
    public static class MapInput
    {
        public static bool Prefix(InputEvent inputEvent)
        {
            if (MapPenInput.Current is not { } pen || !IsFromTouch(inputEvent))
            {
                return true;
            }
            if (inputEvent is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true })
            {
                pen.OnMapPressed();
            }
            return !(inputEvent is InputEventMouseMotion && pen.Gesture.QuickStrokeActive);
        }
    }

    /// <summary>A quick stroke drawn from a map point ends on that point; lifting the finger must not also vote for it.</summary>
    [HarmonyPatch(typeof(NMapScreen), nameof(NMapScreen.OnMapPointSelectedLocally))]
    public static class PointChosen
    {
        public static bool Prefix()
        {
            return MapPenInput.Current is not { } pen || !pen.Gesture.TouchDrewQuickStroke;
        }
    }

    private static bool IsFromTouch(InputEvent inputEvent) =>
        inputEvent is InputEventMouse && inputEvent.Device == InputEvent.DeviceIdEmulation;
}
