using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace EBuild.Native
{
    public partial class Launcher
    {
        ContentControl homeCard;

        Button launchButton;
        TextBlock launchState;
        Border launchStateBadge;
        Button stopButton;
        Button threadsButton;
        Button downloadsButton;
        TextBlock homeIdentity;
        Image homeAvatar;

        Button currentThread;

        bool ShowingGameCard => route == "home" || route == "instances";

        void ShowHome() {
            threadsButton = QuietAction("游戏线程", ShowThreads);
            downloadsButton = QuietAction("下载任务", () => ShowDownloadTasks(() => {
                ShowHome();
                return Task.CompletedTask;
            }, HomeSection));
            var tools = Actions(threadsButton, downloadsButton);
            tools.Margin = new Thickness(0, 0, 0, 6);
            var page = ShowPage("home", HomeSection, "首页", headerActions: tools);
            page.MaxWidth = 1120;
            var header = (FrameworkElement)page.Children[0];
            var hero = new ContentControl {
                HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch
            };
            void FitHomepage() {
                var height = PageScroll.ViewportHeight - Page.Margin.Top - Page.Margin.Bottom - header.ActualHeight;
                hero.MinHeight = Math.Max(0, Math.Floor(height));
            }
            ScrollChangedEventHandler fitViewport = (_, e) => {
                if (e.Source == PageScroll && e.ViewportHeightChange != 0) FitHomepage();
            };
            hero.Loaded += (_, __) => { PageScroll.ScrollChanged += fitViewport; FitHomepage(); };
            hero.Unloaded += (_, __) => PageScroll.ScrollChanged -= fitViewport;
            header.SizeChanged += (_, __) => FitHomepage();
            homeCard = hero;
            page.Children.Add(homeCard);
            instanceRows.Clear();
            UpdateHomeCard();
        }

        Button HomeAccountButton() {
            homeAvatar = new Image { Width = 24, Height = 24, Margin = new Thickness(0, 0, 8, 0), SnapsToDevicePixels = true };
            RenderOptions.SetBitmapScalingMode(homeAvatar, BitmapScalingMode.NearestNeighbor);
            homeIdentity = InstanceText("", 12);
            homeIdentity.VerticalAlignment = VerticalAlignment.Center;
            var identity = QuietAction("", () => Navigate(AccountsSection));
            identity.Padding = new Thickness(2, 3, 6, 3);
            identity.MinHeight = 32;
            identity.Margin = new Thickness(-3, 4, 0, 0);
            identity.HorizontalAlignment = HorizontalAlignment.Left;
            identity.MaxWidth = 200;
            var person = new DockPanel();
            DockPanel.SetDock(homeAvatar, Dock.Left);
            person.Children.Add(homeAvatar);
            person.Children.Add(homeIdentity);
            identity.Content = person;
            AutomationProperties.SetName(identity, "玩家账号");
            var accounts = CreateActionMenu();
            AddMenu(accounts, "管理名称", () => ManageAppearance("name"));
            AddMenu(accounts, "管理皮肤", () => ManageAppearance("skin"));
            AddMenu(accounts, "管理披风", () => ManageAppearance("cape"));
            identity.ContextMenu = accounts;
            UpdateAccountIdentity();
            return identity;
        }

        void UpdateHomeCard(bool animate = false) {
            if (!ShowingGameCard || homeCard == null) return;
            bool hero = route == "home";
            UpdateInstanceHighlights();
            launchButton = null;
            launchState = null;
            launchStateBadge = null;
            stopButton = null;
            var active = Active;
            string id = active.S("id");
            var identity = new DockPanel { VerticalAlignment = hero ? VerticalAlignment.Bottom : VerticalAlignment.Center };
            var icon = LoaderIcon(active.S("loader"), hero ? 86 : 38);
            if (!hero) {
                icon.Margin = new Thickness(0, 0, 12, 0);
                icon.VerticalAlignment = VerticalAlignment.Center;
                DockPanel.SetDock(icon, Dock.Left);
                identity.Children.Add(icon);
            }
            string title = active == null ? "还没有游戏实例" : active.S("name");
            var name = InstanceText(title, hero ? 29 : 21, FontWeights.SemiBold);
            name.ToolTip = title;
            var caption = InstanceText(active == null ? "安装 Minecraft，或拖入游戏文件夹、整合包。" : InstanceVersion(active), 12);
            UseResource(caption, TextBlock.ForegroundProperty, "Muted");
            caption.Margin = new Thickness(0, 2, 0, 0);
            var metadata = hero && active != null ? Stack(HomeInstanceButton(name, caption)) : Stack(name);
            if (!hero) metadata.Children.Add(caption);
            metadata.VerticalAlignment = VerticalAlignment.Center;
            if (active.B("externalMissing")) {
                var missing = InstanceText("找不到游戏文件夹", 12);
                UseResource(missing, TextBlock.ForegroundProperty, "Danger");
                missing.Margin = new Thickness(0, 4, 0, 0);
                metadata.Children.Add(missing);
            }
            metadata.Children.Add(HomeAccountButton());
            identity.Children.Add(metadata);

            var controls = Stack();
            if (active == null) {
                var create = hero ? HomeLaunchAction(icon, "添加游戏", caption.Text, () => ShowInstall(""), out _)
                    : InstancePrimary("新建实例", () => ShowInstall(""));
                controls.Children.Add(create);
            } else {
                var launch = hero ? HomeLaunchAction(icon, "开始游戏", "", () => StartGame(id), out launchButton)
                    : (launchButton = InstancePrimary("启动游戏", () => StartGame(id)));
                launchState = InstanceText("", 12);
                AutomationProperties.SetLiveSetting(launchState, AutomationLiveSetting.Polite);
                launchState.MaxWidth = 164;
                UseResource(launchState, TextBlock.ForegroundProperty, "Ink");
                launchStateBadge = new Border { Child = launchState, Padding = new Thickness(9, 5, 9, 5),
                    CornerRadius = new CornerRadius(5), VerticalAlignment = VerticalAlignment.Center };
                UseResource(launchStateBadge, Border.BackgroundProperty, "Canvas");
                stopButton = Button("停止", () => StopGame(id));
                stopButton.Margin = new Thickness(7, 0, 0, 0);
                UseResource(stopButton, Control.ForegroundProperty, "Danger");
                var commands = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
                commands.Children.Add(launch);
                commands.Children.Add(launchStateBadge);
                commands.Children.Add(stopButton);
                controls.Children.Add(commands);
                var secondary = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 2, 0, 0) };
                currentThread = QuietAction("查看详情", ShowThreads);
                secondary.Children.Add(currentThread);
                if (active.B("externalMissing")) secondary.Children.Add(QuietAction("重新定位", () => Relocate(active)));
                var settingsButton = QuietAction("实例设置", () => { ShowInstanceSettings(active); return Task.CompletedTask; });
                settingsButton.Margin = new Thickness(0);
                if (!hero) settingsButton.MinWidth = launchButton.MinWidth;
                secondary.Children.Add(settingsButton);
                controls.Children.Add(secondary);
            }
            controls.Margin = new Thickness(14, 0, 0, 0);
            controls.MaxWidth = hero ? 400 : 310;
            controls.VerticalAlignment = VerticalAlignment.Center;

            var layout = new Grid();
            layout.ColumnDefinitions.Add(new ColumnDefinition());
            layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(controls, 1);
            layout.Children.Add(identity);
            layout.Children.Add(controls);
            if (hero) {
                var surface = new Grid { MinHeight = 242, Background = Brushes.Transparent };
                surface.Children.Add(HomeCurveTexture(out var glow));
                surface.PreviewMouseLeftButtonDown += (_, __) => {
                    if (MotionEnabled) PlayHomeCurveFlow(glow);
                };
                layout.Margin = new Thickness(9, 21, 9, 14);
                layout.VerticalAlignment = VerticalAlignment.Bottom;
                surface.Children.Add(layout);
                homeCard.Content = surface;
            } else {
                var card = Card(layout);
                card.Padding = new Thickness(13, 9, 13, 8);
                card.Margin = new Thickness(0);
                homeCard.Content = card;
            }
            if (animate) AnimateEntrance(layout, 0, 10);
            UpdateGameControls();
            RenderDownloads();
        }

        FrameworkElement HomeInstanceButton(TextBlock name, TextBlock caption) {
            UseResource(name, TextBlock.ForegroundProperty, "Ink");
            var chevron = new System.Windows.Shapes.Path { Data = Geometry.Parse("M 1,5 L 5,1 L 9,5"),
                StrokeThickness = 1.35, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            UseResource(chevron, System.Windows.Shapes.Shape.StrokeProperty, "Muted");
            var title = new DockPanel();
            DockPanel.SetDock(chevron, Dock.Right);
            title.Children.Add(chevron);
            title.Children.Add(name);
            var popup = new Popup { Placement = PlacementMode.Top, VerticalOffset = -6, StaysOpen = false,
                AllowsTransparency = true, PopupAnimation = MotionEnabled ? PopupAnimation.Fade : PopupAnimation.None };
            var choose = QuietAction("", () => {
                if (popup.IsOpen) popup.IsOpen = false;
                else OpenHomeInstanceMenu(popup);
                return Task.CompletedTask;
            });
            choose.Content = Stack(title, caption);
            choose.Padding = new Thickness(3, 1, 6, 2);
            choose.Margin = new Thickness(-3, 0, 0, 0);
            choose.HorizontalAlignment = HorizontalAlignment.Left;
            choose.HorizontalContentAlignment = HorizontalAlignment.Left;
            choose.ToolTip = "切换首页实例";
            AutomationProperties.SetName(choose, "切换首页实例 · " + name.Text);
            AutomationProperties.SetAutomationId(choose, "HomeInstanceSwitcher");
            popup.PlacementTarget = choose;
            popup.Closed += (_, __) => popup.Child = null;
            choose.Unloaded += (_, __) => popup.IsOpen = false;
            var host = new Grid { HorizontalAlignment = HorizontalAlignment.Left };
            host.Children.Add(choose);
            host.Children.Add(popup);
            return host;
        }

        void OpenHomeInstanceMenu(Popup popup) {
            var search = CatalogInput("搜索实例名称、版本或加载器", max: 80);
            search.Margin = new Thickness(0, 0, 0, 8);
            var rows = Stack();
            var empty = Note("没有匹配的实例");
            empty.Margin = new Thickness(6);
            empty.Visibility = Visibility.Collapsed;
            var scroll = new ScrollViewer { Content = Stack(rows, empty), MaxHeight = Math.Min(246, Math.Max(130, ActualHeight - 260)),
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            var panel = new Border { Child = Stack(search, scroll), Width = 392, Padding = new Thickness(8),
                CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1) };
            UseResource(panel, Border.BackgroundProperty, "Card");
            UseResource(panel, Border.BorderBrushProperty, "Line");
            UseResource(panel, TextElement.ForegroundProperty, "Ink");
            AutomationProperties.SetName(panel, "切换首页实例");
            var duplicateNumbers = InstanceDuplicateNumbers();
            var choices = new List<(Button Row, string Search)>();
            foreach (var instance in OrderedInstances()) {
                string id = instance.S("id");
                bool current = id == selected;
                string subtitle = instance.S("version") + " · " + Loader(instance.S("loader"));
                if (duplicateNumbers.TryGetValue(id, out int number)) subtitle += " · " + DuplicateInstanceLabel(instance, number);
                if (instance.B("externalMissing")) subtitle += " · 路径丢失";
                var row = QuietAction("", async () => {
                    popup.IsOpen = false;
                    await SelectInstance(id);
                });
                row.Style = (Style)FindResource("InstanceRowButton");
                var icon = LoaderIcon(instance.S("loader"), 24);
                var check = current ? InstanceText("✓", 13, FontWeights.SemiBold) : null;
                if (check != null) UseResource(check, TextBlock.ForegroundProperty, "AccentText");
                row.Content = current ? Row(icon, instance.S("name"), subtitle, check) : Row(icon, instance.S("name"), subtitle);
                row.Padding = new Thickness(9, 5, 9, 3);
                row.Margin = new Thickness(0);
                row.Tag = current;
                AutomationProperties.SetName(row, instance.S("name") + " · " + subtitle + (current ? " · 当前实例" : ""));
                choices.Add((row, InstanceSearchText(instance)));
                rows.Children.Add(row);
            }
            search.TextChanged += (_, __) => {
                var keywords = search.Text.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                bool any = false;
                foreach (var choice in choices) {
                    bool matches = MatchesInstanceSearch(choice.Search, keywords);
                    choice.Row.Visibility = matches ? Visibility.Visible : Visibility.Collapsed;
                    any |= matches;
                }
                empty.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
                scroll.ScrollToTop();
            };
            panel.PreviewKeyDown += (_, e) => {
                if (e.Key != Key.Escape) return;
                e.Handled = true;
                popup.IsOpen = false;
                popup.PlacementTarget.Focus();
            };
            popup.Child = panel;
            popup.IsOpen = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => {
                if (popup.IsOpen && popup.Child == panel) search.Focus();
            }));
        }

        FrameworkElement HomeLaunchAction(Image icon, string label, string hint, Func<Task> action, out Button button) {
            var title = InstanceText(label, 32);
            title.TextAlignment = TextAlignment.Right;
            UseResource(title, TextBlock.ForegroundProperty, "Ink");
            button = QuietAction("", action);
            button.Content = title;
            button.Margin = new Thickness(0);
            button.Padding = new Thickness(2, 0, 2, 0);
            button.HorizontalAlignment = HorizontalAlignment.Right;
            AutomationProperties.SetName(button, label);
            var labels = Stack(button);
            if (hint != "") {
                var description = InstanceText(hint, 13);
                description.TextAlignment = TextAlignment.Right;
                description.IsHitTestVisible = false;
                UseResource(description, TextBlock.ForegroundProperty, "Muted");
                labels.Children.Add(description);
                AutomationProperties.SetHelpText(button, hint);
            }
            labels.VerticalAlignment = VerticalAlignment.Bottom;
            icon.Margin = new Thickness(14, 0, 0, 0);
            icon.IsHitTestVisible = false;
            var content = new DockPanel();
            DockPanel.SetDock(icon, Dock.Right);
            content.Children.Add(icon);
            content.Children.Add(labels);
            content.SetBinding(VisibilityProperty, new Binding(nameof(UIElement.Visibility)) { Source = button });
            return content;
        }

        static readonly Geometry HomeCurves = CreateHomeCurves();

        static Geometry CreateHomeCurves() {
            var pattern = new GeometryGroup();
            pattern.Children.Add(Geometry.Parse(
                "M 8,262 A 405,405 0 0 1 531,-12 M 8,262 A 405,405 0 0 0 531,-12 M 82,280 A 310,310 0 0 1 634,193"));
            pattern.Children.Add(new EllipseGeometry(new Point(334, 170), 74, 74));
            pattern.Children.Add(new EllipseGeometry(new Point(531, 218), 121, 121));
            pattern.Freeze();
            return pattern;
        }

        static Border HomeCurveTexture(out System.Windows.Shapes.Path glow) {
            var curves = new System.Windows.Shapes.Path { Data = HomeCurves, StrokeThickness = 1.5, Opacity = .30 };
            UseResource(curves, System.Windows.Shapes.Shape.StrokeProperty, "AccentText");
            var horizontalFade = new LinearGradientBrush(new GradientStopCollection {
                new GradientStop(Colors.Transparent, 0), new GradientStop(Colors.White, .42),
                new GradientStop(Colors.White, .82), new GradientStop(Colors.Transparent, 1)
            }, new Point(0, .5), new Point(1, .5));
            horizontalFade.Freeze();
            var pattern = new Canvas { Width = 620, Height = 266, OpacityMask = horizontalFade };
            pattern.Children.Add(curves);
            glow = CreateHomeCurveGlow();
            pattern.Children.Add(glow);
            Canvas.SetRight(pattern, 0);
            Canvas.SetTop(pattern, 0);

            var verticalFade = new LinearGradientBrush(new GradientStopCollection {
                new GradientStop(Colors.Transparent, 0), new GradientStop(Colors.White, .15),
                new GradientStop(Colors.White, .83), new GradientStop(Colors.Transparent, 1)
            }, new Point(.5, 0), new Point(.5, 1));
            verticalFade.Freeze();
            var viewport = new Canvas { ClipToBounds = true };
            viewport.Children.Add(pattern);
            return new Border { Background = Brushes.Transparent, Child = viewport, IsHitTestVisible = false, OpacityMask = verticalFade };
        }

        static System.Windows.Shapes.Path CreateHomeCurveGlow() {
            var dashes = new DoubleCollection { 26, 230 };
            dashes.Freeze();
            var flow = new System.Windows.Shapes.Path {
                Data = HomeCurves, StrokeThickness = 2, Opacity = 0,
                StrokeDashArray = dashes, StrokeDashCap = PenLineCap.Round
            };
            UseResource(flow, System.Windows.Shapes.Shape.StrokeProperty, "AccentText");
            flow.Unloaded += (_, __) => {
                flow.BeginAnimation(System.Windows.Shapes.Shape.StrokeDashOffsetProperty, null);
                flow.BeginAnimation(OpacityProperty, null);
            };
            return flow;
        }

        static void PlayHomeCurveFlow(System.Windows.Shapes.Path glow) {
            var duration = TimeSpan.FromMilliseconds(780);
            double start = glow.StrokeDashOffset;
            var travel = new DoubleAnimation(start, start - 220, duration) { FillBehavior = FillBehavior.Stop };
            var light = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
            light.KeyFrames.Add(new LinearDoubleKeyFrame(glow.Opacity, TimeSpan.Zero));
            light.KeyFrames.Add(new LinearDoubleKeyFrame(.64, TimeSpan.FromMilliseconds(80)));
            light.KeyFrames.Add(new LinearDoubleKeyFrame(.64, TimeSpan.FromMilliseconds(470)));
            light.KeyFrames.Add(new EasingDoubleKeyFrame(0, duration, new QuadraticEase { EasingMode = EasingMode.EaseOut }));
            glow.BeginAnimation(System.Windows.Shapes.Shape.StrokeDashOffsetProperty, travel);
            glow.BeginAnimation(OpacityProperty, light);
        }

        Button InstancePrimary(string label, Func<Task> action) {
            var button = Button(label, action, true);
            button.HorizontalAlignment = HorizontalAlignment.Right;
            button.MinWidth = 112;
            button.Margin = new Thickness(0);
            return button;
        }
    }
}
