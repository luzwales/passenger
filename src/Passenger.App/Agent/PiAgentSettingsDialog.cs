using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Passenger.Agent;
using Passenger.App.ToolWindows;

namespace Passenger.App.Agent;

/// <summary>Configures LLM provider endpoints, multiple models per endpoint, per-model thinking level,
/// context window, max tokens, and runs live connection probes.</summary>
public sealed class PiAgentSettingsDialog : IdeWindow
{
    private const double Gutter = Gaps.Page;

    private readonly ObservableCollection<ProviderRow> providerRows = [];
    private readonly ObservableCollection<AgentModelEntry> modelRows = [];
    private readonly List<AgentProviderEndpoint> providers;
    private readonly ListBox providerList = new() { Classes = { StyleClasses.Tree } };
    private readonly ListBox modelList = new() { Classes = { StyleClasses.Tree } };

    private readonly TextBox providerNameBox = new() { PlaceholderText = "Display name (e.g. router)" };
    private readonly FormDropDown presetBox = new();
    private readonly FormDropDown protocolBox = new();
    private readonly TextBox baseUrlBox = new() { PlaceholderText = "https://api.openai.com/v1" };
    private readonly TextBox apiKeyBox = new() { PasswordChar = '•', PlaceholderText = "sk-…" };

    private readonly TextBox modelIdBox = new() { PlaceholderText = "gpt-4.1 / claude-sonnet-4-5 / gemini-4-hydrogen" };
    private readonly TextBox modelNameBox = new() { PlaceholderText = "Optional friendly name" };
    private readonly FormDropDown thinkingBox = new();
    private readonly TextBox maxTokensBox = new() { PlaceholderText = "8192" };
    private readonly TextBox contextWindowBox = new() { PlaceholderText = "128000" };
    private readonly TextBox temperatureBox = new() { PlaceholderText = "0.2" };

    private readonly Button testButton = new() { Content = "Test Connection" };
    private readonly Button duplicateProviderButton = new() { Content = "Duplicate" };
    private readonly Button removeProviderButton = new() { Content = "Remove" };
    private readonly Button addModelButton = new() { Content = "Add Model" };
    private readonly Button removeModelButton = new() { Content = "Remove Model" };
    private readonly TextBlock statusText = new() { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Classes = { StyleClasses.Muted } };
    private readonly PiAgentRuntime runtime;

    private int selectedProviderIndex = -1;
    private int selectedModelIndex = -1;
    private bool loadingFields;
    private CancellationTokenSource? probing;

    public PiAgentSettingsDialog(PiAgentConfig config, PiAgentRuntime runtime)
    {
        FormRows.Scope(this);
        this.runtime = runtime;
        PiAgentConfig clean = config.Clamped();
        providers = [.. clean.Providers];
        if (providers.Count == 0)
        {
            providers.Add(AgentProviderEndpoint.Create(AgentProviderKind.OpenAI));
        }

        Title = "Provider Settings";
        Width = DialogWidths.Workspace;
        Height = 700;
        MinWidth = 680;
        MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        AutomationProperties.SetName(providerList, "Provider Endpoints");
        AutomationProperties.SetName(modelList, "Endpoint Models");
        AutomationProperties.SetName(providerNameBox, "Provider Name");
        AutomationProperties.SetName(presetBox, "Provider Preset");
        AutomationProperties.SetName(protocolBox, "Protocol");
        AutomationProperties.SetName(baseUrlBox, "Base URL");
        AutomationProperties.SetName(apiKeyBox, "API Key");
        AutomationProperties.SetName(modelIdBox, "Model ID");
        AutomationProperties.SetName(modelNameBox, "Model Display Name");
        AutomationProperties.SetName(thinkingBox, "Thinking Level");
        AutomationProperties.SetName(maxTokensBox, "Max Output Tokens");
        AutomationProperties.SetName(contextWindowBox, "Context Window");
        AutomationProperties.SetName(temperatureBox, "Temperature");
        AutomationProperties.SetName(testButton, "Test Connection");
        AutomationProperties.SetName(duplicateProviderButton, "Duplicate Provider");
        AutomationProperties.SetName(removeProviderButton, "Remove Provider");
        AutomationProperties.SetName(addModelButton, "Add Model");
        AutomationProperties.SetName(removeModelButton, "Remove Model");

        presetBox.HorizontalAlignment = HorizontalAlignment.Stretch;
        protocolBox.HorizontalAlignment = HorizontalAlignment.Stretch;
        thinkingBox.HorizontalAlignment = HorizontalAlignment.Stretch;

        presetBox.ItemsSource = AgentProviderPreset.All;
        protocolBox.ItemsSource = AgentProtocolCatalog.All;
        ThinkingChoice[] thinkingChoices = [.. AgentThinkingCatalog.All.Select(l => new ThinkingChoice(l))];
        thinkingBox.ItemsSource = thinkingChoices;

        ReusedRows.Use(presetBox, () =>
        {
            TextBlock text = new() { VerticalAlignment = VerticalAlignment.Center };
            return text.Showing<AgentProviderPreset>(p => text.Text = p.Name);
        });
        ReusedRows.Use(protocolBox, () =>
        {
            TextBlock text = new() { VerticalAlignment = VerticalAlignment.Center };
            return text.Showing<AgentProtocolOption>(p => text.Text = p.Name);
        });
        ReusedRows.Use(thinkingBox, () =>
        {
            TextBlock text = new() { VerticalAlignment = VerticalAlignment.Center };
            return text.Showing<ThinkingChoice>(choice => text.Text = AgentThinkingCatalog.Label(choice.Level));
        });

        providerList.ItemsSource = providerRows;
        ReusedRows.Use(providerList, () =>
        {
            TextBlock title = new() { Classes = { StyleClasses.Strong }, TextTrimming = TextTrimming.CharacterEllipsis };
            TextBlock sub = new() { Classes = { StyleClasses.Muted, StyleClasses.Caption }, TextTrimming = TextTrimming.CharacterEllipsis };
            StackPanel stack = new()
            {
                Spacing = Gaps.Hair,
                Margin = new Thickness(Gaps.Small, Gaps.Tight),
                Children = { title, sub },
            };
            return stack.Showing<ProviderRow>(row =>
            {
                title.Text = row.Title;
                sub.Text = row.Subtitle;
            });
        });

        modelList.ItemsSource = modelRows;
        ReusedRows.Use(modelList, () =>
        {
            TextBlock title = new() { Classes = { StyleClasses.Strong }, TextTrimming = TextTrimming.CharacterEllipsis };
            TextBlock sub = new() { Classes = { StyleClasses.Muted, StyleClasses.Caption }, TextTrimming = TextTrimming.CharacterEllipsis };
            StackPanel stack = new()
            {
                Spacing = Gaps.Hair,
                Margin = new Thickness(Gaps.Small, Gaps.Tight),
                Children = { title, sub },
            };
            return stack.Showing<AgentModelEntry>(entry =>
            {
                title.Text = entry.DisplayName;
                sub.Text = $"{entry.ModelId} · Think: {AgentThinkingCatalog.Label(entry.ThinkingLevel)} · Max {entry.MaxTokens}";
            });
        });

        temperatureBox.Text = clean.Temperature.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);

        Button addProviderButton = new() { Content = "Add" };
        AutomationProperties.SetName(addProviderButton, "Add Provider");
        addProviderButton.Click += (_, _) => AddProvider();
        duplicateProviderButton.Click += (_, _) => DuplicateProvider();
        removeProviderButton.Click += (_, _) => RemoveProvider();
        addModelButton.Click += (_, _) => AddModel();
        removeModelButton.Click += (_, _) => RemoveModel();
        testButton.Click += (_, _) => IdeErrors.Observe(ProbeSelectedAsync());

        providerList.SelectionChanged += (_, _) =>
        {
            if (!loadingFields && providerList.SelectedIndex >= 0 && providerList.SelectedIndex < providers.Count)
            {
                LoadProvider(providerList.SelectedIndex);
            }
        };
        modelList.SelectionChanged += (_, _) =>
        {
            if (!loadingFields && selectedProviderIndex >= 0 && modelList.SelectedIndex >= 0 && modelList.SelectedIndex < providers[selectedProviderIndex].Models.Count)
            {
                LoadModel(modelList.SelectedIndex);
            }
        };

        providerNameBox.TextChanged += (_, _) => CommitProviderEdits();
        baseUrlBox.TextChanged += (_, _) => CommitProviderEdits();
        apiKeyBox.TextChanged += (_, _) => CommitProviderEdits();
        presetBox.SelectionChanged += (_, _) => ApplyProviderPreset();
        protocolBox.SelectionChanged += (_, _) => CommitProviderEdits();

        modelIdBox.TextChanged += (_, _) => CommitModelEdits();
        modelNameBox.TextChanged += (_, _) => CommitModelEdits();
        thinkingBox.SelectionChanged += (_, _) => CommitModelEdits();
        maxTokensBox.TextChanged += (_, _) => CommitModelEdits();
        contextWindowBox.TextChanged += (_, _) => CommitModelEdits();

        ToolFlow providerActions = new()
        {
            Spacing = Gaps.Small,
            Margin = new Thickness(Gaps.Normal, Gaps.Small, Gaps.Normal, Gaps.Normal),
            Children = { addProviderButton, duplicateProviderButton, removeProviderButton },
        };
        DockPanel leftDock = new() { Width = 250 };
        DockPanel.SetDock(providerActions, Dock.Bottom);
        leftDock.Children.Add(providerActions);
        leftDock.Children.Add(providerList);
        Border leftPane = new() { BorderThickness = Hairlines.After, Child = leftDock };
        leftPane.Bind(Border.BorderBrushProperty, leftPane.GetResourceObservable(TokenKeys.Divider));

        Border modelFrame = new()
        {
            BorderThickness = Hairlines.Round,
            Height = 132,
            Child = modelList,
        };
        modelFrame.Bind(Border.BorderBrushProperty, modelFrame.GetResourceObservable(TokenKeys.PopupBorder));
        modelFrame.Bind(Border.BackgroundProperty, modelFrame.GetResourceObservable(TokenKeys.List));
        modelFrame.Bind(Border.CornerRadiusProperty, modelFrame.GetResourceObservable(TokenKeys.ControlRadius));

        ToolFlow modelButtons = new()
        {
            Spacing = Gaps.Small,
            Children = { addModelButton, removeModelButton },
        };

        ToolFlow probeRow = new()
        {
            Spacing = Gaps.Normal,
            Children = { testButton, statusText },
        };
        ToolFlow.SetFills(statusText, new ToolLayout.Fills(80));

        StackPanel fields = new()
        {
            Spacing = Gaps.Normal,
            Margin = new Thickness(0, 0, Gutter, Gaps.Group),
            Children =
            {
                new TextBlock { Text = "Provider", Classes = { StyleClasses.PaneTitle } },
                FormRows.Of("Name", providerNameBox),
                FormRows.Of("Preset", presetBox),
                FormRows.Of("Protocol", protocolBox),
                FormRows.Of("Base URL", baseUrlBox),
                FormRows.Of("API Key", apiKeyBox),
                FormRows.Of(null, probeRow),
                new TextBlock
                {
                    Text = "Models on this provider",
                    Classes = { StyleClasses.PaneTitle },
                    Margin = new Thickness(0, Gaps.Tight, 0, 0),
                },
                modelFrame,
                modelButtons,
                FormRows.Of("Model ID", modelIdBox),
                FormRows.Of("Display Name", modelNameBox),
                FormRows.Of("Thinking", thinkingBox),
                FormRows.Of("Max Tokens", maxTokensBox),
                FormRows.Of("Context Window", contextWindowBox),
                FormRows.Of("Temperature", temperatureBox),
            },
        };

        ScrollViewer rightScroll = new()
        {
            Margin = new Thickness(Gaps.Page, Gaps.Group, 0, 0),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = fields,
        };

        DockPanel.SetDock(leftPane, Dock.Left);
        DockPanel body = new() { Children = { leftPane, rightScroll } };

        Button ok = DialogFooter.Default("Save");
        ok.Click += (_, _) => Accept();
        Content = DialogFooter.Page(body, DialogFooter.OkCancel(ok, () => Close(false)));

        Closed += (_, _) =>
        {
            probing?.Cancel();
            probing?.Dispose();
            probing = null;
        };

        int initialProvider = providers.FindIndex(p => string.Equals(p.Id, clean.ActiveProviderId, StringComparison.Ordinal));
        RefreshProviderList(initialProvider >= 0 ? initialProvider : 0, clean.ActiveModelEntryId);
        Opened += (_, _) => Dispatcher.UIThread.Post(() => modelIdBox.Focus(), DispatcherPriority.Loaded);
    }

    public PiAgentConfig? Result { get; private set; }

    public int SelectedProviderIndex => selectedProviderIndex;
    public int SelectedModelIndex => selectedModelIndex;

    public void SelectProvider(int index) => providerList.SelectedIndex = index;

    public void SelectModel(int index) => modelList.SelectedIndex = index;

    private void RefreshProviderList(int selectIndex, string? preferredModelId = null)
    {
        int clamped = Math.Clamp(selectIndex, 0, Math.Max(0, providers.Count - 1));
        loadingFields = true;
        try
        {
            providerRows.Clear();
            foreach (AgentProviderEndpoint endpoint in providers)
            {
                providerRows.Add(ProviderRow.From(endpoint));
            }
            if (providers.Count > 0)
            {
                providerList.SelectedIndex = clamped;
            }
        }
        finally
        {
            loadingFields = false;
        }
        if (providers.Count > 0)
        {
            LoadProvider(clamped, preferredModelId);
        }
        removeProviderButton.IsEnabled = providers.Count > 1;
        duplicateProviderButton.IsEnabled = providers.Count > 0;
    }

    private void LoadProvider(int index, string? preferredModelId = null)
    {
        if (index < 0 || index >= providers.Count)
        {
            return;
        }
        loadingFields = true;
        try
        {
            selectedProviderIndex = index;
            AgentProviderEndpoint endpoint = providers[index];
            providerNameBox.Text = endpoint.Name;
            presetBox.SelectedItem = AgentProviderPreset.For(endpoint.Provider);
            protocolBox.SelectedItem = AgentProtocolCatalog.For(endpoint.Protocol);
            baseUrlBox.Text = endpoint.BaseUrl;
            apiKeyBox.Text = endpoint.ApiKey;
        }
        finally
        {
            loadingFields = false;
        }
        int modelIdx = preferredModelId is not null
            ? providers[index].Models.ToList().FindIndex(m => string.Equals(m.Id, preferredModelId, StringComparison.Ordinal))
            : 0;
        RefreshModelList(modelIdx >= 0 ? modelIdx : 0);
    }

    private void RefreshModelList(int selectIndex)
    {
        if (selectedProviderIndex < 0 || selectedProviderIndex >= providers.Count)
        {
            return;
        }
        IReadOnlyList<AgentModelEntry> list = providers[selectedProviderIndex].Models;
        int clamped = Math.Clamp(selectIndex, 0, Math.Max(0, list.Count - 1));
        loadingFields = true;
        try
        {
            modelRows.Clear();
            foreach (AgentModelEntry entry in list)
            {
                modelRows.Add(entry);
            }
            if (list.Count > 0)
            {
                modelList.SelectedIndex = clamped;
            }
        }
        finally
        {
            loadingFields = false;
        }
        if (list.Count > 0)
        {
            LoadModel(clamped);
        }
        removeModelButton.IsEnabled = list.Count > 1;
    }

    private void LoadModel(int index)
    {
        if (selectedProviderIndex < 0 || selectedProviderIndex >= providers.Count)
        {
            return;
        }
        IReadOnlyList<AgentModelEntry> list = providers[selectedProviderIndex].Models;
        if (index < 0 || index >= list.Count)
        {
            return;
        }
        loadingFields = true;
        try
        {
            selectedModelIndex = index;
            AgentModelEntry entry = list[index];
            modelIdBox.Text = entry.ModelId;
            modelNameBox.Text = entry.Name;
            thinkingBox.SelectedItem = new ThinkingChoice(entry.ThinkingLevel);
            maxTokensBox.Text = entry.MaxTokens.ToString(System.Globalization.CultureInfo.InvariantCulture);
            contextWindowBox.Text = entry.ContextWindow.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        finally
        {
            loadingFields = false;
        }
    }

    private void ApplyProviderPreset()
    {
        if (loadingFields || selectedProviderIndex < 0 || selectedProviderIndex >= providers.Count || presetBox.SelectedItem is not AgentProviderPreset preset)
        {
            return;
        }
        loadingFields = true;
        try
        {
            AgentProviderEndpoint current = providers[selectedProviderIndex];
            AgentProviderEndpoint updated = (current with
            {
                Provider = preset.Kind,
                Protocol = preset.Protocol,
                BaseUrl = preset.BaseUrl,
                Name = preset.Name,
            }).Clamped();
            providers[selectedProviderIndex] = updated;
            providerNameBox.Text = updated.Name;
            protocolBox.SelectedItem = AgentProtocolCatalog.For(updated.Protocol);
            baseUrlBox.Text = updated.BaseUrl;
            providerRows[selectedProviderIndex].Update(updated);
        }
        finally
        {
            loadingFields = false;
        }
    }

    private void CommitProviderEdits()
    {
        if (loadingFields || selectedProviderIndex < 0 || selectedProviderIndex >= providers.Count)
        {
            return;
        }
        AgentProviderKind provider = presetBox.SelectedItem is AgentProviderPreset p ? p.Kind : providers[selectedProviderIndex].Provider;
        AgentProtocol protocol = protocolBox.SelectedItem is AgentProtocolOption opt ? opt.Kind : providers[selectedProviderIndex].Protocol;
        AgentProviderEndpoint updated = (providers[selectedProviderIndex] with
        {
            Name = providerNameBox.Text ?? string.Empty,
            Provider = provider,
            Protocol = protocol,
            BaseUrl = baseUrlBox.Text ?? string.Empty,
            ApiKey = apiKeyBox.Text ?? string.Empty,
        }).Clamped();
        providers[selectedProviderIndex] = updated;
        providerRows[selectedProviderIndex].Update(updated);
    }

    private void CommitModelEdits()
    {
        if (loadingFields || selectedProviderIndex < 0 || selectedProviderIndex >= providers.Count)
        {
            return;
        }
        AgentProviderEndpoint endpoint = providers[selectedProviderIndex];
        if (selectedModelIndex < 0 || selectedModelIndex >= endpoint.Models.Count)
        {
            return;
        }
        AgentModelEntry existing = endpoint.Models[selectedModelIndex];
        AgentThinkingLevel thinking = thinkingBox.SelectedItem is ThinkingChoice choice ? choice.Level : existing.ThinkingLevel;
        int maxTokens = int.TryParse(maxTokensBox.Text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int m)
            ? m
            : existing.MaxTokens;
        long contextWindow = long.TryParse(contextWindowBox.Text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out long cw)
            ? cw
            : existing.ContextWindow;

        AgentModelEntry edited = (existing with
        {
            ModelId = modelIdBox.Text ?? string.Empty,
            Name = modelNameBox.Text ?? string.Empty,
            ThinkingLevel = thinking,
            MaxTokens = maxTokens,
            ContextWindow = contextWindow,
        }).Clamped(existing.ModelId);

        List<AgentModelEntry> updatedModels = [.. endpoint.Models];
        updatedModels[selectedModelIndex] = edited;
        AgentProviderEndpoint updatedEndpoint = (endpoint with { Models = updatedModels }).Clamped();
        providers[selectedProviderIndex] = updatedEndpoint;

        loadingFields = true;
        try
        {
            modelRows[selectedModelIndex] = edited;
            modelList.SelectedIndex = selectedModelIndex;
            providerRows[selectedProviderIndex].Update(updatedEndpoint);
        }
        finally
        {
            loadingFields = false;
        }
    }

    private void AddProvider()
    {
        AgentProviderEndpoint added = AgentProviderEndpoint.Create(AgentProviderKind.Custom) with
        {
            Id = AgentProviderEndpoint.NewId(),
            Name = $"Endpoint {providers.Count + 1}",
        };
        providers.Add(added);
        RefreshProviderList(providers.Count - 1);
    }

    private void DuplicateProvider()
    {
        if (selectedProviderIndex < 0 || selectedProviderIndex >= providers.Count)
        {
            return;
        }
        AgentProviderEndpoint source = providers[selectedProviderIndex];
        List<AgentModelEntry> copiedModels = [.. source.Models.Select(m => m with { Id = AgentModelEntry.NewId() })];
        AgentProviderEndpoint copy = source with
        {
            Id = AgentProviderEndpoint.NewId(),
            Name = source.DisplayName + " Copy",
            Models = copiedModels,
        };
        providers.Add(copy);
        RefreshProviderList(providers.Count - 1);
    }

    private void RemoveProvider()
    {
        if (providers.Count <= 1 || selectedProviderIndex < 0 || selectedProviderIndex >= providers.Count)
        {
            return;
        }
        providers.RemoveAt(selectedProviderIndex);
        RefreshProviderList(Math.Min(selectedProviderIndex, providers.Count - 1));
    }

    private void AddModel()
    {
        if (selectedProviderIndex < 0 || selectedProviderIndex >= providers.Count)
        {
            return;
        }
        AgentProviderEndpoint endpoint = providers[selectedProviderIndex];
        List<AgentModelEntry> list = [.. endpoint.Models];
        AgentModelEntry added = AgentModelEntry.Create($"model-{list.Count + 1}");
        list.Add(added);
        providers[selectedProviderIndex] = (endpoint with { Models = list }).Clamped();
        providerRows[selectedProviderIndex].Update(providers[selectedProviderIndex]);
        RefreshModelList(list.Count - 1);
    }

    private void RemoveModel()
    {
        if (selectedProviderIndex < 0 || selectedProviderIndex >= providers.Count)
        {
            return;
        }
        AgentProviderEndpoint endpoint = providers[selectedProviderIndex];
        if (endpoint.Models.Count <= 1 || selectedModelIndex < 0 || selectedModelIndex >= endpoint.Models.Count)
        {
            return;
        }
        List<AgentModelEntry> list = [.. endpoint.Models];
        list.RemoveAt(selectedModelIndex);
        providers[selectedProviderIndex] = (endpoint with { Models = list }).Clamped();
        providerRows[selectedProviderIndex].Update(providers[selectedProviderIndex]);
        RefreshModelList(Math.Min(selectedModelIndex, list.Count - 1));
    }

    private async Task ProbeSelectedAsync()
    {
        if (selectedProviderIndex < 0 || selectedProviderIndex >= providers.Count)
        {
            return;
        }
        CommitProviderEdits();
        CommitModelEdits();
        AgentProviderEndpoint endpoint = providers[selectedProviderIndex];
        AgentModelEntry entry = selectedModelIndex >= 0 && selectedModelIndex < endpoint.Models.Count
            ? endpoint.Models[selectedModelIndex]
            : endpoint.Models[0];

        AgentModelProfile target = new()
        {
            Id = entry.Id,
            Name = entry.DisplayName,
            Provider = endpoint.Provider,
            BaseUrl = endpoint.BaseUrl,
            ApiKey = endpoint.ApiKey,
            Model = entry.ModelId,
            Protocol = endpoint.Protocol,
            ThinkingLevel = entry.ThinkingLevel,
            MaxTokens = entry.MaxTokens,
            ContextWindow = entry.ContextWindow,
        };

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
            ConnectionTestResult probe = await runtime.TestConnectionAsync(target, cancel);
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
        CommitProviderEdits();
        CommitModelEdits();
        double temperature = double.TryParse(temperatureBox.Text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double t)
            ? t
            : 0.2;
        AgentProviderEndpoint chosenProvider = selectedProviderIndex >= 0 && selectedProviderIndex < providers.Count
            ? providers[selectedProviderIndex]
            : providers[0];
        AgentModelEntry chosenModel = selectedModelIndex >= 0 && selectedModelIndex < chosenProvider.Models.Count
            ? chosenProvider.Models[selectedModelIndex]
            : chosenProvider.Models[0];

        Result = new PiAgentConfig
        {
            Providers = [.. providers],
            ActiveProviderId = chosenProvider.Id,
            ActiveModelEntryId = chosenModel.Id,
            Temperature = temperature,
            MaxTokens = chosenModel.MaxTokens,
        }.Clamped();
        Close(true);
    }

    private sealed class ProviderRow : Observable
    {
        private string title = string.Empty;
        private string subtitle = string.Empty;

        public string Title
        {
            get => title;
            private set => Set(ref title, value);
        }

        public string Subtitle
        {
            get => subtitle;
            private set => Set(ref subtitle, value);
        }

        public static ProviderRow From(AgentProviderEndpoint endpoint)
        {
            ProviderRow row = new();
            row.Update(endpoint);
            return row;
        }

        public void Update(AgentProviderEndpoint endpoint)
        {
            Title = endpoint.DisplayName;
            Subtitle = $"{AgentProtocolCatalog.ShortLabel(endpoint.Protocol)} · {endpoint.Models.Count} model(s)";
        }
    }

    private sealed record ThinkingChoice(AgentThinkingLevel Level);
}
