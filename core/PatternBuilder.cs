using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using SkiaSharp;

namespace WallAligner.Core;

public static class PatternBuilder
{
    public static TextLayout text_layout(string value, string family) =>
        new(value, new Typeface(family), 100, Brushes.White, lineHeight: 120);

    public static IReadOnlyList<Point[]> text(string value, string family)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("Enter some text.");
        using var layout = text_layout(value, family);
        using var combined = new SKPath();
        double top = 0;
        foreach (var line in layout.TextLines)
        {
            double left = line.Start;
            foreach (var run in line.TextRuns.OfType<DrawableTextRun>())
            {
                if (run is ShapedTextRun shaped)
                {
                    var typeface = shaped.GlyphRun.GlyphTypeface;
                    using var face = SKTypeface.FromFamilyName(typeface.FamilyName,
                        new SKFontStyle((int)typeface.Weight, (int)typeface.Stretch,
                            typeface.Style == FontStyle.Normal ? SKFontStyleSlant.Upright : SKFontStyleSlant.Italic));
                    using var font = new SKFont(face, 100) { LinearMetrics = true };
                    double x = left;
                    foreach (var glyph in shaped.GlyphRun.GlyphInfos)
                    {
                        using var path = font.GetGlyphPath(glyph.GlyphIndex);
                        if (path is not null)
                            combined.AddPath(path, (float)(x + glyph.GlyphOffset.X),
                                (float)(top + line.Baseline + glyph.GlyphOffset.Y));
                        x += glyph.GlyphAdvance;
                    }
                }
                left += run.Size.Width;
            }
            top += line.Height;
        }
        return flatten(combined);
    }

    public static IReadOnlyList<Point[]> svg(string value)
    {
        if (value.Length > 4_000_000) throw new ArgumentException("The SVG is too large.");
        using var combined = new SKPath();
        if (!value.TrimStart().StartsWith('<'))
        {
            using var path = SKPath.ParseSvgPathData(value) ?? throw new FormatException("Invalid SVG path.");
            combined.AddPath(path);
        }
        else
        {
            using var reader = XmlReader.Create(new StringReader(value), new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = 4_000_000
            });
            var root = XDocument.Load(reader).Root ?? throw new FormatException("Empty SVG.");
            add(root, SKMatrix.Identity, combined);
        }
        return flatten(combined);
    }

    static void add(XElement node, SKMatrix parent, SKPath combined, int depth = 0)
    {
        if (depth > 64) throw new ArgumentException("The SVG has too many nested groups.");
        if (node.Name.LocalName is "defs" or "clipPath" or "mask" or "metadata") return;
        if ((string?)node.Attribute("display") == "none") return;
        SKMatrix matrix = SKMatrix.Concat(parent, transform((string?)node.Attribute("transform")));
        using var path = new SKPath();
        float n(string key, float fallback = 0) => number((string?)node.Attribute(key), fallback);
        switch (node.Name.LocalName)
        {
            case "path":
                using (var parsed = SKPath.ParseSvgPathData((string?)node.Attribute("d") ?? ""))
                    if (parsed is not null) path.AddPath(parsed);
                break;
            case "rect":
                path.AddRoundRect(new SKRect(n("x"), n("y"), n("x") + n("width"), n("y") + n("height")), n("rx"), n("ry", n("rx")));
                break;
            case "circle":
                path.AddCircle(n("cx"), n("cy"), n("r"));
                break;
            case "ellipse":
                path.AddOval(new SKRect(n("cx") - n("rx"), n("cy") - n("ry"), n("cx") + n("rx"), n("cy") + n("ry")));
                break;
            case "line":
                path.MoveTo(n("x1"), n("y1"));
                path.LineTo(n("x2"), n("y2"));
                break;
            case "polyline":
            case "polygon":
                var values = numbers((string?)node.Attribute("points") ?? "");
                for (int i = 0; i + 1 < values.Length; i += 2)
                    if (i == 0) path.MoveTo(values[i], values[i + 1]); else path.LineTo(values[i], values[i + 1]);
                if (node.Name.LocalName == "polygon") path.Close();
                break;
        }
        combined.AddPath(path, matrix);
        foreach (var child in node.Elements()) add(child, matrix, combined, depth + 1);
    }

    static float number(string? value, float fallback = 0) =>
        float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result) && float.IsFinite(result) ? result : fallback;

    static float[] numbers(string value) =>
        Regex.Matches(value, @"[-+]?(?:\d*\.\d+|\d+\.?\d*)(?:[eE][-+]?\d+)?")
            .Select(m => number(m.Value)).ToArray();

    static SKMatrix transform(string? value)
    {
        var result = SKMatrix.Identity;
        foreach (Match match in Regex.Matches(value ?? "", @"(matrix|translate|scale|rotate|skewX|skewY)\s*\(([^)]*)\)"))
        {
            var v = numbers(match.Groups[2].Value);
            var next = match.Groups[1].Value switch
            {
                "matrix" when v.Length == 6 => new SKMatrix(v[0], v[2], v[4], v[1], v[3], v[5], 0, 0, 1),
                "translate" when v.Length >= 1 => SKMatrix.CreateTranslation(v[0], v.Length > 1 ? v[1] : 0),
                "scale" when v.Length >= 1 => SKMatrix.CreateScale(v[0], v.Length > 1 ? v[1] : v[0]),
                "rotate" when v.Length >= 3 => SKMatrix.CreateRotationDegrees(v[0], v[1], v[2]),
                "rotate" when v.Length >= 1 => SKMatrix.CreateRotationDegrees(v[0]),
                "skewX" when v.Length >= 1 => SKMatrix.CreateSkew((float)Math.Tan(v[0] * Math.PI / 180), 0),
                "skewY" when v.Length >= 1 => SKMatrix.CreateSkew(0, (float)Math.Tan(v[0] * Math.PI / 180)),
                _ => SKMatrix.Identity
            };
            result = SKMatrix.Concat(result, next);
        }
        return result;
    }

    static IReadOnlyList<Point[]> flatten(SKPath path)
    {
        var contours = new List<Point[]>();
        int total = 0;
        using var measure = new SKPathMeasure(path, false);
        do
        {
            float length = measure.Length;
            if (!float.IsFinite(length) || length <= 0) continue;
            int count = Math.Clamp((int)Math.Ceiling(length / 2), 2, 12000);
            var points = new Point[count + 1];
            for (int i = 0; i <= count; i++)
            {
                measure.GetPositionAndTangent(length * i / count, out var pos, out _);
                points[i] = new(pos.X, pos.Y);
            }
            contours.Add(points);
            total += points.Length;
            if (contours.Count > 1000 || total > 200000) throw new ArgumentException("The drawing has too many paths.");
        } while (measure.NextContour());
        if (contours.Count == 0) throw new ArgumentException("No visible paths were found.");
        return contours;
    }

    public static IReadOnlyList<Point[]> shape(string shape)
    {
        return shape switch
        {
            "Rectangle" => [new Point[] { new(0, 0), new(200, 0), new(200, 140), new(0, 140), new(0, 0) }],
            "Triangle" => [new Point[] { new(100, 0), new(200, 170), new(0, 170), new(100, 0) }],
            "Line" => [new Point[] { new(0, 0), new(200, 0) }],
            "Star" => [Enumerable.Range(0, 11).Select(i =>
            {
                double angle = i * Math.PI / 5 - Math.PI / 2;
                double radius = i % 2 == 0 ? 100 : 45;
                return new Point(Math.Cos(angle) * radius, Math.Sin(angle) * radius);
            }).ToArray()],
            "Heart" => [Enumerable.Range(0, 97).Select(i =>
            {
                double angle = i * Math.PI / 48;
                return new Point(16 * Math.Pow(Math.Sin(angle), 3),
                    -(13 * Math.Cos(angle) - 5 * Math.Cos(2 * angle) - 2 * Math.Cos(3 * angle) - Math.Cos(4 * angle)));
            }).ToArray()],
            _ => [Enumerable.Range(0, 97).Select(i => new Point(100 + Math.Cos(i * Math.PI / 48) * 100,
                100 + Math.Sin(i * Math.PI / 48) * 100)).ToArray()]
        };
    }

    public static Point[][] outline(IReadOnlyList<Point[]> contours, double width, double rotation)
    {
        var all = contours.SelectMany(x => x).ToArray();
        if (all.Length == 0) return [];
        var center = new Point((all.Min(p => p.X) + all.Max(p => p.X)) / 2,
            (all.Min(p => p.Y) + all.Max(p => p.Y)) / 2);
        double factor = width / Math.Max(1, all.Max(p => p.X) - all.Min(p => p.X));
        double cosine = Math.Cos(rotation * Math.PI / 180), sine = Math.Sin(rotation * Math.PI / 180);
        return contours.Select(path => path.Select(p =>
        {
            var delta = (p - center) * factor;
            return new Point(delta.X * cosine - delta.Y * sine, delta.X * sine + delta.Y * cosine);
        }).ToArray()).ToArray();
    }

    public static Point[] sample(IReadOnlyList<Point[]> contours, int count, double width, double rotation)
    {
        if (count < 1 || count > 20000) throw new ArgumentOutOfRangeException(nameof(count));
        var all = contours.SelectMany(c => c).ToArray();
        if (all.Length == 0) return [];
        double left = all.Min(p => p.X), right = all.Max(p => p.X);
        double top = all.Min(p => p.Y), bottom = all.Max(p => p.Y);
        double factor = width / Math.Max(1, right - left);
        Point normalize(Point p)
        {
            double x = (p.X - (left + right) / 2) * factor;
            double y = (p.Y - (top + bottom) / 2) * factor;
            double a = rotation * Math.PI / 180;
            return new(x * Math.Cos(a) - y * Math.Sin(a), x * Math.Sin(a) + y * Math.Cos(a));
        }
        var paths = new List<(Point[] points, double[] distances, double length, bool closed)>();
        foreach (var contour in contours)
        {
            var points = contour.Select(normalize).ToArray();
            var distances = new double[points.Length];
            for (int i = 1; i < contour.Length; i++)
                distances[i] = distances[i - 1] + ((Vector)(points[i] - points[i - 1])).Length;
            if (distances.Length > 1 && distances[^1] > 0)
                paths.Add((points, distances, distances[^1], points[0] == points[^1]));
        }
        if (paths.Count == 0) return Enumerable.Repeat(normalize(all[0]), count).ToArray();
        int minimum = count >= paths.Count ? 1 : 0;
        double low = 0, high = paths.Sum(x => x.length) * Math.Max(1, count);
        for (int iteration = 0; iteration < 64; iteration++)
        {
            double step = (low + high) / 2;
            double needed = paths.Sum(x => Math.Max(minimum, x.length / step + (minimum > 0 && !x.closed ? 1 : 0)));
            if (needed > count) low = step; else high = step;
        }
        var quotas = paths.Select(x => Math.Max(minimum, x.length / high + (minimum > 0 && !x.closed ? 1 : 0))).ToArray();
        var counts = count == paths.Count ? Enumerable.Repeat(1, paths.Count).ToArray() : quotas.Select(x => (int)Math.Floor(x)).ToArray();
        int remainder = count - counts.Sum();
        foreach (int index in Enumerable.Range(0, paths.Count).OrderByDescending(i => quotas[i] - counts[i]).Take(remainder))
            counts[index]++;
        var output = new Point[count];
        int offset = 0;
        for (int path_index = 0; path_index < paths.Count; path_index++)
        {
            var path = paths[path_index];
            int amount = counts[path_index], segment = 1;
            for (int i = 0; i < amount; i++)
            {
                double distance = amount == 1 ? path.length / 2 : path.length * i / (path.closed ? amount : amount - 1);
                while (segment < path.distances.Length - 1 && path.distances[segment] <= distance) segment++;
                double start = path.distances[segment - 1], length = path.distances[segment] - start;
                output[offset++] = path.points[segment - 1] + (path.points[segment] - path.points[segment - 1]) *
                    (length > 0 ? Math.Clamp((distance - start) / length, 0, 1) : 0);
            }
        }
        return output;
    }
}
