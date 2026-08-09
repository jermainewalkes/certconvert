using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CertConvert.ViewModels;
using CertConvert.Views;

namespace CertConvert.Gui.Tests;

/// <summary>
/// Small-screen layout audit (1.3 cycle). Deterministic, headless, platform-agnostic:
/// lays out every page at the minimum window (860×580) and asserts no page's content
/// is WIDER than its viewport — i.e. nothing clips horizontally, since the view
/// ScrollViewers are vertical-only. This is the measurement the screenshot harness
/// cannot give objectively. Set CERTCONVERT_AUDIT_DIR to also dump the numbers.
/// </summary>
public class LayoutAuditTests
{
    private static readonly string[] Pages =
        { "Inspect", "Convert", "Chain", "Keys", "Generate", "About" };

    [AvaloniaFact]
    public void Pages_DoNotClipHorizontally_AtMinimumWindow()
    {
        const double w = 860, h = 580;
        var vm = new MainWindowViewModel();
        var window = new MainWindow { DataContext = vm, Width = w, Height = h };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var lines = new List<string> { $"Layout audit @ {w}x{h}  (MinWidth={window.MinWidth} MinHeight={window.MinHeight})" };
        var clipped = new List<string>();

        for (int i = 0; i < Pages.Length; i++)
        {
            vm.SelectedIndex = i;
            Dispatcher.UIThread.RunJobs();

            // The current page is the single CertConvert.Views UserControl in the tree;
            // its own ScrollViewer holds the page content.
            var page = window.GetVisualDescendants().OfType<UserControl>()
                .FirstOrDefault(c => c.GetType().Namespace == "CertConvert.Views");
            var sv = page?.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();

            double viewport = sv?.Viewport.Width ?? double.NaN;
            double content = sv?.Extent.Width ?? double.NaN;
            double overflow = content - viewport;
            lines.Add($"  {Pages[i],-9} viewport={viewport,6:F0}  content={content,6:F0}  h-overflow={overflow,6:F1}");
            if (overflow > 1.0)
                clipped.Add($"{Pages[i]} (+{overflow:F0}px)");
        }

        var dir = Environment.GetEnvironmentVariable("CERTCONVERT_AUDIT_DIR");
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "layout-860x580.txt"), string.Join("\n", lines) + "\n");
        }

        Assert.True(clipped.Count == 0,
            "Horizontal clipping at 860x580 (content wider than viewport): " + string.Join(", ", clipped)
            + "\n" + string.Join("\n", lines));
    }
}
