using iPhoneMiror.Core.Models;

namespace iPhoneMiror.Core.Compatibility;

public sealed record FeatureCompatibility(string Feature, FeatureSupport Support, string Detail);

public sealed record CompatibilityReport(
    IosVersion? Version,
    string Summary,
    bool IsTargetVersion,
    IReadOnlyList<FeatureCompatibility> Features);

/// <summary>
/// Evaluates what iPhoneMiror can do for a given iOS version. Versions are only reported as "Verified"
/// when they appear in <see cref="VerifiedVersions"/>, which is populated from real-device test results
/// (see docs/COMPATIBILITY.md). Nothing is claimed as verified by default.
/// </summary>
public sealed class CompatibilityEvaluator
{
    public static readonly IosVersion MinimumTarget = new(26, 0, 0);

    /// <summary>iOS major versions this release targets (26.x and 27.x).</summary>
    public static readonly IReadOnlyList<int> TargetMajors = [26, 27];

    public CompatibilityEvaluator(IEnumerable<IosVersion>? verifiedVersions = null)
    {
        VerifiedVersions = verifiedVersions?.ToHashSet() ?? [];
    }

    public IReadOnlySet<IosVersion> VerifiedVersions { get; }

    public CompatibilityReport Evaluate(DeviceInfo device) => Evaluate(device.IosVersion);

    public CompatibilityReport Evaluate(IosVersion? version)
    {
        if (version is null)
        {
            return new CompatibilityReport(null, "iOS version not known yet. Trust this computer on the iPhone to read it.", false,
                [new FeatureCompatibility("Device detection", FeatureSupport.ImplementedUnverified, "Detected through Apple Mobile Device Service.")]);
        }

        var v = version.Value;
        var verified = VerifiedVersions.Any(x => x.Major == v.Major && x.Minor == v.Minor);
        var isTarget = TargetMajors.Contains(v.Major);
        var baseSupport = verified ? FeatureSupport.Verified : FeatureSupport.ImplementedUnverified;

        string summary;
        if (v < MinimumTarget)
            summary = $"iOS {v} is older than the supported range (26.x and 27.x). Device detection may work; other features are not targeted.";
        else if (v.Major > TargetMajors.Max())
            summary = $"iOS {v} is newer than the targeted versions. Not verified.";
        else if (verified)
            summary = $"iOS {v} has been verified on real hardware.";
        else
            summary = $"iOS {v} is a target version but has NOT been verified on real hardware for this build.";

        var features = new List<FeatureCompatibility>
        {
            new("USB detection & pairing", v < MinimumTarget ? FeatureSupport.ImplementedUnverified : baseSupport,
                "usbmux + lockdown protocol via Apple Mobile Device Service."),
            new("Wi-Fi connection", baseSupport,
                "Requires \"Show this iPhone when on Wi-Fi\" (Wi-Fi sync) enabled once over USB via iTunes/Apple Devices."),
            new("Bluetooth", FeatureSupport.ImplementedUnverified,
                "Discovery and Windows pairing only. Bluetooth cannot carry screen video."),
            new("Screen mirroring (WebDriverAgent)", FeatureSupport.RequiresComponent,
                "Requires WebDriverAgent (Appium, BSD-3) signed and running on the iPhone. Building it needs Xcode on a Mac."),
            new("Screen mirroring (AirPlay)", FeatureSupport.RequiresComponent,
                "Requires UxPlay (GPL-3.0) installed separately. Video shows in UxPlay's own window; no input control."),
            new("Input control", FeatureSupport.RequiresComponent,
                "Delivered through WebDriverAgent (XCTest). iOS has no public API for remote input from Windows."),
            new("Apple iPhone Mirroring (macOS feature)", FeatureSupport.Unavailable,
                "Apple's iPhone Mirroring protocol is private, Mac-only and undocumented; it is not implemented."),
        };

        return new CompatibilityReport(v, summary, isTarget, features);
    }
}
