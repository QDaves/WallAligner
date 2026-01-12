using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Xabbo.Core;
using Xabbo.Messages;
using Xabbo.Messages.Flash;
using APoint = Avalonia.Point;
using ARect = Avalonia.Rect;

namespace WallAligner.Core;

public partial class RoomCanvas
{
    private void onkeydown(object? sender, KeyEventArgs e)
    {
        if (isDrawingMode && selectedobject != null && (e.Key == Key.Delete || e.Key == Key.Back))
        {
            drawingobjects.Remove(selectedobject);
            selectedobject = null;
            int totalpoints = drawingobjects.Sum(o => o.GetRawPoints().Count);
            statuschanged?.Invoke($"deleted - {drawingobjects.Count} objects, {totalpoints} points");
            InvalidateVisual();
            e.Handled = true;
        }
    }

    private void onpointerdown(object? sender, PointerPressedEventArgs e)
    {
        var room = currentroom;
        if (room == null) return;

        var pos = e.GetPosition(this);
        var roompos = toroom(pos);
        var props = e.GetCurrentPoint(this).Properties;

        if (isDrawingMode && props.IsLeftButtonPressed)
        {
            if (selectedobject != null)
            {
                var bounds = selectedobject.GetBounds();
                var center = selectedobject.GetCenter();
                double rotatehandley = bounds.miny - 30;

                if (Math.Sqrt(Math.Pow(pos.X - center.X, 2) + Math.Pow(pos.Y - rotatehandley, 2)) < 12)
                {
                    rotatingobject = true;
                    objectoriginalrotation = selectedobject.Rotation;
                    objectdragstart = pos;
                    e.Pointer.Capture(this);
                    return;
                }

                if (Math.Sqrt(Math.Pow(pos.X - (bounds.maxx + 10), 2) + Math.Pow(pos.Y - center.Y, 2)) < 12)
                {
                    scalingobject = true;
                    objectoriginalscale = selectedobject.Scale;
                    objectdragstart = pos;
                    e.Pointer.Capture(this);
                    return;
                }

                if (Math.Sqrt(Math.Pow(pos.X - center.X, 2) + Math.Pow(pos.Y - center.Y, 2)) < 12)
                {
                    draggingobject = true;
                    objectoriginalpos = selectedobject.Position;
                    objectdragstart = pos;
                    e.Pointer.Capture(this);
                    return;
                }
            }

            DrawingObject? hitobj = null;
            for (int i = drawingobjects.Count - 1; i >= 0; i--)
            {
                if (drawingobjects[i].HitTest(pos))
                {
                    hitobj = drawingobjects[i];
                    break;
                }
            }

            if (hitobj != null)
            {
                foreach (var obj in drawingobjects) obj.Selected = false;
                hitobj.Selected = true;
                selectedobject = hitobj;

                draggingobject = true;
                objectoriginalpos = hitobj.Position;
                objectdragstart = pos;
                e.Pointer.Capture(this);
                InvalidateVisual();

                int totalpoints = drawingobjects.Sum(o => o.GetRawPoints().Count);
                statuschanged?.Invoke($"selected object - {drawingobjects.Count} objects, {totalpoints} points");
                return;
            }

            if (selectedobject != null)
            {
                selectedobject.Selected = false;
                selectedobject = null;
                InvalidateVisual();
            }

            if (drawingshapetype == 0)
            {
                isdrawing = true;
                currentsegment = new List<APoint>();
                currentsegment.Add(pos);
                e.Pointer.Capture(this);
                InvalidateVisual();
            }
            return;
        }

        if (props.IsRightButtonPressed)
        {
            selecting = true;
            selectstart = selectend = pos;
            selected.Clear();
            e.Pointer.Capture(this);
            InvalidateVisual();
            return;
        }

        if (previewbox.HasValue && props.IsLeftButtonPressed)
        {
            var box = previewbox.Value;
            var center = new APoint(box.Center.X, box.Center.Y);
            double scalewidth = box.Width;

            if (Math.Abs(pos.X - (center.X - scalewidth / 2)) <= 10 && Math.Abs(pos.Y - (center.Y + 30)) <= 10)
            {
                previewboxresizing = true;
                previewboxhandle = 0;
                previewboxdragstart = pos;
                e.Pointer.Capture(this);
                return;
            }
            if (Math.Abs(pos.X - (center.X + scalewidth / 2)) <= 10 && Math.Abs(pos.Y - (center.Y + 30)) <= 10)
            {
                previewboxresizing = true;
                previewboxhandle = 1;
                previewboxdragstart = pos;
                e.Pointer.Capture(this);
                return;
            }

            double distcenter = Math.Sqrt(Math.Pow(pos.X - center.X, 2) + Math.Pow(pos.Y - center.Y, 2));
            if (distcenter <= 15)
            {
                previewboxdragging = true;
                previewboxdragstart = pos;
                e.Pointer.Capture(this);
                return;
            }
        }

        IWallItem? clicked = null;
        double mindist = 8;

        foreach (var item in room.WallItems)
        {
            var itempos = calcitemloc(room, item.Location);
            var dist = Math.Sqrt(Math.Pow(itempos.X - roompos.X, 2) + Math.Pow(itempos.Y - roompos.Y, 2));
            if (dist < mindist)
            {
                clicked = item;
                mindist = dist;
            }
        }

        if (clicked != null)
        {
            if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                if (!selected.Add(clicked.Id))
                    selected.Remove(clicked.Id);
            }
            else
            {
                if (!selected.Contains(clicked.Id))
                    selected.Clear();
                selected.Add(clicked.Id);
            }

            dragstart = pos;
            dragids = new HashSet<long>(selected);
            dragitems = dragids.Select(id =>
            {
                var item = room.WallItems.First(x => x.Id == id);
                var visualpos = calcitemloc(room, item.Location);
                return new DraggedWallItem
                {
                    item = item,
                    originallocation = item.Location,
                    location = item.Location,
                    originalheight = calcwallheight(room, item.Location),
                    originalvisualx = visualpos.X,
                    originalvisualy = visualpos.Y
                };
            }).ToArray();

            e.Pointer.Capture(this);
            InvalidateVisual();
        }
        else
        {
            panning = true;
            panstart = pos;
            e.Pointer.Capture(this);
        }
    }

    private void onpointerup(object? sender, PointerReleasedEventArgs e)
    {
        if (draggingobject)
        {
            draggingobject = false;
            e.Pointer.Capture(null);
            InvalidateVisual();
            return;
        }

        if (rotatingobject)
        {
            rotatingobject = false;
            e.Pointer.Capture(null);
            InvalidateVisual();
            return;
        }

        if (scalingobject)
        {
            scalingobject = false;
            e.Pointer.Capture(null);
            InvalidateVisual();
            return;
        }

        if (isdrawing)
        {
            isdrawing = false;
            if (currentsegment.Count > 1)
            {
                double cx = 0, cy = 0;
                foreach (var p in currentsegment) { cx += p.X; cy += p.Y; }
                cx /= currentsegment.Count;
                cy /= currentsegment.Count;

                var freehand = new FreehandDrawingObject(currentsegment, new APoint(cx, cy));
                drawingobjects.Add(freehand);

                int totalpoints = drawingobjects.Sum(o => o.GetRawPoints().Count);
                statuschanged?.Invoke($"freehand added - {drawingobjects.Count} objects, {totalpoints} points");
            }
            currentsegment.Clear();
            e.Pointer.Capture(null);
            InvalidateVisual();
            return;
        }

        if (previewboxdragging)
        {
            previewboxdragging = false;
            e.Pointer.Capture(null);
            return;
        }

        if (previewboxresizing)
        {
            previewboxresizing = false;
            previewboxhandle = -1;
            e.Pointer.Capture(null);
            return;
        }

        if (panning)
        {
            panning = false;
            e.Pointer.Capture(null);
            return;
        }

        if (selecting)
        {
            selecting = false;

            var room = currentroom;
            if (room != null)
            {
                double x = Math.Min(selectstart.X, selectend.X);
                double y = Math.Min(selectstart.Y, selectend.Y);
                double w = Math.Abs(selectend.X - selectstart.X);
                double h = Math.Abs(selectend.Y - selectstart.Y);
                var rect = new ARect(x, y, w, h);

                selected.Clear();

                foreach (var item in room.WallItems)
                {
                    var pos = calcitemloc(room, item.Location);
                    var screenpos = toscreen(pos);
                    if (rect.Contains(screenpos))
                    {
                        selected.Add(item.Id);
                    }
                }
            }

            e.Pointer.Capture(null);
            InvalidateVisual();
            return;
        }

        if (dragids.Count > 0)
        {
            var room = currentroom;
            var itemstosend = dragitems.ToArray();

            dragids.Clear();
            e.Pointer.Capture(null);

            if (room != null && extension != null)
            {
                Task.Run(async () =>
                {
                    foreach (var drag in itemstosend)
                    {
                        var locstr = drag.location.ToString();
                        try
                        {
                            extension.Send(Out.MoveWallItem, drag.item.Id, locstr);
                        }
                        catch { }
                        await Task.Delay(66);
                    }

                    Dispatcher.UIThread.Post(() =>
                    {
                        dragitems = Array.Empty<DraggedWallItem>();
                        InvalidateVisual();
                    });
                });
            }
            else
            {
                dragitems = Array.Empty<DraggedWallItem>();
            }
        }
    }

    private void onpointermove(object? sender, PointerEventArgs e)
    {
        if (draggingobject && selectedobject != null)
        {
            var pos = e.GetPosition(this);
            var delta = new APoint(pos.X - objectdragstart.X, pos.Y - objectdragstart.Y);
            selectedobject.Position = new APoint(objectoriginalpos.X + delta.X, objectoriginalpos.Y + delta.Y);
            InvalidateVisual();
            return;
        }

        if (rotatingobject && selectedobject != null)
        {
            var pos = e.GetPosition(this);
            var center = selectedobject.GetCenter();
            double startangle = Math.Atan2(objectdragstart.Y - center.Y, objectdragstart.X - center.X);
            double currentangle = Math.Atan2(pos.Y - center.Y, pos.X - center.X);
            double deltadegrees = (currentangle - startangle) * 180 / Math.PI;
            selectedobject.Rotation = objectoriginalrotation + deltadegrees;
            InvalidateVisual();
            return;
        }

        if (scalingobject && selectedobject != null)
        {
            var pos = e.GetPosition(this);
            var center = selectedobject.GetCenter();
            double startdist = Math.Sqrt(Math.Pow(objectdragstart.X - center.X, 2) + Math.Pow(objectdragstart.Y - center.Y, 2));
            double currentdist = Math.Sqrt(Math.Pow(pos.X - center.X, 2) + Math.Pow(pos.Y - center.Y, 2));
            if (startdist > 1)
            {
                double scalefactor = currentdist / startdist;
                selectedobject.Scale = Math.Max(0.1, objectoriginalscale * scalefactor);
            }
            InvalidateVisual();
            return;
        }

        if (isDrawingMode && isdrawing)
        {
            var pos = e.GetPosition(this);
            var last = currentsegment.LastOrDefault();

            if (currentsegment.Count == 0 || Math.Sqrt(Math.Pow(pos.X - last.X, 2) + Math.Pow(pos.Y - last.Y, 2)) > 3)
            {
                currentsegment.Add(pos);
                int totalpoints = drawingobjects.Sum(o => o.GetRawPoints().Count) + currentsegment.Count;
                statuschanged?.Invoke($"drawing... {totalpoints} points");
                InvalidateVisual();
            }
            return;
        }

        if (previewboxdragging && previewbox.HasValue)
        {
            var pos = e.GetPosition(this);
            var delta = new APoint(pos.X - previewboxdragstart.X, pos.Y - previewboxdragstart.Y);

            previewmouseoffset = new APoint(
                previewmouseoffset.X + delta.X,
                previewmouseoffset.Y + delta.Y
            );
            previewboxdragstart = pos;

            var now = DateTime.Now;
            if ((now - lastpreviewupdate).TotalMilliseconds >= PREVIEW_THROTTLE_MS)
            {
                lastpreviewupdate = now;
                updatepreview();
            }
            else
            {
                InvalidateVisual();
            }
            return;
        }

        if (previewboxresizing && previewbox.HasValue)
        {
            var pos = e.GetPosition(this);
            var delta = new APoint(pos.X - previewboxdragstart.X, pos.Y - previewboxdragstart.Y);
            var box = previewbox.Value;

            double newwidth = box.Width;

            if (previewboxhandle == 0)
            {
                newwidth = Math.Max(10, box.Width - delta.X * 4);
            }
            else if (previewboxhandle == 1)
            {
                newwidth = Math.Max(10, box.Width + delta.X * 4);
            }

            double centerx = box.Center.X;
            double centery = box.Center.Y;
            previewbox = new ARect(centerx - newwidth / 2, centery - newwidth / 2, newwidth, newwidth);
            previewboxdragstart = pos;

            var now = DateTime.Now;
            if ((now - lastpreviewupdate).TotalMilliseconds >= PREVIEW_THROTTLE_MS)
            {
                lastpreviewupdate = now;
                updatepreview();
            }
            else
            {
                InvalidateVisual();
            }
            return;
        }

        if (panning)
        {
            var pos = e.GetPosition(this);
            var delta = new APoint(pos.X - panstart.X, pos.Y - panstart.Y);
            offset = new APoint(
                offset.X - delta.X,
                offset.Y - delta.Y
            );
            panstart = pos;
            InvalidateVisual();
            return;
        }

        if (selecting)
        {
            selectend = e.GetPosition(this);
            InvalidateVisual();
            return;
        }

        var room = currentroom;
        if (room == null) return;

        if (dragids.Count > 0)
        {
            var pos = e.GetPosition(this);
            var delta = new APoint(pos.X - dragstart.X, pos.Y - dragstart.Y);
            var scale = (double)room.FloorPlan.Scale / 64.0;

            var plan = room.FloorPlan;
            int roomscale = plan.Scale;
            int unitsize = 64 / roomscale;

            int lxstep = HALF_W / unitsize;

            foreach (var drag in dragitems)
            {
                var origloc = drag.originallocation;

                int wx = origloc.Wall.X;
                int wy = origloc.Wall.Y;
                int lx = origloc.Offset.X + (int)(delta.X * scale);
                int ly = origloc.Offset.Y;

                while (lx < 0) { wx--; lx += lxstep; }
                while (lx >= lxstep) { wx++; lx -= lxstep; }

                int finalwx = wx;
                int finalwy = wy;

                if (placeontop)
                {
                    int topwy = plan.Size.Y;
                    int wydelta = topwy - wy;
                    finalwx = wx + wydelta;
                    finalwy = topwy;
                }
                else if (wy > 0)
                {
                    finalwx = wx - wy;
                    finalwy = 0;
                }

                int heightpixels = 0;
                int finaly = ly;

                if (autoadjust)
                {
                    double targetvisualy = drag.originalvisualy + delta.Y;

                    var temploc = new WallLocation(new Point(finalwx, finalwy), new Point(lx, 0), origloc.Orientation);
                    double heightoffset = calcwallheight(room, temploc);
                    heightpixels = (int)(heightoffset * HALF_W);

                    int tilescreny = (finalwx + finalwy) * HALF_H;
                    double calculatedly = (targetvisualy - tilescreny + heightoffset * HALF_W) / unitsize;
                    finaly = (int)Math.Round(calculatedly);
                }
                else
                {
                    finaly = origloc.Offset.Y + (int)(delta.Y * scale);
                }

                var newloc = new WallLocation(
                    new Point(finalwx, finalwy),
                    new Point(lx, finaly),
                    origloc.Orientation
                );

                drag.location = newloc;

                if (extension != null)
                {
                    var updateditem = new WallItem(drag.item) { Location = drag.location };
                    try
                    {
                        var header = extension.Messages.Resolve(In.ItemUpdate);
                        var packet = new Packet(header, extension.Session.Client.Type);
                        packet.Write(updateditem);
                        extension.Send(packet);
                    }
                    catch { }
                }
            }

            InvalidateVisual();
        }
    }
}
