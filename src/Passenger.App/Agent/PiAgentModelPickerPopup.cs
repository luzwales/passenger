using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Passenger.Agent;
using Passenger.App.LiveTree;

namespace Passenger.App.Agent;

/// <summary>Compact DSH-style popup for selecting Pi Agent provider, model, and thinking level.</summary>
public sealed class PiAgentModelPickerPopup : UserControl
{
    private const double ListHeight = ListRow.Height * 9;

    private readonly PiAgentConfig config;
    private readonly TextBox searchBox;
    private readonly ListBox providerList = new() { Classes = { StyleClasses.Tree }, MaxHeight = ListHeight };
    private readonly ListBox modelList = new() { Classes = { StyleClasses.Tree }, MaxHeight = ListHeight };
    private readonly TextBlock currentSummary = new() { Classes = { StyleClasses.Strong }, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock providerSummary = new() { Classes = { StyleClasses.Muted, StyleClasses.Caption }, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock thinkingValue = new() { Classes = { StyleClasses.Strong }, VerticalAlignment = VerticalAlignment.Center };
    private readonly ValueSlider thinkingSlider = new() { Step = 1.0, Fraction = v => v / (AgentThinkingCatalog.All.Count - 1), At = f => Math.Round(f * (AgentThinkingCatalog.All.Count - 1)) };
    private readonly ObservableCollection<ProviderItem> providerItems = [];
    private readonly ObservableCollection<ModelItem> modelItems = [];

    private string? activeProviderId;
    private string? activeModelId;
    private AgentThinkingLevel activeThinking;
    private bool loading;

    public PiAgentModelPickerPopup(PiAgentConfig config)
    {
        this.config = config.Clamped();
        activeProviderId = this.config.ActiveProvider?.Id;
        activeModelId = this.config.ActiveModelEntry?.Id;
        activeThinking = this.config.ActiveModelEntry?.ThinkingLevel ?? AgentThinkingLevel.Off;

        Width = PopupFrame.Medium;

        searchBox = SearchBox.InPopup("Model", "Search models…");
        AutomationProperties.SetName(providerList, "Providers");
        AutomationProperties.SetName(modelList, "Models");
        AutomationProperties.SetName(thinkingSlider, "Thinking Level");

        providerList.ItemsSource = providerItems;
        modelList.ItemsSource = modelItems;

        ReusedRows.Use(providerList, () =>
        {
            TextBlock name = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            TextBlock count = new() { Classes = { StyleClasses.Muted, StyleClasses.Caption }, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(Gaps.Small, 0, Gaps.Tight, 0) };
            DockPanel row = new() { Height = ListRow.Height, Margin = ListRow.Inset };
            DockPanel.SetDock(count, Dock.Right);
            row.Children.Add(count);
            row.Children.Add(name);
            return row.Showing<ProviderItem>(item =>
            {
                name.Text = item.Provider.DisplayName;
                count.Text = item.MatchCount.ToString(System.Globalization.CultureInfo.InvariantCulture);
            });
        });

        ReusedRows.Use(modelList, () =>
        {
            TextBlock name = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            TextBlock ctx = new() { Classes = { StyleClasses.Muted, StyleClasses.Caption }, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(Gaps.Small, 0, Gaps.Tight, 0) };
            DockPanel row = new() { Height = ListRow.Height, Margin = ListRow.Inset };
            DockPanel.SetDock(ctx, Dock.Right);
            row.Children.Add(ctx);
            row.Children.Add(name);
            return row.Showing<ModelItem>(item =>
            {
                name.Text = item.Model.DisplayName;
                ctx.Text = FormatContext(item.Model.ContextWindow);
            });
        });

        TextBlock title = new() { Classes = { StyleClasses.PaneTitle }, Text = "Model", VerticalAlignment = VerticalAlignment.Center };
        StackPanel activeInfo = new()
        {
            Orientation = Orientation.Horizontal,
            Spacing = Gaps.Small,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { currentSummary, providerSummary },
        };
        Grid header = new()
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            Margin = new Thickness(Gaps.Wide, Gaps.Wide, Gaps.Wide, Gaps.Normal),
        };
        Grid.SetColumn(title, 0);
        Grid.SetColumn(activeInfo, 1);
        header.Children.Add(title);
        header.Children.Add(activeInfo);

        searchBox.Margin = new Thickness(Gaps.Wide, 0, Gaps.Wide, Gaps.Normal);
        searchBox.TextChanged += (_, _) => RebuildLists();
        searchBox.AddHandler(KeyDownEvent, OnSearchKeyDown, RoutingStrategies.Tunnel);

        providerList.SelectionChanged += (_, _) =>
        {
            if (!loading && providerList.SelectedItem is ProviderItem chosen)
            {
                activeProviderId = chosen.Provider.Id;
                RebuildModels();
            }
        };
        modelList.SelectionChanged += (_, _) =>
        {
            if (!loading && modelList.SelectedItem is ModelItem chosen)
            {
                ChooseModel(chosen);
            }
        };

        Border leftPane = new() { Width = 170, BorderThickness = Hairlines.After, Child = providerList };
        leftPane.Bind(Border.BorderBrushProperty, leftPane.GetResourceObservable(TokenKeys.Divider));
        DockPanel.SetDock(leftPane, Dock.Left);
        DockPanel split = new() { Height = ListHeight, Children = { leftPane, modelList } };
        Border splitHost = new() { BorderThickness = Hairlines.Below, Child = split };
        splitHost.Bind(Border.BorderBrushProperty, splitHost.GetResourceObservable(TokenKeys.Divider));

        TextBlock thinkingTitle = new() { Classes = { StyleClasses.Muted, StyleClasses.Caption }, Text = "Thinking", VerticalAlignment = VerticalAlignment.Center };
        thinkingValue.HorizontalAlignment = HorizontalAlignment.Right;
        Grid thinkingHeader = new() { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        Grid.SetColumn(thinkingTitle, 0);
        Grid.SetColumn(thinkingValue, 1);
        thinkingHeader.Children.Add(thinkingTitle);
        thinkingHeader.Children.Add(thinkingValue);

        Grid levelLabels = new() { ColumnDefinitions = new ColumnDefinitions("*,*,*,*,*,*,*") };
        for (int i = 0; i < AgentThinkingCatalog.All.Count; i++)
        {
            AgentThinkingLevel level = AgentThinkingCatalog.All[i];
            Button tick = new()
            {
                Content = AgentThinkingCatalog.Label(level),
                Classes = { StyleClasses.Link, StyleClasses.Caption },
                HorizontalAlignment = HorizontalAlignment.Center,
                Padding = new Thickness(Gaps.Hair, 0),
                MinHeight = 0,
            };
            AutomationProperties.SetName(tick, $"Thinking {AgentThinkingCatalog.Label(level)}");
            tick.Click += (_, _) => ApplyThinking(level);
            Grid.SetColumn(tick, i);
            levelLabels.Children.Add(tick);
        }

        thinkingSlider.Changing += (_, v) => ShowThinkingLabel(IndexToLevel((int)Math.Round(v)));
        thinkingSlider.Committed += (_, v) => ApplyThinking(IndexToLevel((int)Math.Round(v)));

        Button configureButton = new()
        {
            Content = "Provider Settings…",
            Classes = { StyleClasses.Link, StyleClasses.Caption },
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, Gaps.Tight, 0, 0),
            MinHeight = 0,
            Padding = new Thickness(Gaps.Hair, 0),
        };
        AutomationProperties.SetName(configureButton, "Provider Settings");
        configureButton.Click += (_, _) => SettingsRequested?.Invoke(this, EventArgs.Empty);

        StackPanel thinkingBox = new()
        {
            Margin = new Thickness(Gaps.Wide, Gaps.Normal, Gaps.Wide, Gaps.Wide),
            Spacing = Gaps.Tight,
            Children = { thinkingHeader, thinkingSlider, levelLabels, configureButton },
        };

        DockPanel root = new();
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(searchBox, Dock.Top);
        DockPanel.SetDock(thinkingBox, Dock.Bottom);
        root.Children.Add(header);
        root.Children.Add(searchBox);
        root.Children.Add(thinkingBox);
        root.Children.Add(splitHost);

        Content = new Border
        {
            Classes = { StyleClasses.Popup },
            Child = root,
        };

        UpdateHeader();
        ShowThinkingLabel(activeThinking);
        thinkingSlider.Show((int)activeThinking);
        RebuildLists();

        Dispatcher.UIThread.Post(() => searchBox.Focus(), DispatcherPriority.Loaded);
    }

    public event EventHandler<(string ProviderId, string ModelEntryId)>? ModelSelected;
    public event EventHandler<AgentThinkingLevel>? ThinkingSelected;
    public event EventHandler? SettingsRequested;

    public IReadOnlyList<string> VisibleModelIds => [.. modelItems.Select(m => m.Model.ModelId)];

    private void RebuildLists()
    {
        string query = (searchBox.Text ?? string.Empty).Trim();
        loading = true;
        try
        {
            providerItems.Clear();
            foreach (AgentProviderEndpoint provider in config.Providers)
            {
                int count = query.Length == 0
                    ? provider.Models.Count
                    : provider.Models.Count(m => Matches(provider, m, query));
                if (count > 0 || query.Length == 0)
                {
                    providerItems.Add(new ProviderItem(provider, count));
                }
            }
            ProviderItem? chosen = providerItems.FirstOrDefault(p => string.Equals(p.Provider.Id, activeProviderId, StringComparison.Ordinal))
                ?? providerItems.FirstOrDefault();
            if (chosen is not null)
            {
                activeProviderId = chosen.Provider.Id;
                providerList.SelectedItem = chosen;
            }
        }
        finally
        {
            loading = false;
        }
        RebuildModels();
    }

    private void RebuildModels()
    {
        string query = (searchBox.Text ?? string.Empty).Trim();
        loading = true;
        try
        {
            modelItems.Clear();
            AgentProviderEndpoint? provider = config.Providers.FirstOrDefault(p => string.Equals(p.Id, activeProviderId, StringComparison.Ordinal))
                ?? config.Providers.FirstOrDefault();
            if (provider is not null)
            {
                foreach (AgentModelEntry model in provider.Models)
                {
                    if (query.Length == 0 || Matches(provider, model, query))
                    {
                        modelItems.Add(new ModelItem(provider.Id, provider.DisplayName, model));
                    }
                }
            }
            ModelItem? selected = modelItems.FirstOrDefault(m => string.Equals(m.Model.Id, activeModelId, StringComparison.Ordinal));
            modelList.SelectedItem = selected;
        }
        finally
        {
            loading = false;
        }
    }

    private void ChooseModel(ModelItem chosen)
    {
        activeProviderId = chosen.ProviderId;
        activeModelId = chosen.Model.Id;
        activeThinking = chosen.Model.ThinkingLevel;
        UpdateHeader();
        ShowThinkingLabel(activeThinking);
        thinkingSlider.Show((int)activeThinking);
        ModelSelected?.Invoke(this, (chosen.ProviderId, chosen.Model.Id));
    }

    private void ApplyThinking(AgentThinkingLevel level)
    {
        activeThinking = level;
        ShowThinkingLabel(level);
        thinkingSlider.Show((int)level);
        ThinkingSelected?.Invoke(this, level);
    }

    private void ShowThinkingLabel(AgentThinkingLevel level) =>
        thinkingValue.Text = AgentThinkingCatalog.Label(level);

    private void UpdateHeader()
    {
        AgentProviderEndpoint? provider = config.Providers.FirstOrDefault(p => string.Equals(p.Id, activeProviderId, StringComparison.Ordinal))
            ?? config.ActiveProvider;
        AgentModelEntry? model = provider?.Models.FirstOrDefault(m => string.Equals(m.Id, activeModelId, StringComparison.Ordinal))
            ?? config.ActiveModelEntry;
        currentSummary.Text = model?.DisplayName ?? "Select model";
        providerSummary.Text = provider?.DisplayName ?? string.Empty;
    }

    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Down && modelItems.Count > 0)
        {
            int next = Math.Min(modelItems.Count - 1, modelList.SelectedIndex + 1);
            modelList.SelectedIndex = next;
            e.Handled = true;
        }
        else if (e.Key == Key.Up && modelItems.Count > 0)
        {
            int prev = Math.Max(0, modelList.SelectedIndex - 1);
            modelList.SelectedIndex = prev;
            e.Handled = true;
        }
    }

    private static bool Matches(AgentProviderEndpoint provider, AgentModelEntry model, string query) =>
        model.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase)
        || model.ModelId.Contains(query, StringComparison.OrdinalIgnoreCase)
        || provider.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase);

    private static string FormatContext(long tokens) => tokens switch
    {
        >= 1_000_000 => $"{tokens / 1_000_000}M",
        >= 1_000 => $"{tokens / 1_000}K",
        _ => tokens.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };

    private static AgentThinkingLevel IndexToLevel(int index)
    {
        int clamped = Math.Clamp(index, 0, AgentThinkingCatalog.All.Count - 1);
        return AgentThinkingCatalog.All[clamped];
    }

    private sealed record ProviderItem(AgentProviderEndpoint Provider, int MatchCount);

    private sealed record ModelItem(string ProviderId, string ProviderName, AgentModelEntry Model);
}
