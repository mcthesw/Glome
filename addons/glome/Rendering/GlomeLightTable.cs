#nullable enable
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace Glome;

/// <summary>One stable grouping pass defines both the shader ABI and every update slot.</summary>
public sealed class GlomeLightTable<TKey> where TKey : notnull
{
    private readonly Dictionary<TKey, int> _slots = [];
    private readonly LightRecord[] _records;
    public Vector4I Counts { get; }
    public int this[TKey source] => _slots[source];

    public GlomeLightTable(IEnumerable<(TKey Source, GlomeLight Light)> sources)
    {
        var ordered = sources.OrderBy(entry => entry.Light.Group).ToArray();
        _records = new LightRecord[ordered.Length];
        var counts = new Vector4I();
        for (int i = 0; i < ordered.Length; i++)
        {
            var (source, light) = ordered[i];
            _slots.Add(source, i);
            _records[i] = light.Record;
            counts[(int)light.Group]++;
        }
        Counts = counts;
    }

    public byte[] Pack() => GlomeBuffers.PackLights(Counts, _records);
}
