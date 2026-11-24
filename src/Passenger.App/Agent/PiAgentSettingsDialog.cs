using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Passenger.Agent;
using Passenger.App.Settings;
using Passenger.App.ToolWindows;

namespace Passenger.App.Agent;

/// <summary>Configures Pi Agent model profiles, protocol switching (Anthropic Messages, OpenAI Responses,
/// OpenAI Compatible), API credentials, sampling parameters, and runs live connection probes.</summary>
public sealed class PiAgentSettingsDialog : IdeWindow
{
    private readonly List<AgentModelProfile> models;
    private readonly ListBox profileList = new() { Classes = { StyleClasses.Tree } };
    private readonly TextBox nameBox = new() { PlaceholderText = "Display name" };
    private readonly FormDropDown providerBox = new();
    private readonly FormDropDown protocolBox = new();
    private readonly TextBox baseUrlBox = new() { PlaceholderText = "https://api.openai.com/v1" };
    private readonly TextBox modelBox = new() { PlaceholderText = "gpt-4.1 / claude-sonnet-4-5 / deepseek-chat" };
    private readonly TextBox apiKeyBox = new() { PasswordChar = '•', PlaceholderText = "sk-…" };
    private readonly TextBox temperatureBox = new() { PlaceholderText = "0.2" };
    private readonly TextBox maxTokensBox = new() { PlaceholderText = "8192" };
    private readonly Button testButton = new() { Content = "Test Connection" };
    private readonly Button duplicateButton = new() { Content = "Duplicate" };
    private readonly Button removeButton = new() { Content = "Remove" };
    private readonly TextBlock statusText = new() { TextWrapping = TextWrapping.Wrap, Classes = { StyleClasses.Muted } };
    private readonly PiAgentRuntime runtime;
    private string? activeId;
    private int selectedIndex = -1;
    private bool loadingFields;
    private CancellationTokenSource? probing;

    public PiAgentSettingsDialog(PiAgentConfig config, PiAgentRuntime runtime)
    {
        FormRows.Scope(this);
        this.runtime = runtime;
        PiAgentConfig clean = config.Clamped();
        models = [.. clean.Models];
        if (models.Count == 0)
        {
            models.Add(AgentModelProfile.Create(AgentProviderKind.OpenAI));
        }
        activeId = clean.ActiveModelId ?? models[0].Id;

        Title = "Pi Agent Settings";
        Width = DialogWidths.Wide;
        Height = 560;
        MinWidth = 600;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        AutomationProperties.SetName(profileList, "Model Profiles");
        AutomationProperties.SetName(testButton, "Test Connection");
        AutomationProperties.SetName(duplicateButton, "Duplicate Profile");
        AutomationProperties.SetName(removeButton, "Remove Profile");

        // Align dropdowns inside the form column to a uniform width (DialogButtonsTests).
        providerBox.HorizontalAlignment = HorizontalAlignment.Stretch;
        protocolBox.HorizontalAlignment = HorizontalAlignment.Stretch;

        providerBox.ItemsSource = AgentProviderPreset.All;
        protocolBox.ItemsSource = AgentProtocolCatalog.All;

        ReusedRows.Use(providerBox, () =>
        {
            TextBlock text = new() { VerticalAlignment = VerticalAlignment.Center };
            return text.Showing<AgentProviderPreset>(p => text.Text = p.Name);
        });
        ReusedRows.Use(protocolBox, () =>
        {
            TextBlock text = new() { VerticalAlignment = VerticalAlignment.Center };
            return text.Showing<AgentProtocolOption>(p => text.Text = p.Name);
        });
        ReusedRows.Use(profileList, () =>
        {
            TextBlock title = new() { Classes = { StyleClasses.Strong }, TextTrimming = TextTrimming.CharacterEllipsis };
            TextBlock sub = new() { Classes = { StyleClasses.Muted, StyleClasses.Caption }, TextTrimming = TextTrimming.CharacterEllipsis };
            return new StackPanel
            {
                Spacing = Gaps.Hair,
                Margin = new Thickness(Gaps.Small, Gaps.Tight),
                Children = { title, sub },
            }.Showing<AgentModelProfile>(profile =>
            {
                title.Text = profile.DisplayName;
                sub.Text = $"{AgentProtocolCatalog.ShortLabel(profile.Protocol)} · {profile.Model}";
            });
        });

        temperatureBox.Text = clean.Temperature.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
        maxTokensBox.Text = clean.MaxTokens.ToString(System.Globalization.CultureInfo.InvariantCulture);

        Button addButton = new() { Content = "Add" };
        AutomationProperties.SetName(addButton, "Add Profile");
        addButton.Click += (_, _) => AddProfile();
        duplicateButton.Click += (_, _) => DuplicateProfile();
        removeButton.Click += (_, _) => RemoveProfile();
        testButton.Click += (_, _) => IdeErrors.Observe(ProbeSelectedAsync());

        profileList.SelectionChanged += (_, _) =>
        {
            if (profileList.SelectedIndex >= 0 && profileList.SelectedIndex < models.Count)
            {
                LoadProfile(profileList.SelectedIndex);
            }
        };

        nameBox.TextChanged += (_, _) => CommitFieldEdits();
        baseUrlBox.TextChanged += (_, _) => CommitFieldEdits();
        modelBox.TextChanged += (_, _) => CommitFieldEdits();
        apiKeyBox.TextChanged += (_, _) => CommitFieldEdits();
        providerBox.SelectionChanged += (_, _) => ApplyProviderPreset();
        protocolBox.SelectionChanged += (_, _) => CommitFieldEdits();

        StackPanel listActions = new()
        {
            Orientation = Orientation.Horizontal,
            Spacing = Gaps.Small,
            Children = { addButton, duplicateButton, removeButton },
        };
        Border listFrame = new() { Classes = { StyleClasses.Framed }, Child = profileList };
        DockPanel leftPane = new() { LastChildFill = true };
        DockPanel.SetDock(listActions, Dock.Bottom);
        listActions.Margin = new Thickness(0, Gaps.Normal, 0, 0);
        leftPane.Children.Add(listActions);
        leftPane.Children.Add(listFrame);

        StackPanel fields = new()
        {
            Spacing = Gaps.Normal,
            Children =
            {
                FormRows.Of("Name", nameBox),
                FormRows.Of("Provider", providerBox),
                FormRows.Of("Protocol", protocolBox),
                FormRows.Of("Base URL", baseUrlBox),
                FormRows.Of("Model", modelBox),
                FormRows.Of("API Key", apiKeyBox),
                new TextBlock
                {
                    Text = "Sampling (applied across sessions)",
                    Classes = { StyleClasses.PaneTitle },
                    Margin = new Thickness(0, Gaps.Normal, 0, 0),
                },
                FormRows.Of("Temperature", temperatureBox),
                FormRows.Of("Max Tokens", maxTokensBox),
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = Gaps.Normal,
                    Margin = new Thickness(0, Gaps.Tight, 0, 0),
                    Children = { testButton, statusText },
                },
            },
        };

        ScrollViewer rightScroll = new() { Content = fields };
        PaneSplit split = new(leftPane, rightScroll, "230,1,*");

        Button ok = DialogFooter.Default("Save");
        ok.Click += (_, _) => Accept();
        Content = DialogFooter.Page(split, DialogFooter.OkCancel(ok, () => Close(false)));

        Closed += (_, _) =>
        {
            probing?.Cancel();
            probing?.Dispose();
            probing = null;
        };

        int initial = models.FindIndex(m => m.Id == activeId);
        RefreshList(initial >= 0 ? initial : 0);
        Opened += (_, _) => Dispatcher.UIThread.Post(() => modelBox.Focus(), DispatcherPriority.Loaded);
    }

    public PiAgentConfig? Result { get; private set; }

    private void RefreshList(int select)
    {
        int clamped = Math.Clamp(select, 0, Math.Max(0, models.Count - 1));
        profileList.ItemsSource = models.ToList();
        if (models.Count > 0)
        {
            profileList.SelectedIndex = clamped;
            LoadProfile(clamped);
        }
        removeButton.IsEnabled = models.Count > 1;
        duplicateButton.IsEnabled = models.Count > 0;
    }

    private void LoadProfile(int index)
    {
        if (index < 0 || index >= models.Count)
        {
            return;
        }
        loadingFields = true;
        try
        {
            selectedIndex = index;
            AgentModelProfile profile = models[index];
            activeId = profile.Id;
            nameBox.Text = profile.Name;
            providerBox.SelectedItem = AgentProviderPreset.For(profile.Provider);
            protocolBox.SelectedItem = AgentProtocolCatalog.For(profile.Protocol);
            baseUrlBox.Text = profile.BaseUrl;
            modelBox.Text = profile.Model;
            apiKeyBox.Text = profile.ApiKey;
        }
        finally
        {
            loadingFields = false;
        }
    }

    private void ApplyProviderPreset()
    {
        if (loadingFields || selectedIndex < 0 || selectedIndex >= models.Count || providerBox.SelectedItem is not AgentProviderPreset preset)
        {
            return;
        }
        loadingFields = true;
        try
        {
            AgentModelProfile current = models[selectedIndex];
            AgentModelProfile updated = (current with
            {
                Provider = preset.Kind,
                Protocol = preset.Protocol,
                BaseUrl = preset.BaseUrl,
                Model = string.IsNullOrWhiteSpace(preset.Model) ? current.Model : preset.Model,
                Name = preset.Name,
            }).Clamped();
            models[selectedIndex] = updated;
            nameBox.Text = updated.Name;
            protocolBox.SelectedItem = AgentProtocolCatalog.For(updated.Protocol);
            baseUrlBox.Text = updated.BaseUrl;
            modelBox.Text = updated.Model;
            profileList.ItemsSource = models.ToList();
            profileList.SelectedIndex = selectedIndex;
        }
        finally
        {
            loadingFields = false;
        }
    }

    private void CommitFieldEdits()
    {
        if (loadingFields || selectedIndex < 0 || selectedIndex >= models.Count)
        {
            return;
        }
        AgentProviderKind provider = providerBox.SelectedItem is AgentProviderPreset p ? p.Kind : models[selectedIndex].Provider;
        AgentProtocol protocol = protocolBox.SelectedItem is AgentProtocolOption opt ? opt.Kind : models[selectedIndex].Protocol;
        models[selectedIndex] = (models[selectedIndex] with
        {
            Name = nameBox.Text ?? string.Empty,
            Provider = provider,
            Protocol = protocol,
            BaseUrl = baseUrlBox.Text ?? string.Empty,
            Model = modelBox.Text ?? string.Empty,
            ApiKey = apiKeyBox.Text ?? string.Empty,
        }).Clamped();
        profileList.ItemsSource = models.ToList();
        profileList.SelectedIndex = selectedIndex;
    }

    private void AddProfile()
    {
        AgentModelProfile added = AgentModelProfile.Create(AgentProviderKind.OpenAI) with
        {
            Id = AgentModelProfile.NewId(),
            Name = $"Profile {models.Count + 1}",
        };
        models.Add(added);
        RefreshList(models.Count - 1);
    }

    private void DuplicateProfile()
    {
        if (selectedIndex < 0 || selectedIndex >= models.Count)
        {
            return;
        }
        AgentModelProfile copy = models[selectedIndex] with
        {
            Id = AgentModelProfile.NewId(),
            Name = models[selectedIndex].DisplayName + " Copy",
        };
        models.Add(copy);
        RefreshList(models.Count - 1);
    }

    private void RemoveProfile()
    {
        if (models.Count <= 1 || selectedIndex < 0 || selectedIndex >= models.Count)
        {
            return;
        }
        models.RemoveAt(selectedIndex);
        RefreshList(Math.Min(selectedIndex, models.Count - 1));
    }

    private async Task ProbeSelectedAsync()
    {
        if (selectedIndex < 0 || selectedIndex >= models.Count)
        {
            return;
        }
        probing?.Cancel();
        probing?.Dispose();
        probing = new CancellationTokenSource();
        CancellationToken cancel = probing.Token;

        testButton.IsEnabled = false;
        statusText.Classes.Set(StyleClasses.Error, false);
        statusText.Classes.Set(StyleClasses.Muted, true);
        statusText.Text = "Probing endpoint…";
        try
        {
            ConnectionTestResult probe = await runtime.TestConnectionAsync(models[selectedIndex], cancel);
            if (cancel.IsCancellationRequested)
            {
                return;
            }
            statusText.Classes.Set(StyleClasses.Error, !probe.Ok);
            statusText.Classes.Set(StyleClasses.Muted, probe.Ok);
            statusText.Text = probe.Summary;
        }
        catch (OperationCanceledException)
        {
            // Cancelled when dialog closes or user restarts probe.
        }
        finally
        {
            testButton.IsEnabled = true;
        }
    }

    private void Accept()
    {
        CommitFieldEdits();
        double temperature = double.TryParse(temperatureBox.Text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double t)
            ? t
            : 0.2;
        int maxTokens = int.TryParse(maxTokensBox.Text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int m)
            ? m
            : 8192;
        Result = new PiAgentConfig
        {
            Models = [.. models],
            ActiveModelId = selectedIndex >= 0 && selectedIndex < models.Count ? models[selectedIndex].Id : activeId,
            Temperature = temperature,
            MaxTokens = maxTokens,
        }.Clamped();
        Close(true);
    }
}
