#nullable enable
using System;
using Glome.Core;
using Godot;

namespace Glome;

[Tool, GlobalClass]
public partial class S3CharacterController : Node3D
{
    [Export] public float BodyRadius { get; set; } = 0.28f;
    [Export] public float BodyHeight { get; set; } = 1.8f;
    [Export] public float EyeHeight { get; set; } = 1.65f;
    [Export] public float WalkSpeed { get; set; } = 3.4f;
    [Export] public float SprintSpeed { get; set; } = 6.8f;
    [Export] public float FlightSpeed { get; set; } = 6.6f;
    [Export] public float MouseSensitivity { get; set; } = 0.0022f;
    [Export] public bool CaptureMouseOnStart { get; set; } = true;
    [Export] public bool EnableDefaultInput { get; set; } = true;

    private S3MotorCore? _motor;
    private S3Camera3D? _camera;
    private Input.MouseModeEnum? _previousMouseMode;
    public bool Flying => _motor?.Flying ?? false;

    private WalkPose Arrival(double radius, bool ground)
    {
        var authored = GlomeChart.At(this, radius);
        if (!ground) return new(authored, 0);
        // Walking follows the equator even when the authored transform has altitude or pitch.
        var foot = authored.P.Reject(D4.Up);
        var p = foot.Length > 1e-9 ? foot.Unit() : D4.Pole;
        var back = authored.Back.Reject(D4.Up).Reject(p);
        if (back.Length < 1e-9) back = -D4.Hodge(authored.Right, D4.Up, p);
        back = back.Unit();
        return new(new(-D4.Hodge(D4.Up, back, p), D4.Up, back, p),
            Math.Asin(Math.Clamp(-authored.Back.Dot(D4.Up), -1, 1)));
    }

    internal S3Frame AuthoredEye(double radius)
    {
        var space = GlomeChart.SpaceOf(this);
        bool ground = space?.EquatorialGroundEnabled != false && space?.Environment?.EquatorialGroundEnabled != false;
        var pose = Arrival(radius, ground);
        return ground ? pose.Eye(radius, EyeHeight) : pose.Ground;
    }

    internal void Bind(S3Camera3D camera, S3CollisionWorld world)
    {
        _camera = camera;
        var profile = new BodyProfile(BodyRadius, BodyHeight, EyeHeight);
        if (_motor != null) _motor.SetWorld(world, profile);
        else
        {
            _motor = new(world.Radius, Arrival(world.Radius, world.EquatorialGroundEnabled), profile, world);
            if (CaptureMouseOnStart && EnableDefaultInput && DisplayServer.GetName() != "headless") Capture();
        }
        camera.DrivenEye = _motor.Camera;
    }

    internal void Unbind()
    {
        if (IsInstanceValid(_camera)) _camera!.DrivenEye = null;
        _camera = null;
        _motor = null;
        ReleaseMouse();
    }

    public override void _ExitTree() => Unbind();

    public void Move(Vector3 direction, double delta, bool sprint = false)
    {
        if (_motor == null || delta <= 0) return;
        var speed = Flying ? FlightSpeed : sprint ? SprintSpeed : WalkSpeed;
        _motor.Move(direction.X, direction.Y, direction.Z, speed * delta, seconds: delta);
        _camera!.DrivenEye = _motor.Camera;
    }

    public void Look(Vector2 motion)
    {
        if (_motor == null) return;
        _motor.Look(motion.X * MouseSensitivity, -motion.Y * MouseSensitivity);
        _camera!.DrivenEye = _motor.Camera;
    }

    public override void _PhysicsProcess(double delta)
    {
        if (_motor == null || !EnableDefaultInput) return;
        static int Press(Key key, Key alternate) =>
            Input.IsPhysicalKeyPressed(key) || Input.IsPhysicalKeyPressed(alternate) ? 1 : 0;
        Move(new(Press(Key.D, Key.Right) - Press(Key.A, Key.Left),
            (Input.IsPhysicalKeyPressed(Key.Space) ? 1 : 0) - (Input.IsPhysicalKeyPressed(Key.Ctrl) ? 1 : 0),
            Press(Key.S, Key.Down) - Press(Key.W, Key.Up)), delta, Input.IsPhysicalKeyPressed(Key.Shift));
    }

    private void Capture()
    {
        _previousMouseMode ??= Input.MouseMode;
        Input.MouseMode = Input.MouseModeEnum.Captured;
    }

    private void ReleaseMouse()
    {
        if (_previousMouseMode is { } mode && Input.MouseMode == Input.MouseModeEnum.Captured)
            Input.MouseMode = mode;
        _previousMouseMode = null;
    }

    public override void _UnhandledInput(InputEvent input)
    {
        if (_motor == null || !EnableDefaultInput) return;
        if (input is InputEventKey { Pressed: true, Echo: false, Keycode: Key.Escape })
        {
            if (_previousMouseMode != null) ReleaseMouse();
            else Capture();
        }
        else if (input is InputEventMouseMotion motion && Input.MouseMode == Input.MouseModeEnum.Captured) Look(motion.Relative);
        else return;
        GetViewport().SetInputAsHandled();
    }
}
