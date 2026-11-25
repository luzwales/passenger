using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.App.Run;
using Passenger.Core;
using Passenger.Workspace;

namespace Passenger.Tests;

/// <summary>Tests for dotnetup discovery, terminal dotnet isolation, and RunConfiguration .NET environment selection.</summary>
public sealed class DotnetEnvironmentTests : IDisposable
{
    private readonly string folder = PathNames.Canonical(TestFolders.Create("dotnet-env-"));

    public void Dispose() => TestFolders.Delete(folder);

    [Fact]
    public void Terminal_dotnet_does_not_inject_dotnetup_when_no_global_json_asks_for_it()
    {
        string? dotnetupRoot = Dotnet.DotnetupRoot();
        Assert.SkipWhen(dotnetupRoot is null, "dotnetup is not installed on this machine");

        Assert.True(Dotnet.IsDotnetupRoot(dotnetupRoot));
        (string? apart, bool clearDotnetup) = Dotnet.TerminalDotnet(folder);
        Assert.False(Dotnet.IsDotnetupRoot(apart));
        _ = clearDotnetup;
    }

    [Fact]
    public void Run_configuration_persists_and_diffs_dotnet_root()
    {
        string customRoot = Path.Combine(folder, "custom-dotnet");
        RunConfiguration source = new() { Name = "App", Project = Path.Combine(folder, "App.csproj") };
        RunConfiguration edited = source with { DotnetRoot = customRoot };

        JsonObject? diff = ConfigurationEdit.Between(source, edited);
        Assert.NotNull(diff);
        Assert.Equal(customRoot, diff!["dotnetRoot"]?.GetValue<string>());

        RunConfiguration restored = ConfigurationEdit.Over(source, diff);
        Assert.Equal(customRoot, restored.DotnetRoot);
    }

    [AvaloniaFact]
    public void Run_configurations_dialog_shows_dotnet_environment_selector()
    {
        string csproj = Path.Combine(folder, "App", "App.csproj");
        Directory.CreateDirectory(Path.GetDirectoryName(csproj)!);
        File.WriteAllText(csproj, TestProjects.Console());
        RunConfiguration config = new() { Name = "App", Project = csproj };
        RunConfigurationsDialog dialog = new(
            [config],
            [],
            [new ProjectModel(csproj, "App", "Exe", ["net10.0"], [], [], true)],
            "App",
            (_, _, _) => { })
        {
            Folder = folder,
        };
        dialog.Show();
        try
        {
            Assert.True(dialog.DotnetEnvironmentBox.IsVisible);
            Assert.NotNull(dialog.DotnetEnvironmentBox.ItemsSource);
            List<ComboBoxItem> items = [.. dialog.DotnetEnvironmentBox.ItemsSource!.Cast<ComboBoxItem>()];
            Assert.True(items.Count >= 1);
            Assert.Equal("Auto (folder default)", items[0].Content?.ToString());
            if (items.Count > 1)
            {
                dialog.DotnetEnvironmentBox.SelectedIndex = 1;
                Assert.Equal(items[1].Tag as string, dialog.Chosen?.DotnetRoot);
            }
        }
        finally
        {
            dialog.Close();
        }
    }
}
