@tool
extends Node

# This scene is opened only by the acceptance runner. Keep the driver alive while
# the editor opens the real project scene; nothing is installed as an editor plugin.
var failed := false
var preview: TextureRect
var mode: OptionButton
var main_screen: Control

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
			# Read the actual editor viewport. Texture2DRD is a storage image.
			var image := get_viewport().get_texture().get_image()
			var to_pixels := preview.get_viewport().get_final_transform() * preview.get_global_transform_with_canvas()
			var region := Rect2i((to_pixels * Rect2(Vector2.ZERO, preview.size)).grow(-8))
			var crop := image.get_region(region)
			print("Editor view ", title, " output=", preview.texture.get_size(), " panel=", preview.size)
			return crop
	check(false, "Missing editor view " + title)
	return null

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
	var lit := await choose_view("StandardLit")
	if failed: return
	var clay := await choose_view("ClayClean")
	if not check(differs(lit, clay), "standard and clay frames are indistinguishable"): return
	var target := EditorInterface.get_edited_scene_root()
	await check_authored(target)
	if failed: return
	var plugin := EditorPlugin.new()
	plugin.hide_bottom_panel()
	plugin.free()
	get_viewport().warp_mouse(Vector2(2, 2))
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
	var bridge: RefCounted = load("res://EditorChecks.cs").new()
	bridge.RegisterView()
	await choose_view("Material Swatch")
	if failed: return
	var menu: MenuButton
	for child in main_screen.find_children("*", "MenuButton", true, false):
		if child.text == "Bookmarks": menu = child
	if not check(menu != null, "bookmark menu"): return
	menu.about_to_popup.emit()
	menu.get_popup().id_pressed.emit(0)
	var dialog := main_screen.find_children("*", "ConfirmationDialog", true, false)[0] as ConfirmationDialog
	dialog.confirmed.emit()
	dialog.hide()
	if not check(bridge.BookmarkHasId(), "bookmark stores stable provider ID"): return
	await choose_view("ClayClean")
	menu.about_to_popup.emit()
	menu.get_popup().id_pressed.emit(3)
	await settle()
	if not check(mode.get_item_text(mode.selected) == "Material Swatch", "restore bookmarked provider"): return
	bridge.UnregisterView()
	menu.get_popup().id_pressed.emit(3)
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
	await choose_view("Unlit")
	await choose_view("StandardLit")
	if failed: return
	var to_pixels := preview.get_viewport().get_final_transform() * preview.get_global_transform_with_canvas()
	var pixels := preview.size * to_pixels.get_scale().abs()
	if not check(preview.texture.get_size().distance_to(pixels) < 1.5, "native preview resolution"): return

	var center := preview.size * 0.5
	mouse_button(center, true)
	mouse_button(center, false)
	await frames()
	if not check(EditorInterface.get_selection().get_selected_nodes() == [target], "presented BVH pick"): return
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
