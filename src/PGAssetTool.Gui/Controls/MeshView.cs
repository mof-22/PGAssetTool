using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using PGAssetTool.Core.Export.Meshes;
using PGAssetTool.Core.Preview;

namespace PGAssetTool.Gui.Controls;

/// Shows a mesh, turned by dragging and zoomed by the wheel.
///
/// The rasterizer lives in the core and writes straight into the bitmap's own memory, so a frame is
/// one pass over the triangles and a blit; nothing is allocated per frame except when the control
/// is resized.
public sealed class MeshView : Control
{
    public static readonly StyledProperty<UnityMesh?> MeshProperty =
        AvaloniaProperty.Register<MeshView, UnityMesh?>(nameof(Mesh));

    /// One texture per submesh, in Unity order. Null entries and a short list are both fine; those
    /// parts are drawn plain.
    public static readonly StyledProperty<IReadOnlyList<PreviewImage?>?> TexturesProperty =
        AvaloniaProperty.Register<MeshView, IReadOnlyList<PreviewImage?>?>(nameof(Textures));

    public UnityMesh? Mesh
    {
        get => GetValue(MeshProperty);
        set => SetValue(MeshProperty, value);
    }

    public IReadOnlyList<PreviewImage?>? Textures
    {
        get => GetValue(TexturesProperty);
        set => SetValue(TexturesProperty, value);
    }

    private WriteableBitmap? _bitmap;
    private readonly RenderTarget _target = new();
    private PixelSize _size;
    private Camera _camera = new();
    private Point? _dragging;

    static MeshView()
    {
        AffectsRender<MeshView>(MeshProperty, TexturesProperty);
        MeshProperty.Changed.AddClassHandler<MeshView>((view, _) =>
        {
            // A new model gets a fresh viewpoint; keeping the old one leaves the next mesh at
            // whatever angle happened to suit the last.
            view.Recentre();
        });
    }

    /// Set while the middle button is down, which pans instead of turning.
    private bool _panning;

    /// Puts the view back to square. A tilt or a pan is easy to lose track of, and hunting the way
    /// back by hand is worse than either was useful.
    public void Recentre()
    {
        _camera = new Camera();
        InvalidateVisual();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        _panning = e.GetCurrentPoint(this).Properties.IsMiddleButtonPressed;
        _dragging = e.GetPosition(this);
        e.Pointer.Capture(this);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        if (_dragging is not { } from) return;
        var to = e.GetPosition(this);
        _dragging = to;

        // The middle button drags the model around the frame, as it does in the modelling tools
        // this sits beside. Zooming in on the muzzle of a rocket launcher is otherwise impossible:
        // the view is centred on the whole model and only the middle of it can be reached.
        if (_panning)
        {
            var half = Math.Min(Bounds.Width, Bounds.Height) * 0.5;
            if (half > 0)
                _camera = _camera.Panned((float)((to.X - from.X) / half), (float)((from.Y - to.Y) / half));
            InvalidateVisual();
            return;
        }

        // Shift tilts the model in the plane of the screen instead of turning it. A modifier rather
        // than a mode: the ordinary drag keeps working exactly as it did, and this is the third way
        // of turning something, which orbiting alone cannot reach.
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            _camera = _camera.Rolled((float)((to.X - from.X) * 0.01));
            InvalidateVisual();
            return;
        }

        // Dragging takes the model with it: cursor right turns the near face right. The opposite
        // convention — moving the camera instead — reads as the model going the wrong way.
        // A drag across the full width turns it most of the way round.
        _camera = _camera.Dragged((float)((to.X - from.X) * 0.01), (float)((to.Y - from.Y) * 0.01));
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        _dragging = null;
        _panning = false;
        e.Pointer.Capture(null);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        _camera = _camera.Zoomed(e.Delta.Y > 0 ? 0.88f : 1.14f);
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        var width = (int)Bounds.Width;
        var height = (int)Bounds.Height;
        if (Mesh is not { } mesh || width < 2 || height < 2) return;

        if (_bitmap is null || _size.Width != width || _size.Height != height)
        {
            _size = new PixelSize(width, height);
            _bitmap?.Dispose();
            _bitmap = new WriteableBitmap(_size, new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
            _target.Resize(width, height);
        }

        MeshRenderer.Render(mesh, _camera, _target, Textures);

        using (var locked = _bitmap.Lock())
            System.Runtime.InteropServices.Marshal.Copy(_target.Bgra, 0, locked.Address, _target.Bgra.Length);

        context.DrawImage(_bitmap, new Rect(0, 0, width, height));
    }
}
