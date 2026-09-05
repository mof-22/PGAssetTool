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
    [ObservableProperty] private Bitmap? _image;
    [ObservableProperty] private UnityMesh? _mesh;
    [ObservableProperty] private string _caption = "";
    [ObservableProperty] private string? _nothing = "Select a texture or a mesh.";

    public bool HasImage => Image is not null;
    public bool HasMesh => Mesh is not null;

    public void Clear(string? why = null)
    {
        Image?.Dispose();
        Image = null;
        Mesh = null;
        Caption = "";
        Nothing = why ?? "Select a texture or a mesh.";
        Changed();
    }

    public void Show(PreviewImage picture, string caption)
    {
        Image?.Dispose();
        Image = ToBitmap(picture);
        Mesh = null;
        Caption = $"{caption}   {picture.Width}×{picture.Height}";
        Nothing = null;
        Changed();
    }

    public void Show(UnityMesh mesh, string caption)
    {
        Image?.Dispose();
        Image = null;
        Mesh = mesh;
        Caption = $"{caption}   {mesh.VertexCount:N0} vertices, {mesh.Indices.Length / 3:N0} triangles"
            + (mesh.IsSkinned ? $", {mesh.BindPoses.Count} bones" : "");
        Nothing = null;
        Changed();
    }

    private void Changed()
    {
        OnPropertyChanged(nameof(HasImage));
        OnPropertyChanged(nameof(HasMesh));
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
