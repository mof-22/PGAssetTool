using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
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

    /// One per submesh, resolved from the materials the renderer drawing this mesh holds.
    [ObservableProperty] private IReadOnlyList<PreviewImage?>? _meshTextures;

    /// Every texture in the weapon, so a skin can be tried on the model the automatic answer does
    /// not know about. Null means the automatic one.
    public ObservableCollection<TextureChoice> TextureChoices { get; } = [];

    [ObservableProperty] private TextureChoice? _chosenTexture;
    [ObservableProperty] private string _caption = "";
    [ObservableProperty] private string? _nothing = "Select a texture or a mesh.";

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
    public bool CanToggleAlpha => _picture is not null;

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
        Caption = "";
        Nothing = why ?? "Select a texture or a mesh.";
        Changed();
    }

    public void Show(PreviewImage picture, string caption, bool alphaIsCoverage)
    {
        _picture = picture;
        Mesh = null;
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

    public void Show(UnityMesh mesh, string caption, IReadOnlyList<PreviewImage?>? textures)
    {
        _picture = null;
        _automatic = textures;
        Image?.Dispose();
        Image = null;
        Mesh = mesh;
        MeshTextures = textures;
        ChosenTexture = null;
        Caption = $"{caption}   {mesh.VertexCount:N0} vertices, {mesh.Indices.Length / 3:N0} triangles"
            + (mesh.IsSkinned ? $", {mesh.BindPoses.Count} bones" : "")
            + (textures?.Any(t => t is not null) == true ? "" : ", no texture found");
        Nothing = null;
        Changed();
    }

    /// A texture chosen by hand covers the whole model, which is the point: it answers what this
    /// mesh looks like wearing a different skin, and a skin is not per-submesh.
    partial void OnChosenTextureChanged(TextureChoice? value)
    {
        MeshTextures = value?.Image is { } picture
            ? Enumerable.Repeat<PreviewImage?>(picture, Math.Max(Mesh?.SubMeshes.Count ?? 1, 1)).ToList()
            : _automatic;
    }

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

/// A texture offered for a mesh preview, loaded when it is first picked.
public sealed record TextureChoice(string Name, string Bundle, long PathId, PreviewImage? Image)
{
    public override string ToString() => Name;
}
