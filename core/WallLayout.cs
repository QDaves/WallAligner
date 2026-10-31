using Qx.Model;
using APoint = Avalonia.Point;

namespace WallAligner.Core;

public static class WallLayout
{
    public static double height(RoomView room, int x, int y)
    {
        if (x < 0 || y < 0 || x >= room.plan.Width || y >= room.plan.Length) return 0;
        if (room.plan[x, y] >= 0 && room.entry != new Point(x, y))
            return room.plan[x, y];
        double baseline = 3.59375;
        if (room.plan.WallHeight >= 0)
            return baseline + room.plan.WallHeight * (room.entry == new Point(x, y) ? 1 : 2);
        return baseline + (room.entry == new Point(x, y) ? 0 : Math.Max(0, room.plan.Tiles.DefaultIfEmpty(0).Max()));
    }

    public static APoint project(RoomView room, WallLocation location)
    {
        double unit = 64.0 / room.plan.Scale;
        return new((location.Wall.X - location.Wall.Y) * 32 + location.Offset.X * unit -
            (location.Orientation.IsRight ? 32 : 0),
            (location.Wall.X + location.Wall.Y) * 16 + location.Offset.Y * unit -
            height(room, location.Wall.X, location.Wall.Y) * 32);
    }

    public static WallLocation locate(RoomView room, APoint point, WallLocation original, bool front = false)
        => locate(room, point, original, front ? room.plan.Length : 0);

    public static WallLocation locate(RoomView room, APoint point, WallLocation original, int layer)
    {
        double unit = 64.0 / room.plan.Scale;
        int wy = Math.Clamp(layer, 0, room.plan.Length);
        double adjusted = point.X + (original.Orientation.IsRight ? 32 : 0);
        int wx = (int)Math.Floor(adjusted / 32) + wy;
        int lx = (int)Math.Round((adjusted - (wx - wy) * 32) / unit);
        int ly = (int)Math.Round((point.Y - (wx + wy) * 16 + height(room, wx, wy) * 32) / unit);
        return new(wx, wy, lx, ly, original.Orientation);
    }
}
