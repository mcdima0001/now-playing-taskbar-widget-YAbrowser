using System.Windows.Automation;

namespace SpotifyTaskbarWidget;

/// <summary>
/// Locates, through UI Automation, the taskbar elements that anchor the
/// widget: the widgets/weather button (left limit) and the Start button
/// (right limit). Values come back in physical screen pixels.
/// </summary>
internal static class TaskbarAnchors
{
    /// <summary>Ok=false means the READ failed (UIA threw) - the previous
    /// anchors stay valid. Ok=true with a null value means the element really
    /// does not exist (e.g. widgets turned off). Without this distinction a
    /// transient failure was treated as "the button disappeared" and the
    /// widget landed on top of the weather button.</summary>
    public static (bool Ok, double? widgetsRight, double? startLeft, double? taskButtonsRight) Get(IntPtr tray)
    {
        double? widgetsRight = null, startLeft = null, taskButtonsRight = null;
        try
        {
            var root = AutomationElement.FromHandle(tray);

            var widgets = root.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.AutomationIdProperty, "WidgetsButton"));
            if (widgets != null)
            {
                var r = widgets.Current.BoundingRectangle;
                if (!r.IsEmpty) widgetsRight = r.Right;
            }

            var start = root.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.AutomationIdProperty, "StartButton"));
            if (start != null)
            {
                var r = start.Current.BoundingRectangle;
                if (!r.IsEmpty) startLeft = r.Left;
            }

            // End of the app icon row (so they are not covered when the widget
            // anchors right, on left-aligned / secondary taskbars)
            var buttons = root.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));
            foreach (AutomationElement button in buttons)
            {
                // A button can die MID-enumeration (apps opening/closing);
                // without the per-button try, throwing here discarded the whole
                // read - including the anchors already read successfully above
                try
                {
                    string cls = button.Current.ClassName ?? "";
                    if (!cls.StartsWith("Taskbar.TaskListButton", StringComparison.Ordinal)) continue;
                    var r = button.Current.BoundingRectangle;
                    if (r.IsEmpty) continue;
                    if (taskButtonsRight is not double cur || r.Right > cur)
                        taskButtonsRight = r.Right;
                }
                catch { }
            }
        }
        catch
        {
            return (false, null, null, null);
        }
        return (true, widgetsRight, startLeft, taskButtonsRight);
    }
}
