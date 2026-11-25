#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Glome.Core;
using Godot;

namespace Glome.Diagnostics;

internal sealed class GlomeDebugBridge(string directory, string target, string latestName, double interval)
{
    internal readonly record struct Result(bool Ok, string Message, string? SnapshotPath = null,
                                           object? Data = null, bool UnsavedChanges = false);
    private double _wait;
    public bool IsBusy { get; private set; }

    public async void Poll(double delta, Func<string, JsonElement, Task<Result>> dispatch)
    {
        if (IsBusy || (_wait -= delta) > 0) return;
        _wait = interval;
        string pending = Path.Combine(directory, $"{target}-cmd.json");
        if (!File.Exists(pending)) return;
        string claimed = TemporaryPath(pending);
        try { File.Move(pending, claimed); }
        catch (IOException) { return; }
        catch (UnauthorizedAccessException error) { GD.Print($"Live debug: {error.Message}"); return; }

        IsBusy = true;
        string? cmd = null, id = null;
        try
        {
            Result result;
            try
            {
                using var request = JsonDocument.Parse(File.ReadAllBytes(claimed));
                var args = request.RootElement;
                id = StringArg(args, "id");
                cmd = StringArg(args, "cmd") ?? throw new ArgumentException("Missing cmd.");
                result = await dispatch(cmd, args);
            }
            catch (Exception error) { result = new(false, error.Message); }

            var ack = new Dictionary<string, object?>
            {
                ["ok"] = result.Ok, ["cmd"] = cmd, ["id"] = id,
                ["timestamp"] = DateTimeOffset.UtcNow, ["message"] = result.Message
            };
            if (result.SnapshotPath is not null) ack["snapshotPath"] = result.SnapshotPath;
            if (result.Data is not null) ack["data"] = result.Data;
            if (result.UnsavedChanges) ack["unsavedChanges"] = true;
            WriteJson(Path.Combine(directory, $"{target}-ack.json"), ack);
        }
        catch (Exception error) { GD.Print($"Live debug: {error.Message}"); }
        finally
        {
            IsBusy = false;
            try { File.Delete(claimed); }
            catch (IOException error) { GD.Print($"Live debug: {error.Message}"); }
            catch (UnauthorizedAccessException error) { GD.Print($"Live debug: {error.Message}"); }
        }
    }

    public Result Status(Func<string?, object> metadata)
    {
        Directory.CreateDirectory(directory);
        var data = metadata(null);
        WriteJson(Path.Combine(directory, latestName + ".json"), data);
        return new(true, "Status written.", Data: data);
    }

    public async Task<Result> Snapshot(Func<string?, object> metadata, Func<string, Task<bool>> capture)
    {
        Directory.CreateDirectory(directory);
        var history = Directory.CreateDirectory(Path.Combine(directory, "history"));
        string imagePath = Path.Combine(directory, latestName + ".png");
        string temporary = TemporaryPath(imagePath);
        try
        {
            var data = metadata(imagePath);
            if (!await capture(temporary)) return new(false, "No rendered preview. Open the S³ screen and wait for the preview.");
            string name = $"{target}-{DateTime.UtcNow:yyyyMMdd-HHmmss}";
            string archived = Path.Combine(history.FullName, name + ".png");
            for (int suffix = 1; File.Exists(archived); suffix++)
                archived = Path.Combine(history.FullName, $"{name}-{suffix}.png");
            WriteAtomic(archived, File.ReadAllBytes(temporary));
            File.Move(temporary, imagePath, overwrite: true);
            WriteJson(Path.Combine(directory, latestName + ".json"), data);
            foreach (var old in history.EnumerateFiles("*.png").OrderByDescending(file => file.LastWriteTimeUtc)
                         .ThenByDescending(file => file.Name, StringComparer.Ordinal).Skip(10))
                old.Delete();
            return new(true, $"Snapshot saved to {imagePath}", imagePath, data);
        }
        finally { File.Delete(temporary); }
    }

    private static string TemporaryPath(string path) => $"{path}.{Guid.NewGuid():N}.tmp";

    private static void WriteJson(string path, object data) =>
        WriteAtomic(path, JsonSerializer.SerializeToUtf8Bytes(data));

    private static void WriteAtomic(string path, byte[] data)
    {
        string temp = TemporaryPath(path);
        try
        {
            File.WriteAllBytes(temp, data);
            File.Move(temp, path, overwrite: true);
        }
        finally { File.Delete(temp); }
    }

    internal static string? StringArg(JsonElement args, string key) =>
        args.TryGetProperty(key, out var val) ? val.GetString() : null;

    private static double? NumberArg(JsonElement args, string key)
    {
        if (!args.TryGetProperty(key, out var value)) return null;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out double result) || !double.IsFinite(result))
            throw new ArgumentException($"{key} must be a finite number.");
        return result;
    }

    internal static T Named<T>(string name) where T : struct, Enum =>
        Enum.GetNames<T>().FirstOrDefault(value => value.Equals(name, StringComparison.OrdinalIgnoreCase)) is { } match
            ? Enum.Parse<T>(match)
            : throw new ArgumentException($"Unknown {typeof(T).Name} '{name}'. Choose from {string.Join(", ", Enum.GetNames<T>())}.");

    internal static double[] Vector(D4 v) => [v.X, v.Y, v.Z, v.W];
    internal static float[] Vector(Vector3 v) => [v.X, v.Y, v.Z];

    internal static Dictionary<string, object?> Camera(S3Frame view, double radius)
    {
        var p = view.P;
        var horizontal = p.Reject(D4.Up);
        var tangentUp = D4.Up.Reject(p);
        return new()
        {
            ["p"] = Vector(p), ["right"] = Vector(view.Right), ["up"] = Vector(view.Up), ["back"] = Vector(view.Back),
            ["polarDeg"] = double.RadiansToDegrees(Math.Atan2(Math.Sqrt(p.X * p.X + p.Z * p.Z), p.W)),
            ["azimuthDeg"] = double.RadiansToDegrees(Math.Atan2(p.X, p.Z)),
            ["heightM"] = radius * Math.Atan2(p.Y, horizontal.Length),
            ["pitchDeg"] = tangentUp.Length > 1e-10
                ? double.RadiansToDegrees(Math.Asin(Math.Clamp(-view.Back.Dot(tangentUp.Unit()), -1, 1)))
                : null
        };
    }

    internal readonly record struct CameraRequest(double? Polar, double? Azimuth, double? Height, double? Pitch, double? Yaw)
    {
        internal static CameraRequest Read(JsonElement args) => new(NumberArg(args, "polarDeg"), NumberArg(args, "azimuthDeg"),
            NumberArg(args, "heightM"), NumberArg(args, "pitchDeg"), NumberArg(args, "yawDeg"));

        internal bool HasPosition => Polar.HasValue || Azimuth.HasValue || Height.HasValue;
        internal bool HasChanges => HasPosition || Pitch.HasValue || Yaw.HasValue;

        internal S3Frame Apply(S3Frame view, double radius)
        {
            var current = Camera(view, radius);
            double polar = Angle(Polar ?? (double)current["polarDeg"]!);
            double azimuth = Angle(Azimuth ?? (double)current["azimuthDeg"]!);
            double height = Height ?? (double)current["heightM"]!;
            if (Height.HasValue && Math.Abs(height) >= radius * Math.PI / 2)
                throw new ArgumentException("heightM must be between the vertical poles.");
            var ground = S3Frame.OnEquator(polar, azimuth);
            var horizon = HasPosition ? ground.Travel(ground.Up, height / radius) : view;
            var up = D4.Up.Reject(horizon.P);
            if (up.Length < 1e-10) up = horizon.Up;
            up = up.Unit();
            var back = horizon.Back.Reject(up).Reject(horizon.P);
            if (back.Length < 1e-10) back = horizon.Right.Reject(up).Reject(horizon.P);
            back = back.Unit();
            if (Yaw is { } yaw) back = ground.Travel(ground.Up, height / radius).Yaw(Angle(yaw)).Back;
            double pitch = double.DegreesToRadians(Math.Clamp(Pitch ?? (current["pitchDeg"] as double? ?? 0), -88, 88));
            return new S3Frame(-D4.Hodge(up, back, horizon.P), up, back, horizon.P).Pitch(pitch);
        }

        private static double Angle(double degrees) => double.DegreesToRadians(Math.IEEERemainder(degrees, 360));
    }
}
