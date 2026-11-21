using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;

namespace EBuild.Native
{
    public partial class Launcher
    {
        static readonly Dictionary<string, ImageSource> iconSources = new Dictionary<string, ImageSource>();

        static Image LoaderIcon(string loader, double size) =>
            AssetIcon(LoaderOptions.Any(item => item.Key == loader && item.Key != "minecraft") ? loader : "vanilla", size);

        static double Attribute(XElement element, string key, double fallback = 0) =>
            double.TryParse((string)element.Attribute(key), NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ? value : fallback;

        static Image AssetIcon(string name, double size) {
            if (!iconSources.TryGetValue(name, out var icon)) {
                var resource = Application.GetResourceStream(new Uri("pack://application:,,,/EBuild-WPF;component/icons/" + name + ".svg"));
                var drawing = new DrawingGroup();
                using (var input = resource.Stream) {
                    foreach (var item in XDocument.Load(input).Root.Elements()) {
                        Geometry geometry;
                        if (item.Name.LocalName == "path") geometry = Geometry.Parse((string)item.Attribute("d"));
                        else if (item.Name.LocalName == "rect") {
                            double radius = Attribute(item, "rx");
                            geometry = new RectangleGeometry(new Rect(Attribute(item, "x"), Attribute(item, "y"),
                                Attribute(item, "width"), Attribute(item, "height")), radius, radius);
                        } else continue;
                        string fill = (string)item.Attribute("fill");
                        string stroke = (string)item.Attribute("stroke");
                        Pen pen = null;
                        if (!String.IsNullOrEmpty(stroke) && stroke != "none") {
                            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(stroke)) { Opacity = Attribute(item, "stroke-opacity", 1) };
                            pen = new Pen(brush, Attribute(item, "stroke-width", 1));
                            if ((string)item.Attribute("stroke-linecap") == "round") pen.StartLineCap = pen.EndLineCap = PenLineCap.Round;
                        }
                        drawing.Children.Add(new GeometryDrawing(fill == "none" ? null : Brush(fill ?? "#000000"), pen, geometry));
                    }
                }
                icon = new DrawingImage(drawing);
                icon.Freeze();
                iconSources.Add(name, icon);
            }
            return new Image { Source = icon, Width = size, Height = size };
        }

        static BitmapSource ImageFromData(string data) {
            const string prefix = "data:image/png;base64,";
            if (!data.StartsWith(prefix, StringComparison.Ordinal) || data.Length > 1500000) return null;
            try { return DecodeImage(Convert.FromBase64String(data.Substring(prefix.Length)), 0); }
            catch (FormatException) { return null; }
        }

        static BitmapSource DecodeImage(byte[] bytes, int width) {
            try {
                using (var stream = new MemoryStream(bytes, false)) {
                    var image = new BitmapImage();
                    image.BeginInit();
                    image.StreamSource = stream;
                    image.CacheOption = BitmapCacheOption.OnLoad;
                    if (width != 0) image.DecodePixelWidth = width;
                    image.EndInit();
                    image.Freeze();
                    return image;
                }
            } catch (Exception error) when (error is IOException || error is NotSupportedException || error is ArgumentException || error is FileFormatException) { return null; }
        }

        static readonly HttpClient ImageClient = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) {
            Timeout = TimeSpan.FromSeconds(12)
        };

        static async Task<BitmapSource> RemoteImage(string url, int width, params string[] allowedHosts) {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.UserInfo != "" ||
                !uri.IsDefaultPort || !allowedHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase)) return null;
            using (var timeout = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(12))) {
                try {
                    using (var response = await ImageClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false)) {
                        const int limit = 1024 * 1024;
                        if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength.GetValueOrDefault() > limit) return null;
                        using (var source = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                        using (var buffer = new MemoryStream()) {
                            var chunk = new byte[8192];
                            int read;
                            while ((read = await source.ReadAsync(chunk, 0, chunk.Length, timeout.Token).ConfigureAwait(false)) != 0) {
                                if (buffer.Length + read > limit) return null;
                                buffer.Write(chunk, 0, read);
                            }
                            return DecodeImage(buffer.ToArray(), width);
                        }
                    }
                } catch (Exception error) when (error is HttpRequestException || error is IOException || error is OperationCanceledException) { return null; }
            }
        }
    }
}
