#nullable enable
using System;
using Glome.Core;
using Godot;

namespace Glome;

public readonly record struct AudioPathVoice(AudioStreamPlayer3D Player, float GainDb, float Pitch = 1,
                                            double DelaySeconds = 0, float? DistanceOverride = null);

public readonly record struct S3AudioArrival(D4 Source, bool Complementary, D4? LocalFallback);

[GlobalClass]
public partial class S3AudioRig : Node3D
{
    // Enable before entering a viewport whose renderer has no current 3D camera.
    [Export] public bool UseAudioCamera { get; set; }

    /// <summary>Raised when an in-range path starts playing, including a delayed complementary arrival.</summary>
    public event Action<AudioStream, S3AudioArrival>? Arrived;
    private int _generation;

    public void CancelPendingPlayback() => _generation++;

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
        int generation = _generation;
        Schedule(direct, direction, path.DirectDistance, false);
        if (complementary is { } echo) Schedule(echo, -direction, path.ComplementaryDistance, true);

        void Schedule(AudioPathVoice voice, Vector3 arrival, double distance, bool complementaryPath)
        {
            void Start()
            {
                if (generation != _generation || !IsInstanceValid(voice.Player) || !voice.Player.IsInsideTree()) return;
                float metres = voice.DistanceOverride ?? Math.Max(0.15f, (float)distance);
                voice.Player.Position = arrival * metres;
                voice.Player.Stream = stream;
                voice.Player.VolumeDb = voice.GainDb;
                voice.Player.PitchScale = voice.Pitch;
                voice.Player.Play();
                if (voice.Player.MaxDistance <= 0 || metres < voice.Player.MaxDistance)
                    Arrived?.Invoke(stream, new(source, complementaryPath, localFallback));
            }
            if (voice.DelaySeconds > 0) GetTree().CreateTimer(voice.DelaySeconds).Timeout += Start;
            else Start();
        }
    }
}
