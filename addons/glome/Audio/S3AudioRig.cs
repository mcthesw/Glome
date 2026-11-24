#nullable enable
using System;
using Glome.Core;
using Godot;

namespace Glome;

public readonly record struct AudioPathVoice(AudioStreamPlayer3D Player, float GainDb, float Pitch = 1,
                                            double DelaySeconds = 0, float? DistanceOverride = null);

[GlobalClass]
public partial class S3AudioRig : Node3D
{
    // Enable before entering a viewport whose renderer has no current 3D camera.
    [Export] public bool UseAudioCamera { get; set; }

    public override void _Ready()
    {
        GetViewport().AudioListenerEnable3D = true;
        if (UseAudioCamera)
        {
            var camera = new Camera3D { Name = "AudioCamera", CullMask = 0 };
            AddChild(camera);
            camera.MakeCurrent();
        }
        var listener = new AudioListener3D { Name = "Listener" };
        AddChild(listener);
        listener.MakeCurrent();
    }

    public AudioStreamPlayer3D CreateVoice(string name, string bus, float gainDb, float maxDistance,
                                           float unitSize, AudioStreamPlayer3D.AttenuationModelEnum attenuation)
    {
        var player = new AudioStreamPlayer3D
        {
            Name = name, Bus = bus, VolumeDb = gainDb, MaxDistance = maxDistance,
            UnitSize = unitSize, AttenuationModel = attenuation
        };
        AddChild(player);
        return player;
    }

    public void Play(AudioStream stream, S3Frame listener, D4 source, double radius,
                     AudioPathVoice direct, AudioPathVoice? complementary = null, D4? localFallback = null)
    {
        var path = S3AcousticPath.Resolve(listener, source, radius, localFallback);
        D4 d = path.LocalDirection;
        Vector3 direction = new((float)d.X, (float)d.Y, (float)d.Z);
        Schedule(direct, direction, path.DirectDistance);
        if (complementary is { } echo) Schedule(echo, -direction, path.ComplementaryDistance);

        void Schedule(AudioPathVoice voice, Vector3 arrival, double distance)
        {
            void Start()
            {
                if (!IsInstanceValid(voice.Player) || !voice.Player.IsInsideTree()) return;
                voice.Player.Position = arrival * (voice.DistanceOverride ?? Math.Max(0.15f, (float)distance));
                voice.Player.Stream = stream;
                voice.Player.VolumeDb = voice.GainDb;
                voice.Player.PitchScale = voice.Pitch;
                voice.Player.Play();
            }
            if (voice.DelaySeconds > 0) GetTree().CreateTimer(voice.DelaySeconds).Timeout += Start;
            else Start();
        }
    }
}
