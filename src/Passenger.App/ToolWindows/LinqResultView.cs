using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform;
using Passenger.App.Editors;
using Passenger.App.Linq;
using Passenger.Core;

namespace Passenger.App.ToolWindows;

/// <summary>The <c>LINQ Result</c> tool window: renders <c>.Dump()</c> nested collapsible HTML tables and SQL logs
/// inside a system <see cref="NativeWebView"/> (with <see cref="WebView2Pages"/> on Windows) plus Run/Stop/Clear
/// toolbar buttons, parallel to <see cref="RunPane"/> and <see cref="TerminalPane"/>.</summary>
public sealed class LinqResultView : DockPanel
{
    private readonly List<LinqDumpPacket> packets = [];
    private readonly Button runButton;
    private readonly Button debugButton;
    private readonly Button stopButton;
    private readonly Button clearButton;
    private readonly EmptyState empty = new();
    private readonly Panel body = new();

    private NativeWebView? webView;
    private WebView2Pages? served;
    private string? statusText;
    private bool running;
    private bool isDark = true;
    private int renderQueued;

    public LinqResultView()
    {
        runButton = ToolButtons.Of(IconKeys.Run, "Run .linq Query", () => RunClicked?.Invoke(this, EventArgs.Empty));
        debugButton = ToolButtons.Of(IconKeys.Debug, "Debug .linq Query", () => DebugClicked?.Invoke(this, EventArgs.Empty));
        stopButton = ToolButtons.Of(IconKeys.Stop, "Stop .linq Query", () => StopClicked?.Invoke(this, EventArgs.Empty));
        stopButton.IsEnabled = false;
        clearButton = ToolButtons.Of(IconKeys.Clear, "Clear Results", Clear);

        StackPanel toolbar = new()
        {
            Orientation = Orientation.Vertical,
            Spacing = Gaps.Hair,
            Margin = new Thickness(Gaps.Hair),
            Children = { runButton, debugButton, stopButton, clearButton },
        };
        Border rail = new()
        {
            Child = toolbar,
            [DockProperty] = Dock.Left,
        };
        rail.Bind(Border.BorderBrushProperty, rail.GetResourceObservable(TokenKeys.FieldBorder));
        rail.BorderThickness = new Thickness(0, 0, 1, 0);

        empty.Text = HtmlPreview.WhyNone() ?? "Run a .linq query to view .Dump() results.";
        empty.Margin = new Thickness(16);
        body.Children.Add(empty);

        Children.Add(rail);
        Children.Add(body);
    }

    public event EventHandler? RunClicked;
    public event EventHandler? DebugClicked;
    public event EventHandler? StopClicked;

    public IReadOnlyList<LinqDumpPacket> Packets => packets;

    public void SetDark(bool dark)
    {
        if (isDark == dark)
        {
            return;
        }
        isDark = dark;
        QueueRender();
    }

    public void SetRunning(bool active, string? status = null)
    {
        running = active;
        runButton.IsEnabled = !active;
        debugButton.IsEnabled = !active;
        stopButton.IsEnabled = active;
        if (status is not null)
        {
            statusText = status;
        }
        QueueRender();
    }

    public void Clear()
    {
        packets.Clear();
        statusText = null;
        QueueRender();
    }

    public void AppendPacket(LinqDumpPacket packet)
    {
        packets.Add(packet);
        empty.IsVisible = false;
        QueueRender();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (!PassengerApp.ShowsNoWindow && HtmlPreview.WhyNone() is null)
        {
            EnsureWebView();
            QueueRender();
        }
    }

    private void EnsureWebView()
    {
        if (webView is not null)
        {
            return;
        }
        NativeWebView view = new();
        view.AdapterCreated += OnAdapterCreated;
        view.AdapterDestroyed += OnAdapterDestroyed;
        webView = view;
        body.Children.Clear();
        body.Children.Add(view);
    }

    private void OnAdapterCreated(object? sender, WebViewAdapterEventArgs e)
    {
        if (!OperatingSystem.IsWindows())
        {
            RenderNow();
            return;
        }
        served?.Dispose();
        served = e.TryGetPlatformHandle() is IWindowsWebView2PlatformHandle handle ? WebView2Pages.Over(handle) : null;
        RenderNow();
    }

    private void OnAdapterDestroyed(object? sender, WebViewAdapterEventArgs e)
    {
        if (OperatingSystem.IsWindows())
        {
            served?.Dispose();
            served = null;
        }
    }

    private void QueueRender()
    {
        if (Interlocked.Exchange(ref renderQueued, 1) != 0)
        {
            return;
        }
        UiThread.Post(() =>
        {
            Interlocked.Exchange(ref renderQueued, 0);
            RenderNow();
        });
    }

    private void RenderNow()
    {
        string html = LinqResultHtml.BuildPage(packets, statusText ?? (running ? "Running…" : null), isDark);
        if (PassengerApp.ShowsNoWindow || HtmlPreview.WhyNone() is not null || webView is null)
        {
            empty.Text = HtmlPreview.WhyNone() ?? (packets.Count == 0
                ? (statusText ?? "Run a .linq query to view .Dump() results.")
                : $"{packets.Count} dump result(s) captured.");
            empty.IsVisible = true;
            return;
        }

        string virtualPath = Path.Combine(UserTemporary.Folder(), LinqResultHtml.VirtualFileName);
        if (!OperatingSystem.IsWindows())
        {
            webView.NavigateToString(html, new Uri(UserTemporary.Folder()));
        }
        else if (served is { } pages)
        {
            ServedPage page = new(virtualPath, html, []);
            pages.Serve(page);
            webView.Navigate(page.Address);
        }
        else if (webView.TryGetPlatformHandle() is not null)
        {
            webView.NavigateToString(html, new Uri(UserTemporary.Folder()));
        }
    }
}
