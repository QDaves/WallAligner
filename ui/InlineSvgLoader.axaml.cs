using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using WallAligner.Core;
using Xabbo.Core;

namespace WallAligner;

public partial class InlineSvgLoader : UserControl
{
    public event Action<string, string, string>? previewchanged;
    public event Action<string, string, string>? sendtoserver;
    public event Action? cancelled;

    private RoomCanvas? canvas;
    private bool dragging;
    private Avalonia.Point dragstart;
    private Avalonia.Point dragoffset;
    private string selectedfurni = "";

    public InlineSvgLoader()
    {
        InitializeComponent();
        init();

        this.PointerPressed += (s, e) =>
        {
            if (!dragging)
                e.Handled = true;
        };
        this.PointerMoved += (s, e) =>
        {
            if (!dragging)
                e.Handled = true;
        };
        this.PointerReleased += (s, e) =>
        {
            if (!dragging)
                e.Handled = true;
        };
    }

    public void setup(RoomCanvas roomcanvas)
    {
        canvas = roomcanvas;
    }

    public void setfurninames(List<string> names)
    {
        var furniinput = this.FindControl<AutoCompleteBox>("furniinput");
        if (furniinput != null)
        {
            furniinput.ItemsSource = names;
        }
    }

    private void updateitemcount()
    {
        var itemcountlabel = this.FindControl<TextBlock>("itemcountlabel");
        if (itemcountlabel != null && canvas != null && !string.IsNullOrWhiteSpace(selectedfurni))
        {
            var room = canvas.currentroom;
            if (room != null)
            {
                var count = room.WallItems.Count(x => x.GetName()?.Equals(selectedfurni, StringComparison.OrdinalIgnoreCase) == true);
                itemcountlabel.Text = $"Items: {count}";
            }
        }
    }

    private void triggerpreview()
    {
        var filepathbox = this.FindControl<TextBox>("filepath");
        var pathdatabox = this.FindControl<TextBox>("pathdata");
        var limitbox = this.FindControl<TextBox>("previewlimitbox");
        var modelabel = this.FindControl<TextBlock>("modelabel");

        if (canvas == null)
            return;

        var filepath = filepathbox?.Text ?? "";
        var pathdata = pathdatabox?.Text ?? "";

        bool hasfile = !string.IsNullOrWhiteSpace(filepath);
        bool haspath = !string.IsNullOrWhiteSpace(pathdata);

        if (!hasfile && !haspath)
        {
            if (modelabel != null) modelabel.Text = "";
            return;
        }

        if (string.IsNullOrWhiteSpace(selectedfurni))
            return;

        if (limitbox != null && int.TryParse(limitbox.Text, out int limit))
        {
            canvas.previewitemlimit = Math.Max(10, Math.Min(1000, limit));
        }

        if (haspath)
        {
            if (modelabel != null) modelabel.Text = "Mode: Path Data";
            previewchanged?.Invoke("", pathdata, selectedfurni);
        }
        else
        {
            if (modelabel != null) modelabel.Text = "Mode: File";
            previewchanged?.Invoke(filepath, "", selectedfurni);
        }
    }

    private void init()
    {
        var titlebar = this.FindControl<Border>("titlebar");
        var closebtn = this.FindControl<Button>("closebtn");
        var filepathbox = this.FindControl<TextBox>("filepath");
        var browsebtn = this.FindControl<Button>("browsebtn");
        var furniinput = this.FindControl<AutoCompleteBox>("furniinput");
        if (titlebar != null)
        {
            titlebar.PointerPressed += (s, e) =>
            {
                if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                {
                    dragging = true;
                    dragstart = e.GetPosition(this.Parent as Visual);
                    dragoffset = new Avalonia.Point(Bounds.Left, Bounds.Top);
                    e.Handled = true;
                }
            };
        }

        this.PointerMoved += (s, e) =>
        {
            if (dragging && this.Parent != null)
            {
                var pos = e.GetPosition(this.Parent as Visual);
                var delta = pos - dragstart;
                var newx = dragoffset.X + delta.X;
                var newy = dragoffset.Y + delta.Y;
                Canvas.SetLeft(this, newx);
                Canvas.SetTop(this, newy);
            }
        };

        this.PointerReleased += (s, e) =>
        {
            dragging = false;
        };

        if (closebtn != null)
        {
            closebtn.Click += (s, e) => cancelled?.Invoke();
        }

        if (browsebtn != null)
        {
            browsebtn.Click += async (s, e) =>
            {
                var topLevel = TopLevel.GetTopLevel(this);
                if (topLevel == null) return;

                var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = "Select SVG or Image File",
                    AllowMultiple = false,
                    FileTypeFilter = new[]
                    {
                        new FilePickerFileType("SVG Files") { Patterns = new[] { "*.svg" } },
                        new FilePickerFileType("Image Files") { Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.bmp", "*.gif" } },
                        new FilePickerFileType("All Files") { Patterns = new[] { "*" } }
                    }
                });

                if (files.Count > 0 && filepathbox != null)
                {
                    filepathbox.Text = files[0].Path.LocalPath;
                    triggerpreview();
                }
            };
        }

        if (furniinput != null)
        {
            furniinput.TextChanged += (s, e) =>
            {
                if (!string.IsNullOrWhiteSpace(furniinput.Text))
                {
                    selectedfurni = furniinput.Text;
                    updateitemcount();
                    triggerpreview();
                }
            };
        }

        if (filepathbox != null)
        {
            filepathbox.TextChanged += (s, e) => triggerpreview();
        }

        var pathdatabox = this.FindControl<TextBox>("pathdata");
        if (pathdatabox != null)
        {
            pathdatabox.TextChanged += (s, e) => triggerpreview();
        }
    }

    public void send()
    {
        var filepathbox = this.FindControl<TextBox>("filepath");
        var pathdatabox = this.FindControl<TextBox>("pathdata");

        var filepath = filepathbox?.Text ?? "";
        var pathdata = pathdatabox?.Text ?? "";

        if (!string.IsNullOrWhiteSpace(pathdata))
        {
            sendtoserver?.Invoke("", pathdata, selectedfurni);
        }
        else if (!string.IsNullOrWhiteSpace(filepath))
        {
            sendtoserver?.Invoke(filepath, "", selectedfurni);
        }
    }
}
