using OsuCompanion.Memory;

namespace OsuCompanion.Calculation;

/// <summary>
/// Reads osu!'s hit-error buffer. In stable the offset lives in a
/// <c>List&lt;int&gt;</c>: [4] = backing array, [0xC] = size, elements at array + 8.
/// Alternate layouts are probed because the exact CLR layout varies between builds.
/// </summary>
public sealed class HitErrorReader
{
    private const int MaxErrors = 65536;

    private readonly SafeMemoryReader _reader;

    public HitErrorReader(SafeMemoryReader reader)
    {
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
    }

    public bool TryRead(IntPtr listAddress, out double[] errors)
    {
        errors = Array.Empty<double>();

        if (listAddress == IntPtr.Zero)
        {
            return false;
        }

        var layouts = _reader.Is64Bit
            ? new (int ItemsOffset, int SizeOffset)[] { (8, 0x10), (8, 0x14), (0, 8) }
            : new (int ItemsOffset, int SizeOffset)[] { (4, 8), (4, 0xC), (0, 4) };

        foreach (var layout in layouts)
        {
            if (!TryReadLayout(listAddress, layout.ItemsOffset, layout.SizeOffset, out errors))
            {
                continue;
            }

            return true;
        }

        return false;
    }

    private bool TryReadLayout(IntPtr listAddress, int itemsOffset, int sizeOffset, out double[] errors)
    {
        errors = Array.Empty<double>();

        if (!_reader.ReadIntPtr(IntPtr.Add(listAddress, itemsOffset), out var items) || items.ToInt64() < 0x10000)
        {
            return false;
        }

        if (!_reader.ReadInt32(IntPtr.Add(listAddress, sizeOffset), out var size) ||
            size <= 0 ||
            size > MaxErrors)
        {
            return false;
        }

        // Elements start after the array header (method table + length).
        var dataAddress = IntPtr.Add(items, _reader.Is64Bit ? 16 : 8);
        var values = new double[size];

        for (var i = 0; i < size; i++)
        {
            if (!_reader.ReadInt32(IntPtr.Add(dataAddress, i * sizeof(int)), out var raw))
            {
                return false;
            }

            values[i] = raw;
        }

        errors = values;
        return true;
    }
}
