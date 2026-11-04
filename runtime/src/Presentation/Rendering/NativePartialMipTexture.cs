using Godot;
using OpenNV.Runtime.Formats.Gamebryo;

namespace OpenNV.Runtime.Presentation.Rendering;

/// <summary>Uploads exactly the authored levels, without filling or discarding a partial mip pyramid.</summary>
internal sealed partial class NativePartialMipTexture : Texture2Drd
{
    private RenderingDevice? _device;
    private Rid _ownedImage;

    internal static NativePartialMipTexture Create(byte[] bytes, FalloutDdsMipChain chain)
    {
        // Texture2DRD maps BC1 only through Godot's opaque BC1_RGB owner, the
        // same format its own DXT1 path uses. Encoded transparency is kept by
        // expanding every authored level to RGBA8, as NativeDdsTexture.PreserveAlpha
        // does for complete pyramids; no levels are generated or discarded.
        var transparentBc1 = chain.Format == "BC1" && FalloutBc1Alpha.ContainsTransparency(bytes.AsSpan(128),
            chain.Levels[0].Width, chain.Levels[0].Height, chain.Levels.Count);
        var blockFormat = transparentBc1 ? RenderingDevice.DataFormat.R8G8B8A8Unorm : chain.Format switch
        {
            "BC1" => RenderingDevice.DataFormat.Bc1RgbUnormBlock,
            "BC2" => RenderingDevice.DataFormat.Bc2UnormBlock,
            "BC3" => RenderingDevice.DataFormat.Bc3UnormBlock,
            _ => throw new NotSupportedException("Unknown authored block compression format."),
        };
        var payload = transparentBc1 ? ExpandBc1(bytes, chain) : bytes[128..];
        var result = new NativePartialMipTexture();
        Exception? failure = null;
        using var completed = new ManualResetEventSlim();
        RenderingServer.CallOnRenderThread(Callable.From(() =>
        {
            try
            {
                result._device = RenderingServer.GetRenderingDevice() ??
                    throw new NotSupportedException("Authored partial mip textures require the Forward+ or Mobile renderer.");
                using var format = new RDTextureFormat
                {
                    Width = (uint)chain.Levels[0].Width,
                    Height = (uint)chain.Levels[0].Height,
                    Mipmaps = (uint)chain.Levels.Count,
                    TextureType = RenderingDevice.TextureType.Type2D,
                    Format = blockFormat,
                    UsageBits = RenderingDevice.TextureUsageBits.SamplingBit | RenderingDevice.TextureUsageBits.CanCopyFromBit,
                };
                using var view = new RDTextureView();
                result._ownedImage = result._device.TextureCreate(format, view, [payload]);
                if (!result._ownedImage.IsValid) throw new InvalidDataException("The renderer rejected the authored partial mip texture.");
                result.TextureRdRid = result._ownedImage;
            }
            catch (Exception error) { failure = error; }
            finally { completed.Set(); }
        }));
        completed.Wait();
        if (failure is not null) { result.Dispose(); throw new InvalidDataException("Partial DDS upload failed.", failure); }
        result.SetMeta("opennv_dds_source_format", chain.Format);
        result.SetMeta("opennv_dds_authored_levels", chain.Levels.Count);
        return result;
    }

    private static byte[] ExpandBc1(byte[] bytes, FalloutDdsMipChain chain)
    {
        using var expanded = new MemoryStream();
        foreach (var level in chain.Levels)
        {
            using var image = Image.CreateFromData(level.Width, level.Height, false, Image.Format.Dxt1,
                bytes.AsSpan(level.Offset, level.Bytes).ToArray());
            if (image.Decompress() != Error.Ok || image.GetFormat() != Image.Format.Rgba8)
                throw new InvalidDataException("Partial BC1 level could not be expanded to RGBA8.");
            expanded.Write(image.GetData());
        }
        return expanded.ToArray();
    }

    public override void _Notification(int what)
    {
        if (what == NotificationPredelete) ReleaseImage();
    }

    protected override void Dispose(bool disposing)
    {
        ReleaseImage();
        base.Dispose(disposing);
    }

    private void ReleaseImage()
    {
        if (_device is null || !_ownedImage.IsValid) return;
        var device = _device;
        var image = _ownedImage;
        _ownedImage = default;
        // Detach the presentation view before releasing its independently owned
        // RenderingDevice image. Resource.Dispose need not send PREDELETE yet.
        TextureRdRid = default;
        RenderingServer.CallOnRenderThread(Callable.From(() => device.FreeRid(image)));
    }

    internal byte[] ReadAuthoredBytes()
    {
        byte[]? bytes = null;
        Exception? failure = null;
        using var completed = new ManualResetEventSlim();
        RenderingServer.CallOnRenderThread(Callable.From(() =>
        {
            try { bytes = _device!.TextureGetData(_ownedImage, 0); }
            catch (Exception error) { failure = error; }
            finally { completed.Set(); }
        }));
        completed.Wait();
        return bytes ?? throw new InvalidDataException("Authored mip GPU readback is unavailable.", failure);
    }
}
