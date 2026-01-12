using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Xabbo.Core;
using WpfPoint = System.Windows.Point;

namespace WallAligner.Core;

public class PathDistributor
{
    public static PathGeometry loadgeometryfromstring(string pathdata)
    {
        return parsesvgpathdata(pathdata);
    }

    private static PathGeometry parsesvgpathdata(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            throw new ArgumentException("Path data is empty");

        input = input.Trim();

        if (input.Contains("<path") || input.Contains("<svg"))
        {
            return extractsvgpaths(input);
        }

        try
        {
            return Geometry.Parse(input).GetFlattenedPathGeometry();
        }
        catch
        {
            var pathregex = new Regex(@"d=[""']([^""']+)[""']", RegexOptions.IgnoreCase);
            var match = pathregex.Match(input);
            if (match.Success)
            {
                return Geometry.Parse(match.Groups[1].Value).GetFlattenedPathGeometry();
            }

            throw new InvalidOperationException("Could not parse path data");
        }
    }

    public static List<WallLocation> distributefromgeometry(PathGeometry geometry, double scale, int itemcount, int startx, int starty, int roomscale, int wallheight, bool anyfloor, int maxx, Func<WallLocation, double>? calcheight = null, bool placeontop = false, int maxy = 0)
    {
        var cloned = geometry.Clone();
        cloned.Transform = new ScaleTransform(scale, scale);
        return distributepath(cloned, itemcount, startx, starty, roomscale, wallheight, anyfloor, maxx, calcheight, placeontop, maxy);
    }

    public static PathGeometry loadgeometryfromfile(string filepath)
    {
        if (!File.Exists(filepath))
            throw new FileNotFoundException($"File not found: {filepath}");

        var ext = Path.GetExtension(filepath).ToLowerInvariant();

        PathGeometry? geometry = null;

        if (ext == ".svg")
        {
            string svgcontent = File.ReadAllText(filepath);
            geometry = extractsvgpaths(svgcontent);
        }
        else if (ext == ".png" || ext == ".jpg" || ext == ".jpeg" || ext == ".bmp" || ext == ".gif")
        {
            geometry = convertimagetopath(filepath);
        }
        else
        {
            throw new NotSupportedException($"Unsupported file type: {ext}");
        }

        if (geometry == null)
            throw new InvalidOperationException("Failed to load geometry from file");

        return geometry;
    }

    private static PathGeometry extractsvgpaths(string svgcontent)
    {
        var pathregex = new Regex(@"<path[^>]*\sd=[""']([^""']+)[""']", RegexOptions.IgnoreCase);
        var matches = pathregex.Matches(svgcontent);

        if (matches.Count == 0)
        {
            throw new InvalidOperationException("No path elements found in SVG file");
        }

        var geometrygroup = new GeometryGroup();

        foreach (Match match in matches)
        {
            string pathdata = match.Groups[1].Value;
            try
            {
                var geo = Geometry.Parse(pathdata);
                geometrygroup.Children.Add(geo);
            }
            catch { }
        }

        if (geometrygroup.Children.Count == 0)
        {
            throw new InvalidOperationException("Failed to parse any valid paths from SVG");
        }

        return geometrygroup.GetFlattenedPathGeometry();
    }

    private static PathGeometry convertimagetopath(string filepath)
    {
        var bitmap = new BitmapImage(new Uri(filepath, UriKind.Absolute));

        var geometry = new PathGeometry();
        var figure = new PathFigure();
        bool figurecreated = false;

        int width = bitmap.PixelWidth;
        int height = bitmap.PixelHeight;

        var formattedbm = new FormatConvertedBitmap(bitmap, System.Windows.Media.PixelFormats.Bgra32, null, 0);

        int stride = width * 4;
        byte[] pixels = new byte[height * stride];
        formattedbm.CopyPixels(pixels, stride, 0);

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int index = y * stride + x * 4;
                byte b = pixels[index];
                byte g = pixels[index + 1];
                byte r = pixels[index + 2];
                byte a = pixels[index + 3];

                if (a > 128 && r < 128 && g < 128 && b < 128)
                {
                    if (!figurecreated)
                    {
                        figure.StartPoint = new WpfPoint(x, y);
                        figurecreated = true;
                    }
                    else
                    {
                        figure.Segments.Add(new LineSegment(new WpfPoint(x, y), true));
                    }
                }
            }
        }

        if (figurecreated)
        {
            geometry.Figures.Add(figure);
        }

        return geometry.GetFlattenedPathGeometry();
    }

    private static List<WallLocation> distributepath(PathGeometry path, int itemcount, int startx, int starty, int roomscale, int wallheight, bool anyfloor, int maxx, Func<WallLocation, double>? calcheight, bool placeontop = false, int maxy = 0)
    {
        var locations = new List<WallLocation>();
        int basely = roomscale;

        int unitsize = 64 / roomscale;
        const int HALF_W = 32;

        for (int i = 0; i < itemcount; i++)
        {
            double fraction = itemcount > 1 ? i / (itemcount - 1.0) : 0.0;
            path.GetPointAtFractionLength(fraction, out WpfPoint p, out _);

            int pixelx = startx + (int)Math.Round(p.X);
            int pixely = starty + (int)Math.Round(p.Y);

            int adjustedx = pixelx + HALF_W;
            int wx = (int)Math.Floor((double)adjustedx / HALF_W);
            int lx = (int)Math.Round((adjustedx - wx * HALF_W) / (double)unitsize);

            int lxstep = HALF_W / unitsize;
            while (lx < 0) { wx--; lx += lxstep; }
            while (lx >= lxstep) { wx++; lx -= lxstep; }

            int ly;
            int heightpixels = 0;

            if (calcheight != null)
            {
                var temploc = new WallLocation(new Xabbo.Core.Point(wx, 0), new Xabbo.Core.Point(lx, 0), 'r');
                double heightoffset = calcheight(temploc);

                int tilescreny = wx * 16;
                heightpixels = (int)(heightoffset * 32);
                double calculatedly = (pixely - tilescreny + heightoffset * 32) / (double)unitsize;
                ly = (int)Math.Round(calculatedly);
            }
            else
            {
                int linely = basely - (wx * 8);
                bool insideroom = wx >= 0 && wx <= maxx;

                if (insideroom && !anyfloor)
                {
                    linely += (wallheight + 2) * roomscale - (wallheight - 3);
                }

                if (!insideroom && anyfloor)
                {
                    linely -= (wallheight + 2) * roomscale;
                }

                if (insideroom && anyfloor)
                {
                    linely -= roomscale / 4 - 1;
                }

                ly = linely - (int)Math.Round((double)pixely / unitsize);
            }

            int finalwx = wx;
            int finalwy = 0;
            int finaly = ly;

            if (placeontop && maxy > 0)
            {
                int topwy = maxy;
                finalwx = wx + topwy;
                finalwy = topwy;
                finaly = ly - (heightpixels + HALF_W * topwy) / unitsize;
            }

            var loc = new WallLocation(
                new Xabbo.Core.Point(finalwx, finalwy),
                new Xabbo.Core.Point(lx, finaly),
                'r'
            );

            locations.Add(loc);
        }

        return locations;
    }
}
