using OpenNV.Runtime;
using OpenNV.Runtime.Content;
using OpenNV.Runtime.Presentation.Rendering;

// Saves and launch options that must carry across devices: the portable stack
// identity ignores install paths and copy times, and explicit presentation
// options fail closed instead of silently falling back to a profile.
internal static class PortableDeviceContracts
{
    internal static void Run()
    {
        var root = Directory.CreateTempSubdirectory("opennv-portable-identity-");
        try
        {
            (IReadOnlyList<FalloutPluginSource> Plugins, IReadOnlyList<string> Archives, IReadOnlyList<string> Layers) Install(
                string device, DateTime written, long pluginBytes = 64)
            {
                var data = Directory.CreateDirectory(Path.Combine(root.FullName, device, "Data"));
                var archive = Path.Combine(data.FullName, "Synthetic - Main.bsa");
                File.WriteAllBytes(archive, new byte[32]);
                File.SetLastWriteTimeUtc(archive, written);
                var mtime = new DateTimeOffset(written).ToUnixTimeMilliseconds();
                return ([new("Synthetic.esm", Path.Combine(data.FullName, "Synthetic.esm"), pluginBytes, mtime)], [archive],
                    [data.FullName, Path.Combine(root.FullName, device, "Mods", "Loose") + Path.DirectorySeparatorChar]);
            }
            string Identity((IReadOnlyList<FalloutPluginSource> Plugins, IReadOnlyList<string> Archives, IReadOnlyList<string> Layers) stack,
                bool portable) => RuntimeLiveContentSource.ComputeLiveIdentity(stack.Plugins, stack.Archives, stack.Layers, portable);

            var desktop = Install("desktop", new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc));
            var tablet = Install("tablet", new DateTime(2026, 6, 7, 8, 9, 10, DateTimeKind.Utc));
            var edited = Install("edited", new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc), pluginBytes: 65);
            Require(Identity(desktop, portable: true) == Identity(tablet, portable: true),
                "The portable save identity changed when the same files were copied to another folder or device.");
            Require(Identity(desktop, portable: false) != Identity(tablet, portable: false),
                "The legacy save identity no longer binds the install path and modification times.");
            Require(Identity(desktop, portable: true) != Identity(edited, portable: true),
                "The portable save identity ignored a changed plugin size.");
            Require(Identity(desktop, portable: true) != Identity(desktop, portable: false),
                "Portable and legacy save identities collapsed into one value.");
        }
        finally { root.Delete(recursive: true); }

        static RendererQualityProfile Profile(float scale) => new(scale, 2, 1, 4096, 1, 0);
        var profiles = new RendererQualityProfiles(Profile(1), Profile(.75f), Profile(.5f));
        RuntimePresentationQuality Resolve(params (string Key, string Value)[] options) =>
            RuntimePresentationQuality.Resolve(options.ToDictionary(option => option.Key, option => option.Value), profiles);
        var low = Resolve(("quality", "Low"));
        Require(low is { Kind: RuntimePresentationQualityKind.Low, Source: "option", RenderScale: .5f } && low.Profile == profiles.Low,
            "--quality did not select its configured profile and render scale.");
        Require(Resolve(("quality", "high"), ("render-scale", "0.6")).RenderScale == .6f &&
            Resolve(("quality", "high"), ("render-scale", "0.1")).RenderScale == RuntimePresentationQuality.MinimumRenderScale &&
            Resolve(("quality", "low"), ("render-scale", "4")).RenderScale == 1f,
            "--render-scale did not override and clamp the profile scale.");
        Reject(() => Resolve(("quality", "ultra")));
        foreach (var scale in new[] { "fast", "NaN", "" })
            Reject(() => Resolve(("quality", "medium"), ("render-scale", scale)));
        Console.WriteLine("OPENNV_PORTABLE_DEVICE_CONTRACT_PASS portableIdentity=true legacyIdentity=true sizeBound=true " +
            "qualityOption=true renderScale=true invalidOptionsRefused=true");
    }

    private static void Reject(Action action)
    {
        try { action(); }
        catch (ArgumentException) { return; }
        throw new InvalidOperationException("An invalid presentation option was accepted.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
