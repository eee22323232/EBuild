using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;

namespace EBuild.Native
{
    sealed class PlayerPreview : Viewport3D
    {
        readonly Model3DGroup body = new Model3DGroup();
        readonly AxisAngleRotation3D yaw = new AxisAngleRotation3D(new Vector3D(0, 1, 0), 18);
        readonly AxisAngleRotation3D pitch = new AxisAngleRotation3D(new Vector3D(1, 0, 0), -8);
        static readonly BitmapSource DefaultSkin = BuildDefaultSkin();
        const int PreviewPixelsPerTexel = 8;
        const double CameraFrameHeight = 41;
        Point drag;

        internal PlayerPreview() {
            Height = 230;
            Focusable = true;
            AutomationProperties.SetName(this, "角色模型，可用方向键旋转");
            RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor);
            var camera = new OrthographicCamera(new Point3D(0, 0, 58), new Vector3D(0, 0, -1), new Vector3D(0, 1, 0), CameraFrameHeight);
            Camera = camera;
            SizeChanged += (_, e) => {
                // WPF measures the camera width horizontally. Keep the vertical framing stable as the window resizes.
                if (e.NewSize.Width > 0 && e.NewSize.Height > 0)
                    camera.Width = CameraFrameHeight * e.NewSize.Width / e.NewSize.Height;
            };
            body.Transform = new Transform3DGroup { Children = { new RotateTransform3D(yaw), new RotateTransform3D(pitch) } };
            Children.Add(new ModelVisual3D { Content = new AmbientLight(Colors.White) });
            Children.Add(new ModelVisual3D { Content = body });
            MouseLeftButtonDown += (_, e) => { Focus(); CaptureMouse(); drag = e.GetPosition(this); };
            MouseLeftButtonUp += (_, __) => ReleaseMouseCapture();
            MouseMove += (_, e) => {
                if (!IsMouseCaptured || e.LeftButton != MouseButtonState.Pressed) return;
                var point = e.GetPosition(this);
                yaw.Angle += (point.X - drag.X) * .9;
                pitch.Angle = Math.Max(-80, Math.Min(80, pitch.Angle - (point.Y - drag.Y) * .65));
                drag = point;
            };
            KeyDown += (_, e) => {
                switch (e.Key) {
                    case Key.Left: yaw.Angle -= 15; break;
                    case Key.Right: yaw.Angle += 15; break;
                    case Key.Up: pitch.Angle = Math.Min(80, pitch.Angle + 10); break;
                    case Key.Down: pitch.Angle = Math.Max(-80, pitch.Angle - 10); break;
                    default: return;
                }
                e.Handled = true;
            };
        }

        internal void Turn(bool back) { yaw.Angle = back ? 180 : 0; pitch.Angle = -6; }

        internal static BitmapSource Avatar(BitmapSource skin) {
            skin = ValidSkin(skin);
            const int faceSize = 8;
            var bounds = new Rect(0, 0, faceSize, faceSize);
            var drawing = new DrawingVisual();
            using (var layer = drawing.RenderOpen()) {
                layer.DrawImage(new CroppedBitmap(skin, new Int32Rect(8, 8, faceSize, faceSize)), bounds);
                layer.DrawImage(new CroppedBitmap(skin, new Int32Rect(40, 8, faceSize, faceSize)), bounds);
            }
            var image = new RenderTargetBitmap(faceSize, faceSize, 96, 96, PixelFormats.Pbgra32);
            image.Render(drawing);
            image.Freeze();
            return image;
        }

        static BitmapSource ValidSkin(BitmapSource image) => image != null && image.PixelWidth == 64 &&
            (image.PixelHeight == 32 || image.PixelHeight == 64) ? image : DefaultSkin;

        internal void Set(BitmapSource skin, BitmapSource cape, bool slim) {
            skin = ValidSkin(skin);
            bool legacy = skin.PixelHeight == 32;
            int arm = slim && !legacy ? 3 : 4;
            skin = ScaleTexture(skin);
            body.Children.Clear();
            Part(skin, -4, 8, -4, 8, 8, 8, 0, 0, 32, 0, false, true);
            Part(skin, -4, -4, -2, 8, 12, 4, 16, 16, 16, 32, false, !legacy);
            Part(skin, -4 - arm, -4, -2, arm, 12, 4, 40, 16, 40, 32, false, !legacy);
            Part(skin, 4, -4, -2, arm, 12, 4, legacy ? 40 : 32, legacy ? 16 : 48, 48, 48, legacy, !legacy);
            Part(skin, -4, -16, -2, 4, 12, 4, 0, 16, 0, 32, false, !legacy);
            Part(skin, 0, -16, -2, 4, 12, 4, legacy ? 0 : 16, legacy ? 16 : 48, 0, 48, legacy, !legacy);
            if (cape != null && cape.PixelWidth >= 64 && cape.PixelWidth <= 512 && cape.PixelWidth % 64 == 0 && cape.PixelHeight == cape.PixelWidth / 2)
                Box(ScaleTexture(cape), -5, -8.5, -3.15, 10, 16, 1, 0, 0, false, 0, true);
        }

        static BitmapSource ScaleTexture(BitmapSource source) {
            int factor = PreviewPixelsPerTexel * 64 / source.PixelWidth;
            if (factor <= 1) return source;
            var image = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            int width = image.PixelWidth, height = image.PixelHeight;
            var input = new int[width * height];
            image.CopyPixels(input, width * 4, 0);
            int outputWidth = width * factor;
            var output = new int[outputWidth * height * factor];
            for (int y = 0; y < height * factor; y++)
                for (int x = 0; x < outputWidth; x++)
                    output[y * outputWidth + x] = input[y / factor * width + x / factor];
            var scaled = BitmapSource.Create(outputWidth, height * factor, 96, 96, PixelFormats.Bgra32, null, output, outputWidth * 4);
            scaled.Freeze();
            return scaled;
        }

        void Part(BitmapSource texture, double x, double y, double z, int w, int h, int d, int u, int v,
            int outerU, int outerV, bool mirror, bool overlay) {
            Box(texture, x, y, z, w, h, d, u, v, mirror, 0, false);
            if (overlay) Box(texture, x, y, z, w, h, d, outerU, outerV, false, .22, false);
        }

        void Box(BitmapSource texture, double x, double y, double z, int w, int h, int d, int u, int v, bool mirror, double pad, bool cape) {
            double right = x + w + pad, top = y + h + pad, front = z + d + pad;
            x -= pad; y -= pad; z -= pad;
            var f = new Point3D(x, top, front); var fr = new Point3D(right, top, front);
            var b = new Point3D(right, top, z); var bl = new Point3D(x, top, z);
            var lf = new Point3D(x, y, front); var rf = new Point3D(right, y, front);
            var rb = new Point3D(right, y, z); var lb = new Point3D(x, y, z);
            int frontU = u + d, backU = u + 2 * d + w;
            Face(texture, cape ? backU : frontU, v + d, w, h, f, fr, lf, rf, mirror);
            Face(texture, cape ? frontU : backU, v + d, w, h, b, bl, rb, lb, mirror);
            Face(texture, mirror ? u + d + w : u, v + d, d, h, bl, f, lb, lf, mirror);
            Face(texture, mirror ? u : u + d + w, v + d, d, h, fr, b, rf, rb, mirror);
            Face(texture, u + d, v, w, d, bl, b, f, fr, mirror);
            Face(texture, u + d + w, v, w, d, lf, rf, lb, rb, mirror);
        }

        void Face(BitmapSource texture, int x, int y, int w, int h, Point3D a, Point3D b, Point3D c, Point3D d, bool mirror) {
            int scale = texture.PixelWidth / 64;
            var region = new Int32Rect(x * scale, y * scale, w * scale, h * scale);
            var crop = new CroppedBitmap(texture, region);
            var brush = new ImageBrush(crop) { Stretch = Stretch.Fill };
            RenderOptions.SetBitmapScalingMode(brush, BitmapScalingMode.NearestNeighbor);
            var mesh = new MeshGeometry3D {
                Positions = new Point3DCollection { a, b, c, d },
                TriangleIndices = new Int32Collection { 0, 2, 1, 1, 2, 3 },
                TextureCoordinates = new PointCollection { new Point(mirror ? 1 : 0, 0), new Point(mirror ? 0 : 1, 0),
                    new Point(mirror ? 1 : 0, 1), new Point(mirror ? 0 : 1, 1) }
            };
            body.Children.Add(new GeometryModel3D(mesh, new DiffuseMaterial(brush)));
        }

        static BitmapSource BuildDefaultSkin() {
            const int size = 64;
            var pixels = new byte[size * size * 4];
            void Fill(int x, int y, int width, int height, Color color) {
                for (int row = y; row < y + height; row++)
                    for (int col = x; col < x + width; col++) {
                        var i = (row * size + col) * 4;
                        pixels[i] = color.B; pixels[i + 1] = color.G; pixels[i + 2] = color.R; pixels[i + 3] = color.A;
                    }
            }
            void Unwrap(int u, int v, int w, int h, int d, Color color) {
                Fill(u + d, v, w * 2, d, color);
                Fill(u, v + d, 2 * d + 2 * w, h, color);
            }
            var skin = Color.FromRgb(188, 134, 95);
            var shirt = Color.FromRgb(75, 143, 131);
            var pants = Color.FromRgb(63, 80, 111);
            Unwrap(0, 0, 8, 8, 8, skin);
            Fill(8, 0, 16, 8, Color.FromRgb(85, 65, 57));
            Fill(0, 8, 32, 2, Color.FromRgb(85, 65, 57));
            Unwrap(16, 16, 8, 12, 4, shirt);
            Unwrap(40, 16, 4, 12, 4, skin); Unwrap(32, 48, 4, 12, 4, skin);
            Fill(40, 20, 16, 4, shirt); Fill(32, 52, 16, 4, shirt);
            Unwrap(0, 16, 4, 12, 4, pants); Unwrap(16, 48, 4, 12, 4, pants);
            Fill(9, 12, 2, 1, Colors.Ivory); Fill(13, 12, 2, 1, Colors.Ivory);
            Fill(10, 12, 1, 1, Colors.SlateGray); Fill(13, 12, 1, 1, Colors.SlateGray);
            Fill(11, 14, 2, 1, Color.FromRgb(120, 82, 69));
            var bitmap = BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgra32, null, pixels, size * 4);
            bitmap.Freeze();
            return bitmap;
        }
    }
}
