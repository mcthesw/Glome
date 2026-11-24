#nullable enable
using System;

namespace Glome.Core;

public readonly record struct BodyProfile(double Radius, double Height, double EyeHeight);

public sealed class S3MotorCore
{
    private BodyProfile _profile;
    private S3CollisionWorld _collision;
    private S3Frame _body, _flight;
    private double _pitch;

    public bool Flying { get; private set; }
    public S3Frame Camera => Flying ? _flight : new WalkPose(_body, _pitch).Eye(_collision.Radius, _profile.EyeHeight);

    public S3MotorCore(double radius, WalkPose arrival, BodyProfile profile, S3CollisionWorld? collision = null)
    {
        if (collision != null && collision.Radius != radius)
            throw new ArgumentException("Collision charts must use the motor's radius.", nameof(collision));
        _collision = collision ?? new S3CollisionWorld(radius, []);
        _profile = profile;
        Visit(arrival);
    }

    public void SetWorld(S3CollisionWorld collision, BodyProfile? profile = null)
    {
        var camera = Camera;
        _collision = collision;
        _profile = profile ?? _profile;
        if (!collision.EquatorialGroundEnabled) { _flight = camera; Flying = true; }
        ClearPlacement();
    }

    public void Visit(WalkPose arrival)
    {
        if (!_collision.EquatorialGroundEnabled)
        {
            Flying = true;
            _flight = _collision.ClearPlacement(arrival.Ground.Pitch(arrival.Pitch), _profile.Radius, 0);
            return;
        }
        Flying = false;
        _body = _collision.ClearPlacement(arrival.Ground, _profile.Radius, _profile.Height);
        _pitch = arrival.Pitch;
    }

    public void ToggleFlight()
    {
        if (!_collision.EquatorialGroundEnabled) return;
        if (!Flying) { _flight = Camera; Flying = true; return; }
        Flying = false;
        _pitch = 0;
        ClearPlacement();
    }

    // Flight adds an independent six-degree-of-freedom camera. Its equatorial
    // footprint also follows great circles (project the ray's 2-plane along U).
    private void TrackFlightPosition(D4 position)
    {
        if (!_collision.EquatorialGroundEnabled) return;
        D4 ground = position.Reject(D4.Up);
        if (ground.Length < 1e-12) return; // At the vertical pole, retain the last meridian.
        ground = ground.Unit();
        D4 d = ground.Reject(_body.P);
        double angle = Math.Atan2(d.Length, ground.Dot(_body.P));
        if (angle > 1e-14)
            _body = _body.Travel(d.Length > 1e-12 ? d.Unit() : -_body.Back, angle);
    }

    private void AlignFlightHeading()
    {
        if (!_collision.EquatorialGroundEnabled) return;
        D4 back = _flight.Back.Reject(D4.Up).Reject(_body.P);
        if (back.Length < 1e-9) return;
        back = back.Unit();
        _body = new(-D4.Hodge(D4.Up, back, _body.P), D4.Up, back, _body.P);
    }

    public void ClearPlacement()
    {
        if (Flying)
        {
            _flight = _collision.ClearPlacement(_flight, _profile.Radius, 0);
            TrackFlightPosition(_flight.P);
            AlignFlightHeading();
            return;
        }
        _body = _collision.ClearPlacement(_body, _profile.Radius, _profile.Height);
    }

    public double Pitch => _pitch;

    public void Look(double yaw, double pitch)
    {
        if (Flying)
        {
            _flight = _flight.Yaw(yaw).Pitch(pitch).Orthonormalized();
            AlignFlightHeading();
        }
        else
        {
            _body = _body.Yaw(yaw);
            _pitch = Math.Clamp(_pitch + pitch, -1.54, 1.54);
        }
    }

    public void Move(double x, double y, double z, double metres, double roll = 0)
    {
        if (Flying && roll != 0) _flight = _flight.Roll(roll);
        var frame = Flying ? _flight : _body;
        D4 movement = frame.Local(x, Flying ? y : 0, z);
        if (movement.Length < 1e-12) return;
        if (Flying)
        {
            _flight = _collision.Move(frame, movement.Unit(), metres, _profile.Radius, 0, TrackFlightPosition);
            AlignFlightHeading();
        }
        else _body = _collision.Move(frame, movement.Unit(), metres, _profile.Radius, _profile.Height);
    }
}
