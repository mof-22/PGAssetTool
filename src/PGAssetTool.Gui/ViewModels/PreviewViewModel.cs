using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using CommunityToolkit.Mvvm.ComponentModel;
using PGAssetTool.Core.Export.Meshes;
using PGAssetTool.Core.Preview;

namespace PGAssetTool.Gui.ViewModels;

/// What is shown beside the tree for whichever node is selected.
public sealed partial class PreviewViewModel : ObservableObject
{
    private PreviewImage? _picture;

    [ObservableProperty] private Bitmap? _image;
    [ObservableProperty] private UnityMesh? _mesh;
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

    partial void OnShowAlphaChanged(bool value) => Redraw();

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

        // Assigning the property redraws through its change handler, but only when the value moves.
        var unchanged = ShowAlpha == alphaIsCoverage;
        ShowAlpha = alphaIsCoverage;
        if (unchanged) Redraw();
    }

    public void Show(UnityMesh mesh, string caption)
    {
        _picture = null;
        Image?.Dispose();
        Image = null;
        Mesh = mesh;
        Caption = $"{caption}   {mesh.VertexCount:N0} vertices, {mesh.Indices.Length / 3:N0} triangles"
            + (mesh.IsSkinned ? $", {mesh.BindPoses.Count} bones" : "");
        Nothing = null;
        Changed();
    }

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
