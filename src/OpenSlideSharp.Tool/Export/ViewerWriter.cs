using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace OpenSlideSharp.Tool.Export;

/// <summary>
/// The viewer that <c>deepzoom -r</c> writes: <c>index.html</c>, after openslide-python's
/// <c>slide-multipane.html</c> without jQuery, and the <c>static/</c> files it uses. The DZI documents are embedded in
/// the page, so it also works when opened from <c>file://</c>.
/// </summary>
internal static class ViewerWriter
{
    /// <summary>The base name of the slide's Deep Zoom files in the viewer directory, as in deepzoom_tile.py.</summary>
    public const string SlideName = "slide";

    private const string ResourcePrefix = "viewer/";

    /// <summary>
    /// Turns an associated image name into a file name the way deepzoom_tile.py's <c>_slugify</c> does: lower case,
    /// NFKD, non-ASCII characters dropped, and each run of characters other than <c>a-z0-9</c> replaced by <c>_</c>.
    /// </summary>
    public static string Slugify(string text)
    {
        string decomposed = text.ToLowerInvariant().Normalize(NormalizationForm.FormKD);
        var slug = new StringBuilder(decomposed.Length);
        bool separator = false;
        foreach (char c in decomposed)
        {
            if (c > 0x7F)
            {
                continue; // dropped before the substitution, so it does not separate anything
            }

            if (c is (>= 'a' and <= 'z') or (>= '0' and <= '9'))
            {
                if (separator)
                {
                    slug.Append('_');
                    separator = false;
                }

                slug.Append(c);
            }
            else
            {
                separator = true;
            }
        }

        if (separator)
        {
            slug.Append('_');
        }

        return slug.ToString();
    }

    /// <summary>
    /// Gives each associated image a file base name: its <see cref="Slugify"/> slug, made unique where two names
    /// share a slug, a slug is empty or is one of <paramref name="reserved"/>, by appending <c>_2</c>, <c>_3</c>
    /// and so on. Names are taken in ordinal order, so the result does not depend on the slide's order.
    /// </summary>
    public static IReadOnlyList<KeyValuePair<string, string>> AssignSlugs(IEnumerable<string> names, params string[] reserved)
    {
        var sorted = new List<string>(names);
        sorted.Sort(StringComparer.Ordinal);
        var used = new HashSet<string>(reserved, StringComparer.Ordinal);
        var result = new List<KeyValuePair<string, string>>(sorted.Count);
        foreach (string name in sorted)
        {
            string slug = Slugify(name);
            if (slug.Length == 0)
            {
                slug = "image";
            }

            string unique = slug;
            for (int n = 2; !used.Add(unique); n++)
            {
                unique = slug + "_" + n.ToString(CultureInfo.InvariantCulture);
            }

            result.Add(new KeyValuePair<string, string>(name, unique));
        }

        return result;
    }

    /// <summary>
    /// The mean of <c>openslide.mpp-x</c> and <c>openslide.mpp-y</c> for the scale bar, or <c>"0"</c> when either is
    /// missing or invalid, as in deepzoom_tile.py.
    /// </summary>
    public static string FormatMpp(IReadOnlyDictionary<string, string> properties)
    {
        if (properties.TryGetValue(OpenSlideProperties.MppX, out string? x)
            && properties.TryGetValue(OpenSlideProperties.MppY, out string? y)
            && double.TryParse(x, NumberStyles.Float, CultureInfo.InvariantCulture, out double mppX)
            && double.TryParse(y, NumberStyles.Float, CultureInfo.InvariantCulture, out double mppY))
        {
            double mpp = (mppX + mppY) / 2;
            if (double.IsFinite(mpp) && mpp > 0)
            {
                return mpp.ToString("R", CultureInfo.InvariantCulture);
            }
        }

        return "0";
    }

    /// <summary>Renders <c>index.html</c>. All slide text is HTML-escaped; the DZI documents are JSON in the script.</summary>
    /// <param name="slideMpp">The micrometers per pixel of the slide, from <see cref="FormatMpp"/>.</param>
    /// <param name="associated">Associated image names and their DZI file names, in display order.</param>
    /// <param name="properties">The slide properties, in display order.</param>
    /// <param name="dziData">The DZI document of each DZI file name.</param>
    public static string RenderIndex(
        string slideMpp,
        IReadOnlyList<KeyValuePair<string, string>> associated,
        IReadOnlyList<KeyValuePair<string, string>> properties,
        IReadOnlyDictionary<string, string> dziData)
    {
        var html = new StringBuilder(Head);
        html.Append("        <a class=\"load-slide\" href=\"#\" data-url=\"").Append(Encode(SlideName + ".dzi"))
            .Append("\" data-mpp=\"").Append(Encode(slideMpp)).Append("\">Slide</a>\n");
        html.Append("    </div>\n");
        html.Append("    <h2>Associated images</h2>\n");
        if (associated.Count > 0)
        {
            html.Append("    <ul class=\"associated-images\">\n");
            foreach (KeyValuePair<string, string> image in associated)
            {
                html.Append("        <li><a class=\"load-slide\" href=\"#\" data-url=\"").Append(Encode(image.Value))
                    .Append("\" data-mpp=\"0\">").Append(Encode(image.Key)).Append("</a></li>\n");
            }

            html.Append("    </ul>\n");
        }
        else
        {
            html.Append("    <span class=\"associated-images notice\">None</span>\n");
        }

        html.Append("</div>\n");
        html.Append("<div id=\"view\"></div>\n");
        html.Append("<div id=\"properties\">\n");
        html.Append("    <h1>Slide properties</h1>\n");
        if (properties.Count > 0)
        {
            html.Append("    <div id=\"properties-inner\">\n");
            html.Append("        <dl>\n");
            foreach (KeyValuePair<string, string> property in properties)
            {
                html.Append("            <dt>").Append(Encode(property.Key)).Append("</dt>\n");
                html.Append("            <dd>").Append(Encode(property.Value)).Append("</dd>\n");
            }

            html.Append("        </dl>\n");
            html.Append("    </div>\n");
        }
        else
        {
            html.Append("    <span class=\"notice\">None</span>\n");
        }

        html.Append("</div>\n");

        // The default encoder escapes <, >, & and quotes, so the JSON cannot end the script element.
        html.Append(Script.Replace("/*DZI_DATA*/{}", JsonSerializer.Serialize(dziData), StringComparison.Ordinal));
        return html.ToString();
    }

    /// <summary>Writes the viewer files (OpenSeadragon, the scale bar plugin, images and licenses) under <paramref name="directory"/>.</summary>
    public static void WriteStatic(TileWriter writer, string directory)
    {
        Assembly assembly = typeof(ViewerWriter).Assembly;
        foreach (string resource in assembly.GetManifestResourceNames())
        {
            if (!resource.StartsWith(ResourcePrefix, StringComparison.Ordinal))
            {
                continue;
            }

            // RecursiveDir in the logical name uses the build machine's separator.
            string relative = resource[ResourcePrefix.Length..].Replace('\\', '/');
            string path = Path.Combine(directory, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            using Stream stream = assembly.GetManifestResourceStream(resource)!;
            using var content = new MemoryStream();
            stream.CopyTo(content);
            writer.WriteFile(path, content.GetBuffer().AsSpan(0, (int)content.Length));
        }
    }

    private static string Encode(string text) => WebUtility.HtmlEncode(text);

    // Normalized, so the page is the same whatever line endings this source file was checked out with.
    private static readonly string Head = """
        <!doctype html>
        <meta charset="utf-8">
        <title>Slide Viewer</title>
        <meta name="viewport" content="user-scalable=no">

        <style>
        html {
            overflow: hidden;
        }
        body {
            margin: 5px;
        }
        h1 {
            font-size: 1.2em;
        }
        .notice {
            font-style: italic;
        }
        div#images {
            position: absolute;
            width: 15%;
        }
        div#images h2 {
            font-size: 1em;
            margin: 0;
        }
        .associated-images {
            margin-left: 1.5em;
            margin-top: 0;
            margin-bottom: 0;
            padding-left: 0;
        }
        div#images li {
            list-style-type: none;
        }
        .current-slide {
            background-color: #ccf;
        }
        div#view {
            position: absolute;
            left: 16%;
            width: 58%;
            height: 98%;
            background-color: black;
            border: 1px solid #000;
            color: white;
        }
        div#view.fullpage {
            left: 0%;
            border: 0;
        }
        div#properties {
            position: absolute;
            left: 75%;
            width: 24.5%;
            height: 98%;
            overflow: auto;
        }
        div#properties-inner {
            display: inline-block;
        }
        div#properties dl {
            font-size: small;
            margin: 0;
        }
        div#properties dt {
            font-weight: bold;
            border-top: 1px solid #000;
            background-color: #eee;
        }
        div#properties dd {
            margin-left: 0;
        }
        </style>

        <div id="images">
            <h1>View</h1>
            <div class="current-slide">

        """.ReplaceLineEndings("\n");

    private static readonly string Script = """

        <script src="static/openseadragon.min.js"></script>
        <script src="static/openseadragon-scalebar.js"></script>
        <script>
        (function () {
            'use strict';

            // The DZI documents, in the page because browsers do not let a page opened from file:// read other files.
            var dziData = /*DZI_DATA*/{};

            var viewer = OpenSeadragon({
                id: 'view',
                prefixUrl: 'static/images/',
                timeout: 120000,
                animationTime: 0.5,
                blendTime: 0.1,
                constrainDuringPan: true,
                maxZoomPixelRatio: 2,
                minZoomImageRatio: 1,
                visibilityRatio: 1,
                zoomPerScroll: 2
            });
            viewer.scalebar({
                xOffset: 10,
                yOffset: 10,
                barThickness: 3,
                color: '#555555',
                fontColor: '#333333',
                backgroundColor: 'rgba(255, 255, 255, 0.5)'
            });

            function openSlide(url, mpp) {
                var tileSource = url;
                if (Object.prototype.hasOwnProperty.call(dziData, url)) {
                    tileSource = new OpenSeadragon.DziTileSource(
                        OpenSeadragon.DziTileSource.prototype.configure(OpenSeadragon.parseXml(dziData[url]), url));
                }
                viewer.open(tileSource);
                viewer.scalebar({
                    pixelsPerMeter: mpp ? (1e6 / mpp) : 0
                });
            }

            var links = document.querySelectorAll('a.load-slide');
            links.forEach(function (link) {
                link.addEventListener('click', function (event) {
                    event.preventDefault();
                    var current = document.querySelector('.current-slide');
                    if (current) {
                        current.classList.remove('current-slide');
                    }
                    link.parentElement.classList.add('current-slide');
                    openSlide(link.dataset.url, parseFloat(link.dataset.mpp));
                });
            });
            openSlide(links[0].dataset.url, parseFloat(links[0].dataset.mpp));
        }());
        </script>

        """.ReplaceLineEndings("\n");
}
