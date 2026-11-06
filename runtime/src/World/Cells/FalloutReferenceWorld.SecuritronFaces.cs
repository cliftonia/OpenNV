using OpenNV.Runtime.Content;

namespace OpenNV.Runtime.World.Cells;

// SetSecuritronExpression stores a (personality, emotion) pair on the placed
// actor. The face texture is textures\creatures\securitron\<P>_<E>.dds; an
// empty word selects no texture. Words are bare script tokens, never paths.
internal sealed record FalloutSecuritronFace(FalloutFormKey Reference, string Personality, string Emotion)
{
    internal const string ScreenNode = "Screen01:0";
    private const string TextureDirectory = "textures\\creatures\\securitron\\";

    internal string? TexturePath => Personality.Length == 0 || Emotion.Length == 0
        ? null : (TextureDirectory + Personality + "_" + Emotion + ".dds").ToLowerInvariant();

    internal static string Word(string value) => value.All(character => char.IsAsciiLetterOrDigit(character) || character == '_')
        ? value : throw new InvalidDataException("Securitron expression words must be bare letters, digits or underscores.");
}

internal sealed record FalloutSecuritronFacesSnapshot(IReadOnlyList<FalloutSecuritronFace> Faces)
{
    internal void Validate()
    {
        if (Faces is null || Faces.Any(face => face is null || face.Reference.ObjectId == 0 || string.IsNullOrWhiteSpace(face.Reference.OwnerPlugin) ||
            face.Personality is null || face.Emotion is null) || Faces.Select(face => face.Reference).Distinct().Count() != Faces.Count)
            throw new InvalidDataException("Saved Securitron faces are invalid or duplicated.");
        foreach (var face in Faces) { _ = FalloutSecuritronFace.Word(face.Personality); _ = FalloutSecuritronFace.Word(face.Emotion); }
    }
}

internal sealed partial class FalloutReferenceWorld
{
    private readonly Dictionary<FalloutFormKey, FalloutSecuritronFace> _securitronFaces = [];

    // Increments whenever a face changes so resident presentation re-applies.
    internal long SecuritronFaceRevision { get; private set; }

    internal void SetSecuritronFace(FalloutFormKey reference, string personality, string emotion)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (records.GetEffective(reference).Signature is not ("ACHR" or "ACRE"))
            throw new InvalidDataException("Securitron expression target is not a placed actor.");
        _securitronFaces[reference] = new(reference, FalloutSecuritronFace.Word(personality), FalloutSecuritronFace.Word(emotion));
        SecuritronFaceRevision++;
    }

    internal FalloutSecuritronFace? SecuritronFace(FalloutFormKey reference) => _securitronFaces.GetValueOrDefault(reference);

    internal FalloutSecuritronFacesSnapshot? CaptureSecuritronFaces() => _securitronFaces.Count == 0 ? null :
        new(_securitronFaces.Values.OrderBy(face => records.RuntimeFormId(face.Reference)).ToArray());

    internal void RestoreSecuritronFaces(FalloutSecuritronFacesSnapshot? snapshot)
    {
        if (_securitronFaces.Count != 0) throw new InvalidOperationException("Securitron face restore requires a fresh world.");
        if (snapshot is null) return;
        snapshot.Validate();
        foreach (var face in snapshot.Faces)
            if (records.GetEffective(face.Reference).Signature is not ("ACHR" or "ACRE"))
                throw new InvalidDataException("Saved Securitron face target is not a placed actor.");
        foreach (var face in snapshot.Faces) _securitronFaces.Add(face.Reference, face);
        SecuritronFaceRevision++;
    }
}
