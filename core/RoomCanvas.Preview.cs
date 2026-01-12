using System.Globalization;
using Xabbo.Core;
using Xabbo.Messages;
using Xabbo.Messages.Flash;
using APoint = Avalonia.Point;
using ARect = Avalonia.Rect;
using WpfPoint = System.Windows.Point;
using WpfFormattedText = System.Windows.Media.FormattedText;
using WpfTypeface = System.Windows.Media.Typeface;
using WpfBrushes = System.Windows.Media.Brushes;

namespace WallAligner.Core;

public partial class RoomCanvas
{
    public void clearpreview()
    {
        previewlocations = null;
        previewbox = null;
        previewboxdragging = false;
        previewboxresizing = false;
        previewboxhandle = -1;
        previewmouseoffset = new APoint(0, 0);
        currenttext = null;
        previewfont = null;
        previewfurni = null;
        currentfilepath = null;
        isSvgMode = false;
        isDrawingMode = false;
        drawingobjects.Clear();
        currentsegment.Clear();
        drawingfurni = null;
        isdrawing = false;
        selectedobject = null;
        draggingobject = false;
        rotatingobject = false;
        scalingobject = false;
        cachedlocations = null;
        cachedscale = -1;
        cachedstartx = int.MinValue;
        cachedstarty = int.MinValue;
        cachedgeometry = null;
        cachedgeometrykey = null;
        InvalidateVisual();
    }

    public void showpreviewbox()
    {
        if (!previewbox.HasValue)
        {
            double centerx = Bounds.Width / 2;
            double centery = Bounds.Height / 2;
            double defaultwidth = 400;
            previewbox = new ARect(centerx - defaultwidth / 2, centery - defaultwidth / 2, defaultwidth, defaultwidth);
            previewmouseoffset = new APoint(0, 0);
            InvalidateVisual();
        }
    }

    public void previewtext(string text, string font, string furniname)
    {
        var room = currentroom;
        if (room == null || extension == null) return;

        bool contentchanged = currenttext != text || previewfont != font || previewfurni != furniname || isSvgMode;

        currenttext = text;
        previewfont = font;
        previewfurni = furniname;
        currentfilepath = null;
        isSvgMode = false;
        isDrawingMode = false;
        drawingobjects.Clear();
        currentsegment.Clear();
        drawingfurni = null;

        if (contentchanged)
        {
            cachedlocations = null;
            cachedscale = -1;
            cachedstartx = int.MinValue;
            cachedstarty = int.MinValue;
            cachedgeometry = null;
            cachedgeometrykey = null;
        }

        if (!previewbox.HasValue)
        {
            showpreviewbox();
        }

        if (!string.IsNullOrWhiteSpace(text) && !string.IsNullOrWhiteSpace(furniname))
        {
            updatepreview();
        }
        else
        {
            InvalidateVisual();
        }
    }

    public void previewsvg(string filepath, string pathdata, string furniname)
    {
        var room = currentroom;
        if (room == null || extension == null) return;

        bool hasfile = !string.IsNullOrWhiteSpace(filepath);
        bool haspath = !string.IsNullOrWhiteSpace(pathdata);

        bool contentchanged = currentfilepath != filepath || currentpathdata != pathdata || previewfurni != furniname || !isSvgMode;

        currentfilepath = hasfile ? filepath : null;
        currentpathdata = haspath ? pathdata : null;
        previewfurni = furniname;
        currenttext = null;
        previewfont = null;
        isSvgMode = true;
        isPathDataMode = haspath && !hasfile;
        isDrawingMode = false;
        drawingobjects.Clear();
        currentsegment.Clear();
        drawingfurni = null;

        if (contentchanged)
        {
            cachedlocations = null;
            cachedscale = -1;
            cachedstartx = int.MinValue;
            cachedstarty = int.MinValue;
            cachedgeometry = null;
            cachedgeometrykey = null;
        }

        if (!previewbox.HasValue)
        {
            showpreviewbox();
        }

        if (!string.IsNullOrWhiteSpace(furniname))
        {
            if (hasfile && File.Exists(filepath))
            {
                updatepreview();
            }
            else if (haspath)
            {
                updatepreview();
            }
            else if (hasfile)
            {
                statuschanged?.Invoke($"file not found: {filepath}");
            }
            else
            {
                InvalidateVisual();
            }
        }
        else
        {
            InvalidateVisual();
        }
    }

    private void updatepreview()
    {
        var room = currentroom;
        if (room == null || extension == null || !previewbox.HasValue)
        {
            clearpreview();
            return;
        }

        if (isSvgMode)
        {
            bool hassvgcontent = !string.IsNullOrWhiteSpace(currentfilepath) || (isPathDataMode && !string.IsNullOrWhiteSpace(currentpathdata));
            if (!hassvgcontent || string.IsNullOrWhiteSpace(previewfurni))
            {
                clearpreview();
                return;
            }
        }
        else
        {
            if (string.IsNullOrWhiteSpace(currenttext) || string.IsNullOrWhiteSpace(previewfurni))
            {
                clearpreview();
                return;
            }
        }

        try
        {
            var matchingitems = room.WallItems
                .Where(x => x.GetName()?.Equals(previewfurni, StringComparison.OrdinalIgnoreCase) == true)
                .OrderBy(x => x.Id)
                .ToArray();

            if (matchingitems.Length == 0)
            {
                clearpreview();
                return;
            }

            double centerx = Bounds.Width / 2;
            double centery = Bounds.Height / 2;
            var box = previewbox.Value;
            double boxwidth = box.Width;
            previewbox = new ARect(centerx - boxwidth / 2, centery - boxwidth / 2, boxwidth, boxwidth);
            var offsetcenter = new APoint(centerx + previewmouseoffset.X, centery + previewmouseoffset.Y);
            var roomcenter = toroom(offsetcenter);
            double scale = (boxwidth / 100.0);
            int startx = (int)Math.Round(roomcenter.X);
            int starty = (int)Math.Round(roomcenter.Y);

            var plan = room.FloorPlan;
            int roomscale = plan.Scale;
            int wallheight = plan.WallHeight;

            bool anyfloor = false;
            int maxx = 0;
            for (int y = 0; y < plan.Size.Y; y++)
            {
                for (int x = 0; x < plan.Size.X; x++)
                {
                    if (x > maxx) maxx = x;
                    if (y == 1 && plan[x, y] >= 0) anyfloor = true;
                }
            }

            bool canusecache = cachedlocations != null &&
                              Math.Abs(cachedscale - scale) < 0.01 &&
                              cachedstartx == startx &&
                              cachedstarty == starty &&
                              cachedlocations.Count == matchingitems.Length;

            List<WallLocation> locations;
            if (canusecache)
            {
                locations = cachedlocations!;
            }
            else
            {
                if (isSvgMode)
                {
                    if (isPathDataMode && !string.IsNullOrWhiteSpace(currentpathdata))
                    {
                        string geokey = $"path:{currentpathdata.GetHashCode()}";
                        if (cachedgeometry == null || cachedgeometrykey != geokey)
                        {
                            cachedgeometry = PathDistributor.loadgeometryfromstring(currentpathdata!);
                            cachedgeometrykey = geokey;
                        }
                    }
                    else
                    {
                        string geokey = $"file:{currentfilepath}";
                        if (cachedgeometry == null || cachedgeometrykey != geokey)
                        {
                            cachedgeometry = PathDistributor.loadgeometryfromfile(currentfilepath!);
                            cachedgeometrykey = geokey;
                        }
                    }
                    locations = PathDistributor.distributefromgeometry(cachedgeometry, scale, matchingitems.Length, startx, starty, roomscale, wallheight, anyfloor, maxx, loc => calcwallheight(room, loc), placeontop, plan.Size.Y);
                }
                else
                {
                    string geokey = $"{currenttext}|{previewfont}";
                    if (cachedgeometry == null || cachedgeometrykey != geokey)
                    {
                        var formattedtext = new WpfFormattedText(
                            currenttext!,
                            CultureInfo.InvariantCulture,
                            System.Windows.FlowDirection.LeftToRight,
                            new WpfTypeface(previewfont ?? "Arial"),
                            32,
                            WpfBrushes.Black,
                            1);
                        cachedgeometry = formattedtext.BuildGeometry(new WpfPoint(0, 0)).GetFlattenedPathGeometry();
                        cachedgeometrykey = geokey;
                    }
                    locations = PathDistributor.distributefromgeometry(cachedgeometry, scale, matchingitems.Length, startx, starty, roomscale, wallheight, anyfloor, maxx, loc => calcwallheight(room, loc), placeontop, plan.Size.Y);
                }

                cachedlocations = locations;
                cachedscale = scale;
                cachedstartx = startx;
                cachedstarty = starty;
            }

            previewlocations = locations;

            int previewcount = Math.Min(previewitemlimit, matchingitems.Length);
            var ext = extension;

            Task.Run(() =>
            {
                if (ext == null) return;

                for (int previewidx = 0; previewidx < previewcount; previewidx++)
                {
                    int i = (int)Math.Round((double)previewidx * (matchingitems.Length - 1) / Math.Max(1, previewcount - 1));
                    if (i >= matchingitems.Length || i >= locations.Count) break;

                    var item = matchingitems[i];
                    var loc = locations[i];

                    try
                    {
                        var updateditem = new WallItem(item) { Location = loc };
                        var header = ext.Messages.Resolve(In.ItemUpdate);
                        var packet = new Packet(header, ext.Session.Client.Type);
                        packet.Write(updateditem);
                        ext.Send(packet);
                    }
                    catch { }
                }
            });

            InvalidateVisual();
        }
        catch { }
    }

    public async void applytext(string text, string font, string furniname)
    {
        var room = currentroom;
        if (room == null || extension == null) return;

        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(furniname))
        {
            statuschanged?.Invoke("missing text or furniture name");
            return;
        }

        if (previewlocations == null || previewlocations.Count == 0)
        {
            statuschanged?.Invoke("no preview available");
            return;
        }

        try
        {
            var matchingitems = room.WallItems
                .Where(x => x.GetName()?.Equals(furniname, StringComparison.OrdinalIgnoreCase) == true)
                .OrderBy(x => x.Id)
                .ToArray();

            if (matchingitems.Length == 0)
            {
                statuschanged?.Invoke($"no items found with name '{furniname}'");
                return;
            }

            selected.Clear();
            foreach (var item in matchingitems)
                selected.Add(item.Id);

            var locations = previewlocations.ToList();
            clearpreview();
            applypathlocations(matchingitems, locations);
            InvalidateVisual();

            statuschanged?.Invoke($"applied text to {matchingitems.Length} items");
        }
        catch (Exception ex)
        {
            statuschanged?.Invoke($"error: {ex.Message}");
        }
    }
}
