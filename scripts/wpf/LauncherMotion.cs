using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace EBuild.Native
{
    public partial class Launcher
    {
        bool MotionEnabled => settings.S("motion") == "on" ||
            settings.S("motion") != "off" && SystemParameters.ClientAreaAnimation;

        void MoveNavigationMark(bool animate) {
            if (initializing || Navigation.ActualWidth <= 0) return;
            double from = NavigationMarkOffset.X;
            double to = Navigation.ActualWidth / nav.Count * (section + .5) - NavigationMark.Width / 2;
            double travel = to - from;
            bool slide = animate && MotionEnabled && NavigationMark.Visibility == Visibility.Visible && Math.Abs(travel) > .5;
            double peak = to + Math.Sign(travel) * Math.Min(9, Math.Abs(travel) * .2);
            NavigationMark.Visibility = Visibility.Visible;
            NavigationMarkOffset.X = to;
            NavigationMarkOffset.BeginAnimation(TranslateTransform.XProperty, slide
                ? Rebound(from, peak, to, 155, 115)
                : null);
        }

        static DoubleAnimationUsingKeyFrames Rebound(double from, double peak, double rest, int peakMs, int settleMs, int delayMs = 0) {
            var motion = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
            motion.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, TimeSpan.Zero));
            if (delayMs > 0) motion.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, TimeSpan.FromMilliseconds(delayMs)));
            motion.KeyFrames.Add(new EasingDoubleKeyFrame(peak, TimeSpan.FromMilliseconds(delayMs + peakMs),
                new CubicEase { EasingMode = EasingMode.EaseOut }));
            motion.KeyFrames.Add(new EasingDoubleKeyFrame(rest, TimeSpan.FromMilliseconds(delayMs + peakMs + settleMs),
                new SineEase { EasingMode = EasingMode.EaseInOut }));
            return motion;
        }

        void AnimateEntrance(UIElement element, double x, double y, int delayMs = 0) {
            if (!MotionEnabled) return;
            var offset = new TranslateTransform();
            element.RenderTransform = offset;
            double from = x == 0 ? y : x;
            double peak = -Math.Sign(from) * Math.Min(4, Math.Abs(from) * .25);
            offset.BeginAnimation(x == 0 ? TranslateTransform.YProperty : TranslateTransform.XProperty,
                Rebound(from, peak, 0, 136, 100, delayMs));
            element.BeginAnimation(OpacityProperty, new DoubleAnimation(.88, 1, TimeSpan.FromMilliseconds(150)) {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop
            });
        }

        public static readonly DependencyProperty IsSurfacePressedProperty = DependencyProperty.RegisterAttached(
            "IsSurfacePressed", typeof(bool), typeof(Launcher), new PropertyMetadata(false, OnSurfacePressed));

        public static bool GetIsSurfacePressed(DependencyObject element) => (bool)element.GetValue(IsSurfacePressedProperty);
        public static void SetIsSurfacePressed(DependencyObject element, bool value) => element.SetValue(IsSurfacePressedProperty, value);

        static void OnSurfacePressed(DependencyObject element, DependencyPropertyChangedEventArgs e) {
            if (!(element is FrameworkElement surface)) return;
            var owner = GetWindow(surface);
            while (owner != null && !(owner is Launcher)) owner = owner.Owner;
            if (!(owner is Launcher host) || !host.MotionEnabled) {
                surface.ClearValue(RenderTransformProperty);
                return;
            }

            bool down = (bool)e.NewValue;
            var scale = surface.RenderTransform as ScaleTransform;
            if (scale == null) {
                if (!down) return;
                scale = new ScaleTransform(1, 1);
                surface.RenderTransform = scale;
            }
            double influence = Math.Min(1, 160 / Math.Max(1, surface.ActualWidth));
            double from = scale.ScaleX;
            double to = down ? 1 - .052 * influence : 1;
            scale.ScaleX = scale.ScaleY = to;
            DoubleAnimationBase motion = down
                ? (DoubleAnimationBase)new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(65)) {
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop
                }
                : Rebound(from, 1 + .038 * influence, 1, 94, 110);
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, motion);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, motion);
        }
    }
}
