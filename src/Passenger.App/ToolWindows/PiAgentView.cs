using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Passenger.Agent;
using Passenger.App.Agent;
using Passenger.App.Docking;
using Passenger.App.Editors;
using Passenger.App.Settings;
using Passenger.Core;
using Passenger.Editor;

namespace Passenger.App.ToolWindows;

/// <summary>Docked Pi Agent panel: header title-row tools, collapsible workspace session sidebar,
/// shared ~/.pi/agent/ history, Markdown assistant cards, and DSH-style composer bar.</summary>
public sealed class PiAgentView : DockPanel
{
    private readonly PiAgentRuntime runtime;
    private readonly Func<string?> folderProvider;
    private readonly Func<bool> trustedProvider;
    private readonly PopupHost? popups;
    private readonly Action<Uri>? browse;
    private readonly ObservableCollection<ConversationItem> items = [];
    private readonly ObservableCollection<AgentAttachment> attachments = [];
    private readonly ObservableCollection<PiAgentSessionSummary> workspaceSessions = [];

    private readonly ToggleButton sidebarToggle;
    private readonly Button newSessionButton;
    private readonly Button settingsButton;
    private readonly Border sidebarFrame;
    private readonly ListBox sessionList = new() { Classes = { StyleClasses.Tree } };
    private readonly EmptyState sidebarEmpty = new("No sessions yet");
    private readonly PaneSplit bodySplit;

    private readonly Banner trustNotice = new();
    private readonly ItemsControl feed = new() { Margin = new Thickness(Gaps.Normal) };
    private readonly ScrollViewer feedScroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly EmptyState empty = new("Ask Pi Agent about this workspace. Reads, searches, and edits stay inside the open folder.");
    private readonly ItemsControl attachmentStrip = new() { IsVisible = false };
    private readonly TextBox composer = new()
    {
        AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap,
        MinHeight = 56,
        MaxHeight = 140,
        PlaceholderText = "Ask Pi Agent to inspect, explain, or edit files in this workspace…",
    };

    private readonly Button attachButton;
    private readonly Button modelPickerButton = new() { Classes = { StyleClasses.Widget }, Focusable = false };
    private readonly TextBlock modelPickerText = new() { Classes = { StyleClasses.Caption }, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 150 };
    private readonly Button sendButton;
    private readonly Button stopButton;
    private readonly TextBlock turnStatus = new() { Classes = { StyleClasses.Caption, StyleClasses.Muted }, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };

    private PiAgentConfig config = new PiAgentConfig { Models = [AgentModelProfile.Create(AgentProviderKind.OpenAI)] }.Clamped();
    private string sessionId = NewSessionId();
    private string? activeSessionFile;
    private CancellationTokenSource? activeTurn;
    private bool sidebarOpen;
    private string? loadedWorkspace;

    public PiAgentView(PiAgentRuntime runtime, Func<string?> folderProvider, Func<bool> trustedProvider, PopupHost? popups = null, Action<Uri>? browse = null)
    {
        this.runtime = runtime;
        this.folderProvider = folderProvider;
        this.trustedProvider = trustedProvider;
        this.popups = popups;
        this.browse = browse;

        AutomationProperties.SetName(composer, "Pi Agent Prompt");
        AutomationProperties.SetName(sessionList, "Workspace Sessions");

        sidebarToggle = ToolButtons.Toggle(IconKeys.History, "Sessions Sidebar");
        sidebarToggle.IsChecked = sidebarOpen;
        sidebarToggle.Click += (_, _) => SetSidebarOpen(!sidebarOpen, notify: true);

        newSessionButton = ToolButtons.Of(AppActions.PiAgentNewSession, IconKeys.Add, "New Session", action => ActionRequested?.Invoke(this, action));
        settingsButton = ToolButtons.Of(AppActions.PiAgentSettings, IconKeys.Settings, "Provider Settings…", action => ActionRequested?.Invoke(this, action));
        HeaderTools = new ShrinkingRow { Spacing = Gaps.Hair, Children = { sidebarToggle, newSessionButton, settingsButton } };

        attachButton = ToolButtons.Of(IconKeys.Add, "Attach File, Folder, or Image", OpenAttachMenu);

        PathIcon chevron = new() { Classes = { StyleClasses.Small } };
        chevron.Bind(PathIcon.DataProperty, chevron.GetResourceObservable(IconKeys.ChevronDown));
        modelPickerButton.Content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = Gaps.Tight,
            Children = { modelPickerText, chevron },
        };
        ToolButtons.Tip(modelPickerButton, "Select Provider, Model, and Thinking Level");
        modelPickerButton.Click += (_, _) => OpenModelPickerMenu();

        PathIcon sendIcon = new() { Classes = { StyleClasses.Small } };
        sendIcon.Bind(PathIcon.DataProperty, sendIcon.GetResourceObservable(IconKeys.Send));
        sendButton = new Button
        {
            Classes = { StyleClasses.Primary },
            Content = sendIcon,
            Padding = new Thickness(Gaps.Normal, 0),
        };
        ToolButtons.Tip(sendButton, "Send Prompt");
        sendButton.Click += (_, _) => IdeErrors.Observe(SendAsync());

        PathIcon stopIcon = new() { Classes = { StyleClasses.Small } };
        stopIcon.Bind(PathIcon.DataProperty, stopIcon.GetResourceObservable(IconKeys.Stop));
        stopButton = new Button
        {
            Content = stopIcon,
            Padding = new Thickness(Gaps.Normal, 0),
            IsVisible = false,
        };
        ToolButtons.Tip(stopButton, "Stop Agent");
        stopButton.Click += (_, _) => AbortCurrentTurn();

        composer.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
            {
                if (e.KeyModifiers == KeyModifiers.None || e.KeyModifiers.HasFlag(Keystroke.Command))
                {
                    e.Handled = true;
                    IdeErrors.Observe(SendAsync());
                }
            }
        };

        sessionList.ItemsSource = workspaceSessions;
        ReusedRows.Use(sessionList, BuildSessionRow);
        sessionList.SelectionChanged += (_, _) =>
        {
            if (sessionList.SelectedItem is PiAgentSessionSummary chosen && !string.Equals(chosen.FilePath, activeSessionFile, StringComparison.OrdinalIgnoreCase))
            {
                OpenSession(chosen);
            }
        };
        ListMenus.Attach(sessionList, () => sessionList.SelectedItem is not PiAgentSessionSummary chosen ? [] :
        [
            ListMenus.Item("Open Session", () => OpenSession(chosen)),
            ListMenus.Remove("Delete Session", () => DeleteSession(chosen)),
        ]);

        Panel sidebarHost = new() { Children = { sessionList, sidebarEmpty } };
        sidebarFrame = new Border
        {
            BorderThickness = Hairlines.After,
            Child = sidebarHost,
        };
        sidebarFrame.Bind(Border.BorderBrushProperty, sidebarFrame.GetResourceObservable(TokenKeys.Divider));

        DockPanel.SetDock(trustNotice, Dock.Top);

        feed.ItemsSource = items;
        ReusedRows.Use(feed, BuildConversationCard);
        feedScroll.Content = feed;
        Panel feedHost = new() { Children = { feedScroll, empty } };

        attachmentStrip.ItemsSource = attachments;
        attachmentStrip.ItemsPanel = new Avalonia.Controls.Templates.FuncTemplate<Panel?>(() => new ToolFlow { Spacing = Gaps.Tight });
        ReusedRows.Use(attachmentStrip, BuildAttachmentChip);

        ToolFlow composerBar = new()
        {
            Spacing = Gaps.Tight,
            Children = { attachButton, turnStatus, modelPickerButton, sendButton, stopButton },
        };
        ToolFlow.SetFills(turnStatus, new ToolLayout.Fills(0));

        StackPanel composerBox = new()
        {
            Margin = new Thickness(Gaps.Normal),
            Spacing = Gaps.Small,
            Children = { attachmentStrip, composer, composerBar },
        };
        Border composerPanel = new()
        {
            BorderThickness = Hairlines.Round,
            Margin = new Thickness(Gaps.Normal, 0, Gaps.Normal, Gaps.Normal),
            Child = composerBox,
        };
        composerPanel.Bind(Border.BorderBrushProperty, composerPanel.GetResourceObservable(TokenKeys.PopupBorder));
        composerPanel.Bind(Border.BackgroundProperty, composerPanel.GetResourceObservable(TokenKeys.List));
        composerPanel.Bind(Border.CornerRadiusProperty, composerPanel.GetResourceObservable(TokenKeys.ControlRadius));
        DockPanel.SetDock(composerPanel, Dock.Bottom);

        DockPanel chatPane = new() { LastChildFill = true };
        chatPane.Children.Add(trustNotice);
        chatPane.Children.Add(composerPanel);
        chatPane.Children.Add(feedHost);

        bodySplit = new PaneSplit(sidebarFrame, chatPane, "170,1,*", "150,1,*");
        Children.Add(bodySplit);

        ApplyConfig(config, persist: false);
        SetSidebarOpen(sidebarOpen, notify: false);
        RefreshWorkspaceState();
    }

    public Control HeaderTools { get; }
    public IReadOnlyList<AgentAttachment> Attachments => attachments;
    public IReadOnlyList<PiAgentSessionSummary> Sessions => workspaceSessions;
    public bool IsSidebarOpen => sidebarOpen;

    public event EventHandler<string>? ActionRequested;
    public event EventHandler<PiAgentConfig>? ConfigChanged;
    public event EventHandler<bool>? SidebarToggled;

    public string? PromptDraft
    {
        get => composer.Text;
        set => composer.Text = value ?? string.Empty;
    }

    public PiAgentConfig Config => config;

    public void ApplyConfig(PiAgentConfig next, bool persist = false)
    {
        config = next.Clamped();
        UpdateModelPickerLabel();
        if (persist)
        {
            ConfigChanged?.Invoke(this, config);
        }
    }

    public void SetSidebarOpen(bool open, bool notify = true)
    {
        sidebarOpen = open;
        sidebarToggle.IsChecked = open;
        sidebarFrame.IsVisible = open;
        bodySplit.Divider.IsVisible = open;
        if (bodySplit.ColumnDefinitions.Count >= 3)
        {
            bodySplit.ColumnDefinitions[0].Width = open ? new GridLength(170) : new GridLength(0);
            bodySplit.ColumnDefinitions[1].Width = open ? new GridLength(1) : new GridLength(0);
        }
        if (bodySplit.RowDefinitions.Count >= 3)
        {
            bodySplit.RowDefinitions[0].Height = open ? new GridLength(150) : new GridLength(0);
            bodySplit.RowDefinitions[1].Height = open ? new GridLength(1) : new GridLength(0);
        }
        if (notify)
        {
            SidebarToggled?.Invoke(this, open);
        }
    }

    public void SwitchProtocol(AgentProtocol protocol)
    {
        if (config.ActiveProvider is not { } activeProvider)
        {
            return;
        }
        List<AgentProviderEndpoint> list = [.. config.Providers];
        int index = list.FindIndex(p => string.Equals(p.Id, activeProvider.Id, StringComparison.Ordinal));
        if (index < 0)
        {
            return;
        }
        list[index] = (list[index] with { Protocol = protocol }).Clamped();
        ApplyConfig((config with { Providers = list }).Clamped(), persist: true);
    }

    public void SelectModel(string providerId, string modelEntryId) =>
        ApplyConfig((config with { ActiveProviderId = providerId, ActiveModelEntryId = modelEntryId }).Clamped(), persist: true);

    public void SetThinkingLevel(AgentThinkingLevel level)
    {
        if (config.ActiveProvider is not { } provider || config.ActiveModelEntry is not { } model)
        {
            return;
        }
        List<AgentProviderEndpoint> providers = [.. config.Providers];
        int pIdx = providers.FindIndex(p => string.Equals(p.Id, provider.Id, StringComparison.Ordinal));
        if (pIdx < 0)
        {
            return;
        }
        List<AgentModelEntry> models = [.. providers[pIdx].Models];
        int mIdx = models.FindIndex(m => string.Equals(m.Id, model.Id, StringComparison.Ordinal));
        if (mIdx < 0)
        {
            return;
        }
        models[mIdx] = models[mIdx] with { ThinkingLevel = level };
        providers[pIdx] = (providers[pIdx] with { Models = models }).Clamped();
        ApplyConfig((config with { Providers = providers }).Clamped(), persist: true);
    }

    public bool AttachPath(string path, bool folder = false, bool imageOnly = false)
    {
        if (folderProvider() is not { } root)
        {
            return false;
        }
        int before = attachments.Count;
        TryAttach(path, root, folder, imageOnly);
        return attachments.Count > before;
    }

    public void RefreshWorkspaceState()
    {
        string? folder = folderProvider();
        bool hasFolder = !string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder);
        bool trusted = hasFolder && trustedProvider();
        if (!hasFolder)
        {
            trustNotice.Say("Open a folder or solution to run Pi Agent inside that workspace.");
        }
        else if (!trusted)
        {
            trustNotice.Say("Workspace untrusted: read/search tools active; write and command execution disabled until trusted.");
        }
        else
        {
            trustNotice.Say(null);
        }

        string? canonical = hasFolder ? PathNames.Canonical(folder!) : null;
        if (!string.Equals(loadedWorkspace, canonical, StringComparison.OrdinalIgnoreCase))
        {
            loadedWorkspace = canonical;
            activeSessionFile = null;
            sessionId = NewSessionId();
            items.Clear();
            empty.IsVisible = true;
            IdeErrors.Observe(RefreshSessionsAsync(restoreLatest: true));
        }
        UpdateComposerState();
    }

    public void StartNewSession()
    {
        AbortCurrentTurn();
        sessionId = NewSessionId();
        activeSessionFile = null;
        sessionList.SelectedItem = null;
        items.Clear();
        attachments.Clear();
        attachmentStrip.IsVisible = false;
        empty.IsVisible = true;
        turnStatus.Text = "New session";
        runtime.InvalidateSessions();
        UpdateComposerState();
    }

    public async Task RefreshSessionsAsync(bool restoreLatest = false)
    {
        string? folder = folderProvider();
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            workspaceSessions.Clear();
            sidebarEmpty.IsVisible = true;
            return;
        }
        IReadOnlyList<PiAgentSessionSummary> list = await PiAgentSessionStore.ListAsync(folder);
        if (!string.Equals(loadedWorkspace, PathNames.Canonical(folder), StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        workspaceSessions.Clear();
        foreach (PiAgentSessionSummary item in list)
        {
            workspaceSessions.Add(item);
        }
        sidebarEmpty.IsVisible = workspaceSessions.Count == 0;

        if (restoreLatest && activeSessionFile is null && items.Count == 0 && workspaceSessions.Count > 0)
        {
            OpenSession(workspaceSessions[0]);
        }
        else if (activeSessionFile is not null)
        {
            PiAgentSessionSummary? match = workspaceSessions.FirstOrDefault(s => string.Equals(s.FilePath, activeSessionFile, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                sessionList.SelectedItem = match;
            }
        }
    }

    public void OpenSession(PiAgentSessionSummary summary)
    {
        AbortCurrentTurn();
        sessionId = summary.Id;
        activeSessionFile = summary.FilePath;
        items.Clear();
        foreach (PiAgentMessageRecord record in PiAgentSessionStore.LoadTranscript(summary.FilePath, folderProvider()))
        {
            ConversationRole role = record.Kind switch
            {
                PiAgentMessageKind.User => ConversationRole.User,
                PiAgentMessageKind.Tool => ConversationRole.Tool,
                _ => ConversationRole.Assistant,
            };
            ConversationItem item = new(role, record.Title, record.Body);
            if (!string.IsNullOrEmpty(record.Thinking))
            {
                item.AppendThinking(record.Thinking);
            }
            items.Add(item);
        }
        empty.IsVisible = items.Count == 0;
        turnStatus.Text = summary.Title;
        sessionList.SelectedItem = summary;
        ScrollToEnd();
        UpdateComposerState();
    }

    private void DeleteSession(PiAgentSessionSummary summary)
    {
        if (!PiAgentSessionStore.DeleteSession(summary.FilePath))
        {
            return;
        }
        workspaceSessions.Remove(summary);
        sidebarEmpty.IsVisible = workspaceSessions.Count == 0;
        if (string.Equals(activeSessionFile, summary.FilePath, StringComparison.OrdinalIgnoreCase))
        {
            StartNewSession();
        }
    }

    private void OpenAttachMenu()
    {
        ContextMenu menu = new() { Placement = PlacementMode.TopEdgeAlignedLeft };
        menu.Items.Add(ListMenus.Item("Attach File…", () => IdeErrors.Observe(PickFilesAsync()), icon: IconKeys.DocumentText));
        menu.Items.Add(ListMenus.Item("Attach Folder…", () => IdeErrors.Observe(PickFolderAsync()), icon: IconKeys.Explorer));
        menu.Items.Add(ListMenus.Item("Attach Image…", () => IdeErrors.Observe(PickImagesAsync()), icon: IconKeys.Image));
        menu.Open(attachButton);
    }

    private void OpenModelPickerMenu()
    {
        if (popups is not null && TopLevel.GetTopLevel(this) is Window owner)
        {
            PiAgentModelPickerPopup picker = new(config);
            picker.ModelSelected += (_, selection) => SelectModel(selection.ProviderId, selection.ModelEntryId);
            picker.ThinkingSelected += (_, level) => SetThinkingLevel(level);
            picker.SettingsRequested += (_, _) =>
            {
                popups.Close();
                ActionRequested?.Invoke(this, AppActions.PiAgentSettings);
            };
            Point? anchor = modelPickerButton.TranslatePoint(new Point(0, 0), owner);
            Point? at = anchor is { } pt
                ? popups.PointIn(owner, new Point(pt.X, Math.Max(Gaps.Normal, pt.Y - 340)))
                : popups.PointIn(owner, new Point(Gaps.Group, Gaps.Group));
            popups.Show(picker, at);
            return;
        }

        ContextMenu menu = new() { Placement = PlacementMode.TopEdgeAlignedRight };
        AgentProviderEndpoint? activeProvider = config.ActiveProvider;
        AgentModelEntry? activeModel = config.ActiveModelEntry;

        menu.Items.Add(ListMenus.Heading("Models"));
        foreach (AgentProviderEndpoint provider in config.Providers)
        {
            foreach (AgentModelEntry model in provider.Models)
            {
                bool current = activeProvider?.Id == provider.Id && activeModel?.Id == model.Id;
                string label = $"{provider.DisplayName} · {model.DisplayName} ({AgentThinkingCatalog.Label(model.ThinkingLevel)})";
                string pId = provider.Id;
                string mId = model.Id;
                menu.Items.Add(ListMenus.Toggle(label, current, _ => SelectModel(pId, mId)));
            }
        }

        if (activeModel is not null)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(ListMenus.Heading("Thinking Level"));
            foreach (AgentThinkingLevel level in AgentThinkingCatalog.All)
            {
                AgentThinkingLevel target = level;
                menu.Items.Add(ListMenus.Toggle(
                    AgentThinkingCatalog.Label(level),
                    activeModel.ThinkingLevel == level,
                    _ => SetThinkingLevel(target)));
            }
        }

        menu.Items.Add(new Separator());
        menu.Items.Add(ListMenus.Item("Provider Settings…", () => ActionRequested?.Invoke(this, AppActions.PiAgentSettings), icon: IconKeys.Settings));
        menu.Open(modelPickerButton);
    }

    private void UpdateModelPickerLabel()
    {
        if (config.ActiveProvider is { } provider && config.ActiveModelEntry is { } model)
        {
            string think = model.ThinkingLevel == AgentThinkingLevel.Off ? string.Empty : $" · {AgentThinkingCatalog.Label(model.ThinkingLevel)}";
            modelPickerText.Text = $"{provider.DisplayName} · {model.DisplayName}{think}";
        }
        else
        {
            modelPickerText.Text = "Configure Model";
        }
    }

    private async Task PickFilesAsync()
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage || folderProvider() is not { } root)
        {
            return;
        }
        IStorageFolder? start = await storage.TryGetFolderFromPathAsync(root);
        IReadOnlyList<IStorageFile> picked = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Attach workspace files",
            AllowMultiple = true,
            SuggestedStartLocation = start,
        });
        foreach (IStorageFile file in picked)
        {
            if (file.TryGetLocalPath() is { } path)
            {
                TryAttach(path, root, folder: false);
            }
        }
    }

    private async Task PickFolderAsync()
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage || folderProvider() is not { } root)
        {
            return;
        }
        IStorageFolder? start = await storage.TryGetFolderFromPathAsync(root);
        IReadOnlyList<IStorageFolder> picked = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Attach workspace folder",
            AllowMultiple = false,
            SuggestedStartLocation = start,
        });
        if (picked is [var folder] && folder.TryGetLocalPath() is { } path)
        {
            TryAttach(path, root, folder: true);
        }
    }

    private async Task PickImagesAsync()
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage || folderProvider() is not { } root)
        {
            return;
        }
        IStorageFolder? start = await storage.TryGetFolderFromPathAsync(root);
        IReadOnlyList<IStorageFile> picked = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Attach workspace images",
            AllowMultiple = true,
            SuggestedStartLocation = start,
            FileTypeFilter = [FilePickerFileTypes.ImageAll],
        });
        foreach (IStorageFile file in picked)
        {
            if (file.TryGetLocalPath() is { } path)
            {
                TryAttach(path, root, folder: false, imageOnly: true);
            }
        }
    }

    private void TryAttach(string path, string workspaceRoot, bool folder, bool imageOnly = false)
    {
        string canonicalRoot = PathNames.Canonical(workspaceRoot);
        string canonicalPath = PathNames.Canonical(path);
        if (!PathNames.Within(canonicalPath, canonicalRoot))
        {
            turnStatus.Text = $"Attachment skipped: outside workspace {Path.GetFileName(canonicalRoot)}";
            return;
        }
        if (attachments.Any(a => PathNames.Same(a.Path, canonicalPath)))
        {
            return;
        }
        AgentAttachment loaded = folder
            ? WorkspaceAttachments.LoadFolder(canonicalPath, canonicalRoot)
            : imageOnly
                ? WorkspaceAttachments.LoadImage(canonicalPath, canonicalRoot)
                : WorkspaceAttachments.LoadFile(canonicalPath, canonicalRoot);
        if (loaded.Error is { Length: > 0 } err)
        {
            turnStatus.Text = $"Could not attach {loaded.DisplayPath}: {err}";
            return;
        }
        attachments.Add(loaded);
        attachmentStrip.IsVisible = attachments.Count > 0;
    }

    private async Task SendAsync()
    {
        if (activeTurn is not null)
        {
            return;
        }
        string rawPrompt = (composer.Text ?? string.Empty).Trim();
        if (rawPrompt.Length == 0 && attachments.Count == 0)
        {
            return;
        }
        string? folder = folderProvider();
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            turnStatus.Text = "Open a workspace folder first";
            return;
        }
        if (config.ActiveModel is not { } profile || string.IsNullOrWhiteSpace(profile.ApiKey))
        {
            turnStatus.Text = "Configure API Key in Provider Settings";
            ActionRequested?.Invoke(this, AppActions.PiAgentSettings);
            return;
        }

        List<AgentHistoryTurn> history = BuildHistory();
        List<AgentAttachment> sentAttachments = [.. attachments];
        List<AgentAttachment> sentImages = [.. sentAttachments.Where(a => a.Kind == AgentAttachmentKind.Image)];
        string composedPrompt = WorkspaceAttachments.ComposePrompt(rawPrompt, sentAttachments);

        composer.Text = string.Empty;
        attachments.Clear();
        attachmentStrip.IsVisible = false;
        empty.IsVisible = false;

        string userDisplay = rawPrompt.Length > 0 ? rawPrompt : $"Attached {sentAttachments.Count} item(s)";
        if (sentAttachments.Count > 0)
        {
            userDisplay += "\n" + string.Join(", ", sentAttachments.Select(a => a.DisplayPath));
        }
        items.Add(new ConversationItem(ConversationRole.User, "You", userDisplay));
        ConversationItem assistant = new(ConversationRole.Assistant, $"{profile.Model} · {AgentProtocolCatalog.ShortLabel(profile.Protocol)}", string.Empty);
        items.Add(assistant);
        ScrollToEnd();

        activeTurn = new CancellationTokenSource();
        CancellationToken cancel = activeTurn.Token;
        UpdateComposerState();
        turnStatus.Text = "Running…";

        AgentRunRequest request = new(sessionId, composedPrompt, folder, history, sentImages, activeSessionFile);
        Dictionary<string, ConversationItem> toolCards = new(StringComparer.Ordinal);
        try
        {
            await foreach (AgentStreamEvent ev in runtime.RunAsync(request, config, cancel))
            {
                switch (ev)
                {
                    case AgentStreamEvent.RunStarted startedRun when !string.IsNullOrWhiteSpace(startedRun.SessionFile):
                        activeSessionFile = startedRun.SessionFile;
                        break;
                    case AgentStreamEvent.ThinkingDelta thinking:
                        assistant.AppendThinking(thinking.Delta);
                        break;
                    case AgentStreamEvent.TextDelta delta:
                        assistant.AppendBody(delta.Delta);
                        ScrollToEnd();
                        break;
                    case AgentStreamEvent.ToolStarted started:
                        ConversationItem startedCard = new(ConversationRole.Tool, started.Label, started.Arguments);
                        toolCards[started.CallId] = startedCard;
                        items.Add(startedCard);
                        ScrollToEnd();
                        break;
                    case AgentStreamEvent.ToolFinished finished when toolCards.TryGetValue(finished.CallId, out ConversationItem? card):
                        string header = $"{WorkspaceScopedTools.LabelOf(finished.Name)} ({finished.DurationMs ?? 0} ms)";
                        card.Title = finished.IsError ? header + " — Failed" : header;
                        card.SetBody(string.IsNullOrWhiteSpace(finished.Result) ? card.Body : card.Body + "\n\n" + finished.Result);
                        ScrollToEnd();
                        break;
                    case AgentStreamEvent.RunCompleted done:
                        if (!string.IsNullOrWhiteSpace(done.SessionFile))
                        {
                            activeSessionFile = done.SessionFile;
                        }
                        if (!string.IsNullOrWhiteSpace(done.Error))
                        {
                            assistant.AppendBody((assistant.Body.Length > 0 ? "\n\n" : string.Empty) + done.Error);
                        }
                        assistant.FlushMarkdown();
                        turnStatus.Text = done.StopReason == AgentStopReason.Aborted
                            ? "Stopped"
                            : $"{done.Model} · {done.InputTokens}+{done.OutputTokens} tok";
                        break;
                    case AgentStreamEvent.RunFailed failed:
                        assistant.AppendBody((assistant.Body.Length > 0 ? "\n\n" : string.Empty) + "Error: " + failed.Message);
                        assistant.FlushMarkdown();
                        turnStatus.Text = "Failed";
                        break;
                }
            }
        }
        finally
        {
            assistant.FlushMarkdown();
            activeTurn.Dispose();
            activeTurn = null;
            UpdateComposerState();
            ScrollToEnd();
            await RefreshSessionsAsync(restoreLatest: false);
        }
    }

    private void AbortCurrentTurn()
    {
        if (activeTurn is not null)
        {
            activeTurn.Cancel();
            runtime.Abort(sessionId);
        }
    }

    private void UpdateComposerState()
    {
        bool running = activeTurn is not null;
        bool hasFolder = !string.IsNullOrWhiteSpace(folderProvider()) && Directory.Exists(folderProvider()!);
        sendButton.IsVisible = !running;
        sendButton.IsEnabled = !running && hasFolder;
        stopButton.IsVisible = running;
        stopButton.IsEnabled = running;
        attachButton.IsEnabled = !running && hasFolder;
    }

    private List<AgentHistoryTurn> BuildHistory()
    {
        List<AgentHistoryTurn> list = [];
        foreach (ConversationItem item in items)
        {
            if (item.Role == ConversationRole.User)
            {
                list.Add(AgentHistoryTurn.User(item.Body));
            }
            else if (item.Role == ConversationRole.Assistant && !string.IsNullOrWhiteSpace(item.Body))
            {
                list.Add(AgentHistoryTurn.Assistant(item.Body));
            }
        }
        return list;
    }

    private void ScrollToEnd() => UiThread.Post(() => feedScroll.ScrollToEnd());

    private Control BuildSessionRow()
    {
        TextBlock title = new() { Classes = { StyleClasses.Strong, StyleClasses.Caption }, TextTrimming = TextTrimming.CharacterEllipsis };
        TextBlock detail = new() { Classes = { StyleClasses.Muted, StyleClasses.Caption }, TextTrimming = TextTrimming.CharacterEllipsis };
        return new StackPanel
        {
            Spacing = Gaps.Hair,
            Margin = new Thickness(Gaps.Small, Gaps.Tight),
            Children = { title, detail },
        }.Showing<PiAgentSessionSummary>(summary =>
        {
            title.Text = summary.Title;
            detail.Text = $"{summary.Modified.LocalDateTime:MM-dd HH:mm} · {summary.MessageCount} msg";
        });
    }

    private Control BuildAttachmentChip()
    {
        TextBlock label = new() { Classes = { StyleClasses.Caption }, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 180 };
        Button remove = ToolButtons.Small(IconKeys.Close, "Remove Attachment", () => { });
        StackPanel row = new() { Orientation = Orientation.Horizontal, Spacing = Gaps.Tight, Children = { label, remove } };
        Border chip = new()
        {
            BorderThickness = Hairlines.Round,
            Padding = new Thickness(Gaps.Small, Gaps.Hair),
            Child = row,
        };
        chip.Bind(Border.BorderBrushProperty, chip.GetResourceObservable(TokenKeys.PopupBorder));
        chip.Bind(Border.BackgroundProperty, chip.GetResourceObservable(TokenKeys.Button));
        chip.Bind(Border.CornerRadiusProperty, chip.GetResourceObservable(TokenKeys.ControlRadius));
        remove.Click += (_, _) =>
        {
            if (chip.DataContext is AgentAttachment attachment)
            {
                attachments.Remove(attachment);
                attachmentStrip.IsVisible = attachments.Count > 0;
            }
        };
        return chip.Showing<AgentAttachment>(attachment => label.Text = attachment.DisplayPath);
    }

    private Control BuildConversationCard()
    {
        TextBlock title = new() { Classes = { StyleClasses.Strong, StyleClasses.Caption }, TextTrimming = TextTrimming.CharacterEllipsis };
        TextBlock thinking = new() { Classes = { StyleClasses.Muted, StyleClasses.Caption }, TextWrapping = TextWrapping.Wrap, IsVisible = false };
        TextBlock plainBody = new() { TextWrapping = TextWrapping.Wrap };
        StackPanel markdownBody = new() { Spacing = Gaps.Small, IsVisible = false };
        StackPanel bodyStack = new() { Spacing = Gaps.Tight, Children = { title, thinking, plainBody, markdownBody } };
        Border card = new()
        {
            BorderThickness = Hairlines.Round,
            Margin = new Thickness(0, 0, 0, Gaps.Normal),
            Padding = new Thickness(Gaps.Normal),
            Child = bodyStack,
        };
        card.Bind(Border.BorderBrushProperty, card.GetResourceObservable(TokenKeys.PopupBorder));
        card.Bind(Border.BackgroundProperty, card.GetResourceObservable(TokenKeys.List));
        card.Bind(Border.CornerRadiusProperty, card.GetResourceObservable(TokenKeys.ControlRadius));

        Action<Uri> follow = uri => browse?.Invoke(uri);
        ConversationItem? boundItem = null;
        int renderedRevision = -1;

        void SyncMarkdown()
        {
            if (boundItem is null || boundItem.Role != ConversationRole.Assistant)
            {
                return;
            }
            if (renderedRevision == boundItem.MarkdownRevision)
            {
                return;
            }
            renderedRevision = boundItem.MarkdownRevision;
            markdownBody.Children.Clear();
            string text = boundItem.Body;
            if (!string.IsNullOrWhiteSpace(text))
            {
                IReadOnlyList<Markdown.Block> blocks = Markdown.Parse(text);
                Markdown.Render(blocks, markdownBody, follow);
            }
            markdownBody.IsVisible = markdownBody.Children.Count > 0;
        }

        void OnItemPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ConversationItem.MarkdownRevision))
            {
                SyncMarkdown();
            }
        }

        return card.Showing<ConversationItem>(item =>
        {
            if (boundItem is not null)
            {
                boundItem.PropertyChanged -= OnItemPropertyChanged;
            }
            boundItem = item;
            renderedRevision = -1;
            boundItem.PropertyChanged += OnItemPropertyChanged;

            title.DataContext = item;
            bool isAssistant = item.Role == ConversationRole.Assistant;
            plainBody.IsVisible = !isAssistant;
            plainBody.Classes.Set(StyleClasses.Code, item.Role == ConversationRole.Tool);
            title.Bind(TextBlock.TextProperty, new Avalonia.Data.Binding(nameof(ConversationItem.Title)));
            plainBody.Bind(TextBlock.TextProperty, new Avalonia.Data.Binding(nameof(ConversationItem.Body)));
            thinking.Bind(TextBlock.TextProperty, new Avalonia.Data.Binding(nameof(ConversationItem.Thinking)));
            thinking.Bind(IsVisibleProperty, new Avalonia.Data.Binding(nameof(ConversationItem.HasThinking)));
            markdownBody.IsVisible = isAssistant && !string.IsNullOrWhiteSpace(item.Body);
            if (isAssistant)
            {
                SyncMarkdown();
            }
        });
    }

    private static string NewSessionId() => Guid.NewGuid().ToString("N")[..12];

    private enum ConversationRole
    {
        User,
        Assistant,
        Tool,
    }

    private sealed class ConversationItem : Observable
    {
        private readonly Debounce markdownDebounce;
        private string currentTitle;
        private string currentBody;
        private string thinking = string.Empty;
        private int markdownRevision;

        public ConversationItem(ConversationRole role, string title, string body)
        {
            Role = role;
            currentTitle = title;
            currentBody = body;
            markdownDebounce = new Debounce(TimeSpan.FromMilliseconds(90), () =>
            {
                FlushMarkdown();
                return Task.CompletedTask;
            });
        }

        public ConversationRole Role { get; }

        public string Title
        {
            get => currentTitle;
            set => Set(ref currentTitle, value);
        }

        public string Body
        {
            get => currentBody;
            private set => Set(ref currentBody, value);
        }

        public int MarkdownRevision
        {
            get => markdownRevision;
            private set => Set(ref markdownRevision, value);
        }

        public string Thinking
        {
            get => thinking;
            private set
            {
                if (Set(ref thinking, value))
                {
                    Raise(nameof(HasThinking));
                }
            }
        }

        public bool HasThinking => thinking.Length > 0;

        public void AppendBody(string delta)
        {
            bool wasEmpty = Body.Length == 0;
            Body += delta;
            if (Role == ConversationRole.Assistant)
            {
                if (wasEmpty)
                {
                    FlushMarkdown();
                }
                else
                {
                    markdownDebounce.Restart();
                }
            }
        }

        public void SetBody(string value)
        {
            Body = value;
            if (Role == ConversationRole.Assistant)
            {
                FlushMarkdown();
            }
        }

        public void FlushMarkdown()
        {
            markdownDebounce.Stop();
            MarkdownRevision++;
        }

        public void AppendThinking(string delta) => Thinking += delta;
    }
}
