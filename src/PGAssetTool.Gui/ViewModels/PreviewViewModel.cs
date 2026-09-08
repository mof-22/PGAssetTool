using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PGAssetTool.Core.Export.Meshes;
using PGAssetTool.Core.Preview;

namespace PGAssetTool.Gui.ViewModels;

/// Whether the alpha toggle follows the asset or follows the person looking at it.
///
/// Shared by every pane, so flipping it in the editor's left half does not leave the right half
/// disagreeing, and moving between panes keeps the answer. Not saved between runs: the automatic
/// default is the better starting point each time, and a deliberate choice is about the picture in
/// front of you rather than a standing preference.
public sealed class AlphaPreference
{
    /// Null until somebody works the toggle; after that, their answer for everything.
    public bool? Chosen { get; set; }
}

/// What is shown beside the tree for whichever node is selected.
public sealed partial class PreviewViewModel(AlphaPreference? alpha = null) : ObservableObject
{
    private readonly AlphaPreference _alpha = alpha ?? new AlphaPreference();
    private PreviewImage? _picture;

    /// Set while Show is choosing the value, so the automatic default is not mistaken for a choice.
    private bool _deciding;

    [ObservableProperty] private Bitmap? _image;
    [ObservableProperty] private UnityMesh? _mesh;
    [ObservableProperty] private PreviewSound? _sound;

    /// One per submesh, resolved from the materials the renderer drawing this mesh holds.
    [ObservableProperty] private IReadOnlyList<PreviewImage?>? _meshTextures;

    /// Every texture in the weapon, so a skin can be tried on the model the automatic answer does
    /// not know about. Null means the automatic one.
    public ObservableCollection<TextureChoice> TextureChoices { get; } = [];

    [ObservableProperty] private TextureChoice? _chosenTexture;
    [ObservableProperty] private string _caption = "";
    [ObservableProperty] private string? _nothing = "Select a texture, a mesh or a sound.";

    /// How the model is being looked at.
    ///
    /// Here rather than in the control that draws it, because a view is worth more than the reading
    /// that produced it. The editor re-reads a file whenever anything in the workspace is written —
    /// saving the pack's own icon counts — and a camera owned by the control started over every
    /// time, so turning the model to a good angle and then using it as the icon threw the angle
    /// away at the moment it had proved useful.
    [ObservableProperty] private Camera _camera = new();

    /// What is on show, as far as "is this still the same thing" goes. Null for anything that has
    /// no lasting identity, which starts the view over the way a different asset does.
    private string? _subject;

    /// The view each model was last left at, by subject.
    ///
    /// Per model rather than one for the last one looked at, because comparing two models means
    /// going back and forth between them: an angle found for one is wanted again on the way back,
    /// and a single remembered view lost it the moment anything else was selected. Kept for the
    /// length of a session and no longer — an entry is a camera and a file name.
    private readonly Dictionary<string, ViewState> _views = new(StringComparer.OrdinalIgnoreCase);

    private sealed record ViewState(Camera Camera, TextureChoice? Texture);

    partial void OnCameraChanged(Camera value) => Remember();

    private void Remember()
    {
        if (_subject is { } subject) _views[subject] = new ViewState(Camera, ChosenTexture);
    }

    /// Whether the alpha channel is being honoured.
    ///
    /// Only some textures mean coverage by it. Icons do — all four hundred of them sit on an empty
    /// background — but a model texture usually carries something else there, emission most often,
    /// and honouring it punches holes in the picture or blanks it entirely. So the default follows
    /// what the object is rather than what the channel contains, and this stays available for
    /// looking at the channel deliberately.
    [ObservableProperty] private bool _showAlpha;

    public bool HasImage => Image is not null;
    public bool HasMesh => Mesh is not null;
    public bool HasSound => Sound is not null;
    public bool CanToggleAlpha => _picture is not null;

    /// Plays the clip through the speakers. Only one plays at a time; see Speaker.
    [RelayCommand]
    private void Play()
    {
        if (Sound is not { } sound) return;
        try { Audio.Speaker.Play(sound.ToWave(), TimeSpan.FromSeconds(sound.Seconds)); }
        catch (Exception ex) { Caption = $"{ex.Message}  (while playing the clip)"; }
    }

    [RelayCommand]
    private static void Silence() => Audio.Speaker.Stop();

    /// What the space bar does: start it, or stop it if it is still going.
    public void PlayOrStop()
    {
        if (Audio.Speaker.IsPlaying) Audio.Speaker.Stop();
        else Play();
    }

    partial void OnShowAlphaChanged(bool value)
    {
        if (!_deciding) _alpha.Chosen = value;
        Redraw();
    }

    public void Clear(string? why = null)
    {
        _picture = null;
        Image?.Dispose();
        Image = null;
        Mesh = null;
        Sound = null;
        Caption = "";
        Nothing = why ?? "Select a texture, a mesh or a sound.";
        Changed();
    }

    /// A clip, drawn as its envelope and playable. Nothing starts playing on its own: moving down a
    /// weapon's six sounds would otherwise mean six of them going off unasked.
    public void Show(PreviewSound sound, string caption)
    {
        _picture = null;
        Image?.Dispose();
        Image = null;
        Mesh = null;
        Sound = sound;
        Caption = $"{caption}   {sound.Describe}";
        Nothing = null;
        Changed();
    }

    public void Show(PreviewImage picture, string caption, bool alphaIsCoverage)
    {
        _picture = picture;
        Mesh = null;
        Sound = null;
        Caption = $"{caption}   {picture.Width}×{picture.Height}";
        Nothing = null;

        // What the asset is, until somebody says otherwise — and then what they said, for every
        // picture after it. Resetting to the automatic answer each time meant an icon came back with
        // its alpha honoured however many times it had just been turned off.
        var wanted = _alpha.Chosen ?? alphaIsCoverage;

        // Assigning the property redraws through its change handler, but only when the value moves.
        _deciding = true;
        var unchanged = ShowAlpha == wanted;
        ShowAlpha = wanted;
        _deciding = false;
        if (unchanged) Redraw();
    }

    /// <param name="subject">
    /// Which asset this is, so each one keeps the angle it was turned to and the texture put on it
    /// — through a second reading of the same file, and through a trip to something else and back.
    /// A model nobody has looked at yet starts square, since an angle chosen for a pistol says
    /// nothing about a rocket launcher.
    /// </param>
    public void Show(UnityMesh mesh, string caption, IReadOnlyList<PreviewImage?>? textures,
        string? subject = null)
    {
        _subject = subject;
        var seen = subject is not null && _views.TryGetValue(subject, out var before) ? before : null;

        _picture = null;
        Sound = null;
        _automatic = textures;
        Image?.Dispose();
        Image = null;
        Mesh = mesh;
        MeshTextures = textures;
        ChosenTexture = seen?.Texture;
        Camera = seen?.Camera ?? new Camera();
        Caption = $"{caption}   {mesh.VertexCount:N0} vertices, {mesh.Indices.Length / 3:N0} triangles"
            + (mesh.IsSkinned ? $", {mesh.BindPoses.Count} bones" : "")
            + (textures?.Any(t => t is not null) == true ? "" : ", no texture found");
        Nothing = null;
        Changed();
    }

    /// Going back to the automatic answer is immediate; anything else waits for its picture, which
    /// the shell reads and hands back through Wear.
    partial void OnChosenTextureChanged(TextureChoice? value)
    {
        if (value is null || value.PathId == 0) MeshTextures = _automatic;
        Remember();
    }

    /// Covers the whole model with one texture, which is the point: the question it answers is what
    /// this mesh looks like wearing a different skin, and a skin is not per-submesh.
    public void Wear(PreviewImage picture)
        => MeshTextures = Enumerable
            .Repeat<PreviewImage?>(picture, Math.Max(Mesh?.SubMeshes.Count ?? 1, 1))
            .ToList();

    private IReadOnlyList<PreviewImage?>? _automatic;

    private void Redraw()
    {
        if (_picture is null) return;

        Image?.Dispose();
        Image = ToBitmap(ShowAlpha ? _picture : _picture.Opaque());
        Changed();
    }

    private void Changed()
    {
        OnPropertyChanged(nameof(HasImage));
        OnPropertyChanged(nameof(HasMesh));
        OnPropertyChanged(nameof(HasSound));
        OnPropertyChanged(nameof(CanToggleAlpha));
    }

    private static Bitmap ToBitmap(PreviewImage picture)
    {
        var bitmap = new WriteableBitmap(
            new PixelSize(picture.Width, picture.Height), new Vector(96, 96),
            PixelFormat.Bgra8888, AlphaFormat.Unpremul);

        using (var locked = bitmap.Lock())
            System.Runtime.InteropServices.Marshal.Copy(picture.Bgra, 0, locked.Address, picture.Bgra.Length);

        return bitmap;
    }
}

/// A texture offered for a mesh preview: what it is called and where to find it, and nothing else.
///
/// Deliberately without the decoded picture. It used to carry one, filled in once the texture had
/// been read — but a record is compared by its contents, so the filled-in copy was a different value
/// from the one in the list the combo box was showing. The combo box, asked to select something it
/// did not have, selected nothing instead, and the model went straight back to the texture it
/// started with. Picking a skin appeared to do nothing at all.
public sealed record TextureChoice(string Name, string Bundle, long PathId)
{
    /// True for a texture the mesh in front of you is actually drawn with, which the list marks so
    /// the two or three that answer the obvious question are findable without reading the order.
    ///
    /// Outside equality on purpose, which is why this record writes its own. The same texture is a
    /// different answer to this question for each mesh, so the list is rebuilt with the flag turned
    /// over as somebody moves between them — and if that made it a different value, the selection
    /// carried across the rebuild would stop matching and the model would undress itself. What
    /// identifies a texture is where it is, not how it is being shown.
    public bool Worn { get; init; }

    /// True for one of this weapon's skins — what a skin material puts in its main slot, rather
    /// than the gloss or mask bound beside it. Lit fainter than what is worn, and never both at
    /// once: the skin the mesh already has on is worn, and saying it twice says nothing.
    public bool Skin { get; init; }

    public bool Equals(TextureChoice? other)
        => other is not null && Name == other.Name && Bundle == other.Bundle && PathId == other.PathId;

    public override int GetHashCode() => HashCode.Combine(Name, Bundle, PathId);

    public override string ToString() => Name;
}
