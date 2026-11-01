using System.Globalization;

namespace iPhoneMiror.Core.Models;

/// <summary>An iOS version such as 26.0.1.</summary>
public readonly record struct IosVersion(int Major, int Minor, int Patch) : IComparable<IosVersion>
{
    public static bool TryParse(string? text, out IosVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var parts = text.Trim().Split('.');
        if (parts.Length is < 1 or > 3) return false;
        var nums = new int[3];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out nums[i])) return false;
        }
        version = new IosVersion(nums[0], nums[1], nums[2]);
        return true;
    }

    public int CompareTo(IosVersion other)
    {
        var c = Major.CompareTo(other.Major);
        if (c != 0) return c;
        c = Minor.CompareTo(other.Minor);
        return c != 0 ? c : Patch.CompareTo(other.Patch);
    }

    public static bool operator <(IosVersion a, IosVersion b) => a.CompareTo(b) < 0;
    public static bool operator >(IosVersion a, IosVersion b) => a.CompareTo(b) > 0;
    public static bool operator <=(IosVersion a, IosVersion b) => a.CompareTo(b) <= 0;
    public static bool operator >=(IosVersion a, IosVersion b) => a.CompareTo(b) >= 0;

    public override string ToString() => Patch == 0 ? $"{Major}.{Minor}" : $"{Major}.{Minor}.{Patch}";
}
