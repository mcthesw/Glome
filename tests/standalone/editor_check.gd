@tool
extends Node

# This scene is opened only by the acceptance runner. Keep the driver alive while
# the editor opens the real project scene; nothing is installed as an editor plugin.
var failed := false
var preview: TextureRect
var mode: OptionButton
var main_screen: Control
var bridge: RefCounted

func _ready() -> void:
	if not Engine.is_editor_hint() or not OS.get_cmdline_user_args().has("--check-editor"):
		return
	if has_meta("driver"):
		run.call_deferred()
	else:
		var driver: Node = get_script().new()
		driver.set_meta("driver", true)
		EditorInterface.get_base_control().add_child.call_deferred(driver)

func check(ok: bool, detail: String) -> bool:
	if not ok and not failed:
		failed = true
		push_error("Glome acceptance: " + detail)
		get_tree().quit(1)
	return ok

func frames(count: int = 6) -> void:
	for i in count:
		EditorInterface.get_base_control().queue_redraw()
		await RenderingServer.frame_post_draw

func settle(min_ms: int = 300) -> void:
	var start := Time.get_ticks_msec()
	while Time.get_ticks_msec() - start < min_ms or (preview != null and preview.texture == null and Time.get_ticks_msec() - start < 5000):
		EditorInterface.get_base_control().queue_redraw()
		await RenderingServer.frame_post_draw
	await frames(6)

func choose_view(title: String) -> Image:
	for i in mode.item_count:
		if mode.get_item_text(i) == title:
			mode.select(i)
			mode.item_selected.emit(i)
			await settle()
			if not check(preview.texture != null, "No output for " + title):
				return null
			print("Editor view ", title, " output=", preview.texture.get_size(), " panel=", preview.size)
			return preview_frame()
	check(false, "Missing editor view " + title)
	return null

func preview_frame() -> Image:
	# Sample the rendered viewport below the mode pill and selection readout.
	var image := get_viewport().get_texture().get_image()
	var to_pixels := preview.get_viewport().get_final_transform() * preview.get_global_transform_with_canvas()
	var region := Rect2(Vector2(0, preview.size.y * 0.25), preview.size * Vector2(1, 0.75))
	return image.get_region(Rect2i((to_pixels * region).grow(-8)))

func differs(a: Image, b: Image) -> bool:
	if a == null or b == null:
		return false
	a.resize(64, 36, Image.INTERPOLATE_BILINEAR)
	b.resize(64, 36, Image.INTERPOLATE_BILINEAR)
	var changed := 0
	for y in 36:
		for x in 64:
			if absf(a.get_pixel(x,y).get_luminance() - b.get_pixel(x,y).get_luminance()) > 0.03:
				changed += 1
	return changed > 100

func run() -> void:
	await frames()
	var scene := OS.get_environment("GLOME_EDITOR_SCENE")
	EditorInterface.open_scene_from_path(scene)
	await frames(12)
	EditorInterface.set_main_screen_editor("S³")
	main_screen = EditorInterface.get_editor_main_screen().find_child("GlomeMainScreen", true, false)
	if not check(main_screen != null and main_screen.is_visible_in_tree(), "S³ main screen did not open"):
		return
	get_window().mode = Window.MODE_WINDOWED
	get_window().size = Vector2i(1920, 1080)
	get_window().content_scale_factor = minf(1, 1.35 / EditorInterface.get_editor_scale())
	await settle()
	mode = main_screen.find_child("ViewMode", true, false)
	preview = main_screen.find_child("Preview", true, false)
	if not check(mode != null and preview != null and preview.is_ancestor_of(mode), "ViewMode in viewport corner"): return
	var target := EditorInterface.get_edited_scene_root()
	await check_toolbar(target)
	if failed: return
	var lit := await choose_view("StandardLit")
	if failed: return
	var revision: int = target.BakeRevision
	var texture := preview.texture
	var clay := await choose_view("ClayClean")
	if not check(differs(lit, clay), "standard and clay frames are indistinguishable"): return
	var unlit := await choose_view("Unlit")
	var relit := await choose_view("StandardLit")
	if not check(differs(unlit, relit), "unlit and standard frames are indistinguishable"): return
	if not check(target.BakeRevision == revision and preview.texture == texture, "neutral modes rebuilt the preview"): return
	bridge = load("res://EditorChecks.cs").new()
	await check_authored(target)
	if failed: return
	await live_inspector(target.get_node("Target/Block"))
	tool_key(KEY_F)
	await settle()
	var plugin := EditorPlugin.new()
	plugin.hide_bottom_panel()
	plugin.free()
	await frames(12)
	var shot := get_viewport().get_texture().get_image()
	if not check(shot.get_size() == Vector2i(1920,1080), "editor capture size: " + str(shot.get_size())): return
	if not check(shot.save_png(OS.get_environment("GLOME_EDITOR_SHOT")) == OK, "saving editor capture"): return
	print("[GLOME-EDITOR-PASS] ", scene)
	get_tree().quit()

func mouse_button(point: Vector2, pressed: bool) -> void:
	var event := InputEventMouseButton.new()
	event.position = point
	event.button_index = MOUSE_BUTTON_LEFT
	event.pressed = pressed
	preview.gui_input.emit(event)

func tool_key(code: Key, ctrl := false) -> void:
	preview.grab_focus()
	for pressed in [true, false]:
		var event := InputEventKey.new()
		event.keycode = code
		event.physical_keycode = code
		event.ctrl_pressed = ctrl
		event.pressed = pressed
		get_window().push_input(event, true)

func item_index(menu: PopupMenu, title: String) -> int:
	for i in menu.item_count:
		if menu.get_item_text(i) == title: return i
	check(false, "Missing menu item " + title)
	return -1

func press_item(menu: PopupMenu, title: String) -> void:
	var i := item_index(menu, title)
	if i >= 0 and check(not menu.is_item_disabled(i), "Disabled menu item " + title):
		menu.id_pressed.emit(menu.get_item_id(i))

func check_toolbar(space: Node) -> void:
	var bar: HBoxContainer = main_screen.find_child("Toolbar", true, false)
	if not check(bar != null, "native toolbar"): return
	var buttons := bar.get_children().filter(func(n): return n is Button and n.visible)
	var names := ["ToolSelect", "ToolMove", "ToolRotate", "ToolScale", "Snap", "Anchor", "View", "Overlays"]
	if not check(buttons.map(func(n): return str(n.name)) == names, "single-space toolbar order"): return
	for button: Button in buttons.slice(0, 5):
		var theme := EditorInterface.get_editor_theme()
		if theme.has_icon(button.name, "EditorIcons"):
			if not check(button.icon == theme.get_icon(button.name, "EditorIcons") and button.text == "", "native " + str(button.name)): return
		elif not check(button.icon == null and button.text == button.accessibility_name, "tool label fallback"): return
		if not check(button.accessibility_name != "" and button.tooltip_text != "", "accessible tool " + str(button.name)): return
	var keys := [KEY_Q, KEY_W, KEY_E, KEY_R]
	for i in keys.size():
		tool_key(keys[i])
		if not check(buttons[i].button_pressed, "shortcut for " + names[i]): return
	tool_key(KEY_W)
	var overlays: PopupMenu = bar.get_node("Overlays").get_popup()
	for label in ["Anchor Pins", "Selection Bounds", "Collision", "Lights & Cameras"]:
		var i := item_index(overlays, label)
		if failed: return
		var enabled := overlays.is_item_checked(i)
		if not check(enabled == (label == "Anchor Pins"), label + " default"): return
		press_item(overlays, label)
		if not check(overlays.is_item_checked(i) != enabled, label + " toggle"): return
		press_item(overlays, label)
	var anchor: PopupMenu = bar.get_node("Anchor").get_popup()
	for label in ["New Anchor Here", "Duplicate at Antipode", "Snap to Ground", "Face Origin"]:
		if item_index(anchor, label) < 0: return
	var view: PopupMenu = bar.get_node("View").get_popup()
	for label in ["Focus Selection (F)", "Reset View", "From 3D", "Go to Origin", "Go to Antipode", "Level Horizon", "Bookmarks"]:
		if item_index(view, label) < 0: return
	press_item(view, "Reset Fly Speed")
	var speed: Label = preview.find_child("FlySpeed", true, false)
	if not check(speed.visible and speed.text.ends_with(" m/s") and speed.text.replace(",", ".").to_float() == 8, "fly speed reset readout"): return
	var selector: OptionButton = bar.get_node("Space")
	var second: Node = space.get_script().new()
	second.name = "OtherSpace"
	second.AutoStart = false
	space.add_child(second)
	press_item(view, "Rebake Preview")
	if not check(selector.visible and selector.item_count == 2, "multi-space selector"): return
	selector.select(1)
	selector.item_selected.emit(1)
	if not check(selector.text == second.name and selector.tooltip_text == str(space.get_path_to(second)), "selected space path"): return
	selector.select(0)
	selector.item_selected.emit(0)
	second.free()
	press_item(view, "Rebake Preview")
	if not check(not selector.visible and selector.item_count == 1, "single-space selector hidden"): return
	await settle()

func check_lighting(space: Node) -> void:
	var texture := preview.texture
	var revision: int = space.BakeRevision
	for edit in [[space.get_node("Target/Nested/Lamp"), "light_energy", 12.0],
			[space.get_node("Sun"), "light_energy", 0.0], [space.Environment, "FogDensity", 0.65]]:
		var object: Object = edit[0]
		var property: StringName = edit[1]
		EditorInterface.inspect_object(object)
		await frames()
		var inspector := EditorInterface.get_inspector()
		var fields := inspector.find_children("*", "EditorProperty", true, false)
		var field: EditorProperty
		for candidate: EditorProperty in fields:
			if candidate.get_edited_object() == object and candidate.get_edited_property() == property:
				field = candidate
				break
		if not check(field != null, "Inspector field " + str(property)): return
		var before: Variant = object.get(property)
		var initial := preview_frame()
		field.emit_changed(property, edit[2])
		await settle()
		if not check(is_equal_approx(object.get(property), edit[2]) and differs(initial, preview_frame()), "rendered edit " + str(property)): return
		if not check(space.BakeRevision == revision and preview.texture == texture, "incremental " + str(property)): return
		# Use Godot's editor action so the history manager also notifies the preview.
		tool_key(KEY_Z, true)
		await settle()
		if not check(is_equal_approx(object.get(property), before), "undo property " + str(property)): return
		if not check(not differs(initial, preview_frame()), "undo rendered " + str(property)): return
		if not check(space.BakeRevision == revision and preview.texture == texture, "incremental undo " + str(property)): return
	print("Editor lighting: native omni, sun and environment edits changed pixels and undid without rebaking.")

func check_placement(space: Node) -> void:
	var selection := EditorInterface.get_selection()
	selection.clear()
	tool_key(KEY_Q)
	var overlays: PopupMenu = main_screen.find_child("Overlays", true, false).get_popup()
	press_item(overlays, "Anchor Pins")
	var point := preview.size / 2
	var data := {"type": "files", "files": PackedStringArray(["res://Tile.tscn"])}
	if not check(preview._can_drop_data(point, data), "FileSystem PackedScene payload"): return
	preview._drop_data(point, data)
	await settle()
	var placed := selection.get_selected_nodes()
	if not check(placed.size() == 1 and placed[0].scene_file_path == "res://Tile.tscn" and placed[0].owner == space, "linked scene drop"): return
	tool_key(KEY_D, true)
	await settle()
	var copies := selection.get_selected_nodes()
	if not check(copies.size() == 1 and copies[0] != placed[0] and copies[0].scene_file_path == placed[0].scene_file_path, "duplicate linked instance"): return
	var copy := copies[0]
	if not check(copy.owner == space and copy.get_node("Face").owner == copy, "duplicate child ownership"): return
	tool_key(KEY_Z, true)
	await settle()
	if not check(not copy.is_inside_tree() and placed[0].is_inside_tree(), "undo duplication"): return
	selection.clear()
	mouse_button(point, true)
	mouse_button(point, false)
	if not check(selection.get_selected_nodes() == placed, "locked instance pick"): return
	space.set_editable_instance(placed[0], true)
	mouse_button(point, true)
	mouse_button(point, false)
	if not check(selection.get_selected_nodes() == [placed[0].get_node("Face")], "Editable Children pick"): return
	space.set_editable_instance(placed[0], false)
	tool_key(KEY_Z, true)
	await settle()
	if not check(not placed[0].is_inside_tree(), "undo FileSystem placement"): return
	press_item(overlays, "Anchor Pins")
	print("Editor placement: linked drop, duplicate, instance ownership and editable child picking passed.")

func check_gizmos(mesh: MeshInstance3D, space: Node) -> void:
	var original := mesh.transform
	for key in [KEY_W, KEY_E, KEY_R]:
		tool_key(key)
		var start: Vector2 = bridge.HandlePoint(mesh, preview.size, key == KEY_E)
		var motion := InputEventMouseMotion.new()
		motion.relative = Vector2(24, 18) if key == KEY_E else Vector2(30, 0)
		motion.position = start + motion.relative
		motion.button_mask = MOUSE_BUTTON_MASK_LEFT
		var revision: int = space.BakeRevision
		await frames()
		var initial := preview_frame()
		for cancel in [true, false]:
			mouse_button(start, true)
			preview.gui_input.emit(motion)
			await frames()
			if not check(bridge.HasNewWireEdges(initial, preview_frame()) and mesh.transform == original and space.BakeRevision == revision, "transient gizmo wireframe"): return
			if cancel:
				tool_key(KEY_ESCAPE)
				await settle()
				if not check(not bridge.HasNewWireEdges(initial, preview_frame()) and mesh.transform == original and space.BakeRevision == revision, "cancel gizmo"): return
			else:
				mouse_button(motion.position, false)
				await settle()
				var changed := false
				match key:
					KEY_W: changed = not mesh.position.is_equal_approx(original.origin) and mesh.basis.is_equal_approx(original.basis)
					KEY_E: changed = not mesh.basis.is_equal_approx(original.basis) and mesh.scale.is_equal_approx(original.basis.get_scale())
					KEY_R: changed = not mesh.scale.is_equal_approx(original.basis.get_scale())
				if not check(changed and space.BakeRevision > revision, "commit " + OS.get_keycode_string(key)): return
				tool_key(KEY_Z, true)
				await settle()
				if not check(mesh.transform.is_equal_approx(original), "single undo restores mesh transform"): return
	print("Editor gizmos: move, rotate, scale, Esc and one undo per edit passed.")

func live_inspector(node: Node) -> EditorInspector:
	var selection := EditorInterface.get_selection()
	selection.clear()
	selection.add_node(node)
	EditorInterface.inspect_object(node)
	await frames(5)
	return EditorInterface.get_inspector()

func radius_property(inspector: EditorInspector) -> EditorProperty:
	for property in inspector.find_children("*", "EditorProperty", true, false):
		if property.get_edited_property() == &"Radius": return property
	return null

func check_bookmarks() -> void:
	bridge.RegisterView()
	await choose_view("Material Swatch")
	if failed: return
	var view: MenuButton = main_screen.find_child("View", true, false)
	var menu: PopupMenu = view.get_popup().get_node("Bookmarks")
	view.get_popup().about_to_popup.emit()
	press_item(menu, "Save Current View…")
	var dialog := main_screen.find_children("*", "ConfirmationDialog", true, false)[0] as ConfirmationDialog
	dialog.confirmed.emit()
	dialog.hide()
	if not check(bridge.BookmarkHasId(), "bookmark stores stable provider ID"): return
	await choose_view("ClayClean")
	view.get_popup().about_to_popup.emit()
	press_item(menu, "View 1")
	await settle()
	if not check(mode.get_item_text(mode.selected) == "Material Swatch", "restore bookmarked provider"): return
	bridge.UnregisterView()
	view.get_popup().about_to_popup.emit()
	press_item(menu, "View 1")
	await settle()
	if not check(mode.get_item_text(mode.selected) == "StandardLit", "missing provider fallback"): return
	bridge.RestoreLegacy(1)
	await settle()
	if not check(mode.get_item_text(mode.selected) == "ClayClean", "legacy mode without extension"): return
	bridge.RestoreState()
	await settle()
	print("Editor bookmarks: stable provider ID, unregister/reload, missing provider and legacy index passed.")

func check_authored(space: Node) -> void:
	var target: Node3D = space.get_node("Target")
	var nested: Node3D = target.get_node("Nested")
	var absolute: Node3D = target.get_node("Distortion")
	var mesh: MeshInstance3D = target.get_node("Block")
	var collider: CollisionShape3D = target.get_node("Base/Collider")
	var walker: Node3D = space.get_node("Walker")
	var manager := EditorInterface.get_editor_undo_redo()
	var history := manager.get_history_undo_redo(manager.get_object_history_id(space))
	var initial_nested := nested.transform
	var initial_mesh := mesh.mesh
	var initial_shape := collider.shape
	var initial_mount: Transform3D = absolute.get_node("Mount").transform
	var old_offset: Vector3 = absolute.LocalOffset
	var body_radius: float = walker.BodyRadius
	var eye_height: float = walker.EyeHeight
	var angles := Vector2(absolute.BearingDegrees, absolute.PolarDegrees)
	var probe: Node = load("res://Acceptance.cs").new()
	add_child(probe)
	await check_bookmarks()
	if failed: return
	await choose_view("StandardLit")
	if failed: return
	await check_lighting(space)
	if failed: return
	await check_placement(space)
	if failed: return
	var to_pixels := preview.get_viewport().get_final_transform() * preview.get_global_transform_with_canvas()
	var pixels := preview.size * to_pixels.get_scale().abs()
	if not check(preview.texture.get_size().distance_to(pixels) < 1.5, "native preview resolution"): return

	var center := preview.size * 0.5
	tool_key(KEY_Q)
	mouse_button(center, true)
	mouse_button(center, false)
	await frames()
	if not check(EditorInterface.get_selection().get_selected_nodes() == [mesh], "Q picks the source mesh"): return
	await check_gizmos(mesh, space)
	if failed: return
	await live_inspector(target)
	tool_key(KEY_W)
	# Project the known equatorial pivot independently from the editor gizmo.
	center = preview.size * 0.5
	var pivot := center + Vector2.DOWN * center.y * sin(eye_height / 14) / (tan(4.0 / 14) * tan(deg_to_rad(35)))
	var end := pivot + Vector2(56, 0)
	mouse_button(pivot, true)
	var motion := InputEventMouseMotion.new()
	motion.position = end
	motion.relative = end - pivot
	motion.button_mask = MOUSE_BUTTON_MASK_LEFT
	preview.gui_input.emit(motion)
	mouse_button(end, false)
	await settle()
	var spin: PackedFloat64Array = target.RelativeSpin
	if not check(spin.size() == 8 and history.get_current_action_name() == "Move S³ anchor", "RelativeSpin gizmo drag"): return
	history.undo()
	await settle()
	if not check(target.RelativeSpin.is_empty(), "drag undo"): return
	history.redo()
	await settle()
	if not check(target.RelativeSpin == spin, "drag redo"): return
	var means := [probe.VertexMean(space, target), probe.VertexMean(space, nested), probe.VertexMean(space, absolute)]

	var inspector := await live_inspector(space)
	var radius := radius_property(inspector)
	if not check(radius != null, "native Radius property"): return
	radius.emit_changed(&"Radius", 25.0)
	await settle()
	var units_preserved: bool = (target.ArcMetres == 4 and nested.transform == initial_nested
			and absolute.LocalOffset == old_offset and target.RelativeSpin == spin
			and Vector2(absolute.BearingDegrees, absolute.PolarDegrees) == angles)
	if not check(space.Radius == 25 and units_preserved, "unit-preserving radius edit"): return
	history.undo()
	await settle()
	if not check(space.Radius == 14, "radius undo"): return
	var scale_to: SpinBox = inspector.find_child("ScaleToRadius", true, false)
	if not check(scale_to != null, "scale inspector"): return
	scale_to.value = 25
	for button in inspector.find_children("*", "Button", true, false):
		if button.text == "Scale Entire Scene": button.pressed.emit()
	await settle()
	var k := 25.0 / 14
	var scaled_basis := Basis.IDENTITY.scaled(Vector3.ONE * k)
	for pass_index in 2:
		var parent_scaled: bool = (space.Radius == 25 and is_equal_approx(target.ArcMetres, 4*k)
				and target.basis.is_equal_approx(scaled_basis) and nested.transform == initial_nested)
		var absolute_scaled: bool = (absolute.basis.is_equal_approx(scaled_basis)
				and absolute.LocalOffset.is_equal_approx(old_offset*k) and absolute.get_node("Mount").transform == initial_mount)
		var dimensions_scaled := (is_equal_approx(walker.BodyRadius, body_radius*k)
				and is_equal_approx(walker.EyeHeight, eye_height*k) and is_equal_approx(nested.get_node("Lamp").omni_range, 6*k))
		if not check(parent_scaled, "chart scale applied once"): return
		if not check(absolute_scaled, "nested absolute chart scaled independently"): return
		if not check(mesh.mesh == initial_mesh and collider.shape == initial_shape, "shared resources"): return
		if not check(dimensions_scaled, "metric dimensions"): return
		if not check(Vector2(absolute.BearingDegrees, absolute.PolarDegrees) == angles, "preserved spherical angles"): return
		if not check(target.RelativeSpin == spin, "preserved relative spin"): return
		var owners := [target, nested, absolute]
		for i in owners.size():
			if not check(means[i].distance_to(probe.VertexMean(space, owners[i])) < 0.000001, "normalized baked geometry preserved after scale"): return
		history.undo()
		await settle()
		var restored: bool = (space.Radius == 14 and target.ArcMetres == 4 and target.transform == Transform3D.IDENTITY
				and absolute.LocalOffset == old_offset and nested.transform == initial_nested and is_equal_approx(walker.EyeHeight, eye_height))
		if not check(restored, "scale undo restores authored units"): return
		if pass_index == 0:
			history.redo()
			await settle()
	history.undo() # Restore the original, unspun placement for the runtime comparison.
	await settle()
	if not check(target.RelativeSpin.is_empty(), "return to authored pose"): return
	inspector = await live_inspector(absolute)
	var hint: Label = inspector.find_child("ChartDistortion", true, false)
	if not check(hint != null and "4.67 m" in hint.text and "18.4°" in hint.text and "10.0%" in hint.text, "exact transformed-vertex distortion readout"): return
	var saved_offset: Vector3 = absolute.LocalOffset
	absolute.LocalOffset = saved_offset + Vector3(0, 0.25, 0)
	await live_inspector(space)
	inspector = await live_inspector(absolute)
	hint = inspector.find_child("ChartDistortion", true, false)
	if not check(hint != null and "4.82 m" in hint.text and "19.0°" in hint.text and "10.6%" in hint.text and "second anchor" in hint.text, "over-threshold distortion hint"): return
	absolute.LocalOffset = saved_offset
	await settle()
	print("Editor pick, drag, undo/redo, 14 -> 25 m, scale undo/redo; ", hint.text.replace("\n", ", "))
	inspector = await live_inspector(space)
	var distances: Label = inspector.find_child("RadiusDistances", true, false)
	if not check(distances != null and "87.96 m" in distances.text and "21.99 m" in distances.text and "43.98 m" in distances.text, "radius readouts"): return
	var reference := ConfigFile.new()
	reference.set_value("frame", "sample", probe.Measure(space))
	probe.free()
	check(reference.save(OS.get_environment("GLOME_EDITOR_REFERENCE")) == OK, "save editor ray")
