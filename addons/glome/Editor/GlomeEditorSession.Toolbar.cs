#nullable enable
#if TOOLS
using System.Collections.Generic;
using Godot;

namespace Glome.Editor;

internal sealed partial class GlomeEditorSession
{
    private enum GizmoTool { Select, Move, Rotate, Scale }
    internal enum OverlayItem { AnchorPins = 0, SelectionBounds = 1, Collision = 2, LightsAndCameras = 3 }

    private GizmoTool _tool;
    private readonly Dictionary<GizmoTool, Button> _toolButtons = [];
    private Button _snap = null!;
    private PopupMenu _overlays = null!;
    private readonly VBoxContainer _viewportCorner = new() { MouseFilter = Control.MouseFilterEnum.Ignore };

    private void AddToolbar()
    {
        var margin = new MarginContainer { ThemeTypeVariation = "MainToolBarMargin" };
        var bar = new HBoxContainer { Name = "Toolbar" };
        panel.AddChild(margin);
        margin.AddChild(bar);

        var tools = new ButtonGroup();
        foreach (var (tool, key) in new[] { (GizmoTool.Select, "Q"), (GizmoTool.Move, "W"),
                                            (GizmoTool.Rotate, "E"), (GizmoTool.Scale, "R") })
        {
            var button = AddToggle(bar, $"Tool{tool}", tool.ToString(), $"{tool} ({key})");
            button.ButtonGroup = tools;
            _toolButtons.Add(tool, button);
            Listen(button, BaseButton.SignalName.Pressed, Callable.From(() =>
            {
                SetTool(tool);
                _image.GrabFocus();
            }));
        }
        SetTool(GizmoTool.Move);
        bar.AddChild(new VSeparator());
        _snap = AddToggle(bar, "Snap", "Snap", "Snap to 0.5 m or 15°. Hold Ctrl for temporary snapping, Shift for fine motion.");
        Listen(_snap, BaseButton.SignalName.Pressed, Callable.From(() => _image.GrabFocus()));
        bar.AddChild(new VSeparator());

        AddAnchorMenu(AddMenu(bar, "Anchor", "Create, duplicate, or orient an anchor"));
        AddViewMenu(AddMenu(bar, "View", "Navigate, manage bookmarks, or rebake the preview"));
        _overlays = AddMenu(bar, "Overlays", "Choose which guides to show in the viewport");
        AddOverlayItems();
        bar.AddSpacer(false);
        bar.AddChild(spaces);
        FocusAfterMenu(spaces.GetPopup());
        Listen(spaces, OptionButton.SignalName.ItemSelected, Callable.From<long>(index => SelectSpace(spaceNodes[(int)index])));
    }

    private static Button AddToggle(Container bar, string icon, string label, string tip)
    {
        var button = new Button
        {
            Name = icon, AccessibilityName = label, TooltipText = tip, ToggleMode = true,
            ThemeTypeVariation = "FlatButton", Flat = false
        };
        bar.AddChild(button);
        return button;
    }

    private void SetTool(GizmoTool tool)
    {
        _tool = tool;
        _toolButtons[tool].ButtonPressed = true;
        CancelDrag();
    }

    private PopupMenu AddMenu(Container bar, string title, string tip)
    {
        var menu = new MenuButton
        {
            Name = title, Text = title, AccessibilityName = title, TooltipText = tip,
            ThemeTypeVariation = "FlatMenuButton",
            Flat = false, SwitchOnHover = true
        };
        bar.AddChild(menu);
        var popup = menu.GetPopup();
        FocusAfterMenu(popup);
        return popup;
    }

    private void FocusAfterMenu(PopupMenu popup) =>
        Listen(popup, Popup.SignalName.PopupHide, Callable.From(FocusViewport));

    private void FocusViewport()
    {
        if (_image.IsVisibleInTree()) _image.GrabFocus();
    }

    private void AddOverlayItems()
    {
        _overlays.HideOnCheckableItemSelection = false;
        _overlays.AddCheckItem("Anchor Pins", (int)OverlayItem.AnchorPins);
        _overlays.AddCheckItem("Selection Bounds", (int)OverlayItem.SelectionBounds);
        _overlays.AddCheckItem("Collision", (int)OverlayItem.Collision);
        _overlays.AddCheckItem("Lights & Cameras", (int)OverlayItem.LightsAndCameras);
        _overlays.SetItemChecked(_overlays.GetItemIndex((int)OverlayItem.AnchorPins), true);
        Listen(_overlays, PopupMenu.SignalName.IdPressed, Callable.From<long>(id =>
        {
            _overlays.ToggleItemChecked(_overlays.GetItemIndex((int)id));
            if ((OverlayItem)id == OverlayItem.SelectionBounds) RefreshBounds();
            overlay.QueueRedraw();
        }));
    }

    private bool Shows(OverlayItem item) => _overlays.IsItemChecked(_overlays.GetItemIndex((int)item));

    private void AddViewportControls()
    {
        _image.AddChild(_viewportCorner);
        _viewportCorner.AddChild(mode);
        FocusAfterMenu(mode.GetPopup());
        Listen(mode, OptionButton.SignalName.ItemSelected, Callable.From<long>(_ => ViewChanged()));
        _viewportCorner.AddChild(_hud);
        var lines = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        lines.AddThemeConstantOverride("separation", 0);
        _hud.AddChild(lines);
        lines.AddChild(_location);
        lines.AddChild(_distortion);
        lines.AddChild(_delta);
        _hud.Hide();
        _viewportCorner.AddChild(_speedBadge);
        _image.AddChild(_speedTimer);
        Listen(_speedTimer, Timer.SignalName.Timeout, Callable.From(_speedBadge.Hide));
    }

    private void RefreshEditorTheme()
    {
        var theme = EditorInterface.Singleton.GetEditorTheme();
        void SetIcon(Button button)
        {
            bool available = theme.HasIcon(button.Name, "EditorIcons");
            button.Icon = available ? theme.GetIcon(button.Name, "EditorIcons") : null;
            button.Text = available ? "" : button.AccessibilityName;
        }
        foreach (var button in _toolButtons.Values) SetIcon(button);
        SetIcon(_snap);

        // Use the same backdrop and foreground colors as the native 3D viewport menu.
        var backdrop = theme.GetStylebox("Information3dViewport", "EditorStyles");
        mode.BeginBulkThemeOverride();
        foreach (var state in new[] { "normal", "hover", "pressed", "hover_pressed", "focus", "disabled" })
        {
            mode.AddThemeStyleboxOverride(state, backdrop);
            mode.AddThemeStyleboxOverride($"{state}_mirrored", backdrop);
        }
        foreach (var state in new[] { "", "_hover", "_focus", "_pressed", "_hover_pressed" })
            mode.AddThemeColorOverride($"font{state}_color", theme.GetColor($"font_dark_background{state}_color", "Editor"));
        mode.AddThemeConstantOverride("modulate_arrow", 1);
        mode.EndBulkThemeOverride();
        _hud.AddThemeStyleboxOverride("panel", backdrop);
        _speedBadge.AddThemeStyleboxOverride("normal", backdrop);
        var foreground = theme.GetColor("font_dark_background_color", "Editor");
        foreach (var label in new[] { _location, _distortion, _delta, _speedBadge })
            label.AddThemeColorOverride("font_color", foreground);
        _viewportCorner.Position = Vector2.One * Mathf.Round(8 * EditorInterface.Singleton.GetEditorScale());
    }
}
#endif
