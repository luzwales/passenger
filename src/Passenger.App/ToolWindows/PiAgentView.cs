using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Passenger.Agent;
using Passenger.App.Docking;
using Passenger.App.Settings;
using Passenger.Core;
using Passenger.Editor;

namespace Passenger.App.ToolWindows;

/// <summary>Docked Pi Agent panel: protocol/model switching, workspace-scoped file/folder/image attachments,
/// streaming response &amp; tool execution feed, strictly confined to the open workspace folder.</summary>
public sealed class PiAgentView : DockPanel
{
    private readonly PiAgentRuntime runtime;
    private readonly Func<string?> folderProvider;
    private readonly Func<bool> trustedProvider;
    private readonly ObservableCollection<ConversationItem> items = [];
    private readonly ObservableCollection<AgentAttachment> attachments = [];
    private readonly ToolDropDown profileBox = new("Select model");
    private readonly ToolDropDown protocolBox = new("Protocol");
    private readonly TextBlock workspaceBadge = new() { Classes = { StyleClasses.Caption, StyleClasses.Muted }, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly Banner trustNotice = new();
    private readonly ItemsControl feed = new() { Margin = new Thickness(Gaps.Normal) };
    private readonly ScrollViewer feedScroll = new() { VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
    private readonly EmptyState empty = new("Ask Pi Agent about this workspace. Reads, searches, and edits stay inside the open folder.");
    private readonly ItemsControl attachmentStrip = new() { IsVisible = false };
    private readonly TextBox composer = new()
    {
        AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap,
        MinHeight = 64,
        MaxHeight = 140,
        PlaceholderText = "Ask Pi Agent to inspect, explain, or edit files in this workspace…",
    };
    private readonly Button attachFileButton;
    private readonly Button attachFolderButton;
    private readonly Button attachImageButton;
    private readonly Button sendButton;
    private readonly Button stopButton;
    private readonly Button newSessionButton;
    private readonly Button settingsButton;
    private readonly TextBlock turnStatus = new() { Classes = { StyleClasses.Caption, StyleClasses.Muted }, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };

    private PiAgentConfig config = new PiAgentConfig { Models = [AgentModelProfile.Create(AgentProviderKind.OpenAI)] }.Clamped();
    private string sessionId = NewSessionId();
    private CancellationTokenSource? activeTurn;
    private bool syncingSelectors;

    public PiAgentView(PiAgentRuntime runtime, Func<string?> folderProvider, Func<bool> trustedProvider)
    {
        this.runtime = runtime;
        this.folderProvider = folderProvider;
        this.trustedProvider = trustedProvider;

        profileBox.MaxDropDownHeight = 260;
        protocolBox.MaxDropDownHeight = 200;
        AutomationProperties.SetName(profileBox, "Agent Model");
        AutomationProperties.SetName(protocolBox, "Agent Protocol");
        AutomationProperties.SetName(composer, "Pi Agent Prompt");

        profileBox.SelectionChanged += (_, _) => OnProfileChanged();
        protocolBox.SelectionChanged += (_, _) => OnProtocolChanged();

        ReusedRows.Use(profileBox, () =>
        {
            TextBlock label = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            return label.Showing<AgentModelProfile>(p => label.Text = p.DisplayName);
        });
        ReusedRows.Use(protocolBox, () =>
        {
            TextBlock label = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            return label.Showing<AgentProtocolOption>(p => label.Text = AgentProtocolCatalog.ShortLabel(p.Kind));
        });
        protocolBox.ItemsSource = AgentProtocolCatalog.All;

        attachFileButton = ToolButtons.Small(IconKeys.DocumentText, "Attach File…", () => IdeErrors.Observe(PickFilesAsync()));
        attachFolderButton = ToolButtons.Small(IconKeys.Explorer, "Attach Folder…", () => IdeErrors.Observe(PickFolderAsync()));
        attachImageButton = ToolButtons.Small(IconKeys.Image, "Attach Image…", () => IdeErrors.Observe(PickImagesAsync()));
        newSessionButton = ToolButtons.Of(AppActions.PiAgentNewSession, IconKeys.Add, "New Session", action => ActionRequested?.Invoke(this, action));
        settingsButton = ToolButtons.Of(AppActions.PiAgentSettings, IconKeys.Settings, "Pi Agent Settings…", action => ActionRequested?.Invoke(this, action));

        sendButton = new Button
        {
            Content = "Send",
            Classes = { StyleClasses.Primary },
        };
        AutomationProperties.SetName(sendButton, "Send Prompt");
        sendButton.Click += (_, _) => IdeErrors.Observe(SendAsync());

        stopButton = new Button
        {
            Content = "Stop",
            IsVisible = false,
        };
        AutomationProperties.SetName(stopButton, "Stop Agent");
        stopButton.Click += (_, _) => AbortCurrentTurn();

        composer.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && (e.KeyModifiers == KeyModifiers.None || e.KeyModifiers.HasFlag(Keystroke.Command)))
            {
                if (e.KeyModifiers != KeyModifiers.Shift)
                {
                    e.Handled = true;
                    IdeErrors.Observe(SendAsync());
                }
            }
        };

        ToolFlow topFlow = new()
        {
            Margin = new Thickness(Gaps.Normal, Gaps.Tight),
            Spacing = Gaps.Small,
            Children = { profileBox, protocolBox, newSessionButton, settingsButton, workspaceBadge },
        };
        Control topBar = ToolBars.Top(topFlow);
        DockPanel.SetDock(topBar, Dock.Top);

        DockPanel.SetDock(trustNotice, Dock.Top);

        feed.ItemsSource = items;
        ReusedRows.Use(feed, BuildConversationCard);
        feedScroll.Content = feed;
        Panel feedHost = new() { Children = { feedScroll, empty } };

        attachmentStrip.ItemsSource = attachments;
        attachmentStrip.ItemsPanel = new Avalonia.Controls.Templates.FuncTemplate<Panel?>(() => new ToolFlow { Spacing = Gaps.Tight });
        ReusedRows.Use(attachmentStrip, BuildAttachmentChip);

        ToolFlow composerActions = new()
        {
            Spacing = Gaps.Small,
            Children = { attachFileButton, attachFolderButton, attachImageButton, sendButton, stopButton, turnStatus },
        };
        StackPanel composerBox = new()
        {
            Margin = new Thickness(Gaps.Normal),
            Spacing = Gaps.Small,
            Children = { attachmentStrip, composer, composerActions },
        };
        Border composerPanel = new()
        {
            Classes = { StyleClasses.Framed },
            Margin = new Thickness(Gaps.Normal, 0, Gaps.Normal, Gaps.Normal),
            Child = composerBox,
        };
        DockPanel.SetDock(composerPanel, Dock.Bottom);

        Children.Add(topBar);
        Children.Add(trustNotice);
        Children.Add(composerPanel);
        Children.Add(feedHost);

        ApplyConfig(config, persist: false);
        RefreshWorkspaceState();
    }

    public event EventHandler<string>? ActionRequested;
    public event EventHandler<PiAgentConfig>? ConfigChanged;

    public string? PromptDraft
    {
        get => composer.Text;
        set => composer.Text = value ?? string.Empty;
    }

    public PiAgentConfig Config => config;

    public IReadOnlyList<AgentAttachment> Attachments => attachments;

    public void SwitchProtocol(AgentProtocol protocol)
    {
        protocolBox.SelectedItem = AgentProtocolCatalog.For(protocol);
        OnProtocolChanged();
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

    public void ApplyConfig(PiAgentConfig next, bool persist = false)
    {
        config = next.Clamped();
        syncingSelectors = true;
        try
        {
            profileBox.ItemsSource = config.Models.ToList();
            AgentModelProfile? active = config.ActiveModel ?? config.Models.FirstOrDefault();
            profileBox.SelectedItem = active;
            protocolBox.SelectedItem = AgentProtocolCatalog.For(active?.Protocol ?? AgentProtocol.OpenAiCompatible);
        }
        finally
        {
            syncingSelectors = false;
        }
        if (persist)
        {
            ConfigChanged?.Invoke(this, config);
        }
    }

    public void RefreshWorkspaceState()
    {
        string? folder = folderProvider();
        bool hasFolder = !string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder);
        bool trusted = hasFolder && trustedProvider();
        workspaceBadge.Text = hasFolder ? Path.GetFileName(folder) : "No workspace";
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
        UpdateComposerState();
    }

    public void StartNewSession()
    {
        AbortCurrentTurn();
        sessionId = NewSessionId();
        items.Clear();
        attachments.Clear();
        attachmentStrip.IsVisible = false;
        empty.IsVisible = true;
        turnStatus.Text = "New session";
        runtime.InvalidateSessions();
        UpdateComposerState();
    }

    private void OnProfileChanged()
    {
        if (syncingSelectors || profileBox.SelectedItem is not AgentModelProfile selected)
        {
            return;
        }
        PiAgentConfig updated = (config with { ActiveModelId = selected.Id }).Clamped();
        ApplyConfig(updated, persist: true);
    }

    private void OnProtocolChanged()
    {
        if (syncingSelectors || protocolBox.SelectedItem is not AgentProtocolOption chosen || config.ActiveModel is not { } active)
        {
            return;
        }
        List<AgentModelProfile> list = [.. config.Models];
        int index = list.FindIndex(m => m.Id == active.Id);
        if (index < 0)
        {
            return;
        }
        list[index] = (list[index] with { Protocol = chosen.Kind }).Clamped();
        ApplyConfig((config with { Models = list, ActiveModelId = active.Id }).Clamped(), persist: true);
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
            turnStatus.Text = "Configure API Key in Pi Agent Settings";
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

        AgentRunRequest request = new(sessionId, composedPrompt, folder, history, sentImages);
        Dictionary<string, ConversationItem> toolCards = new(StringComparer.Ordinal);
        try
        {
            await foreach (AgentStreamEvent ev in runtime.RunAsync(request, config, cancel))
            {
                switch (ev)
                {
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
                        if (!string.IsNullOrWhiteSpace(done.Error))
                        {
                            assistant.AppendBody((assistant.Body.Length > 0 ? "\n\n" : string.Empty) + done.Error);
                        }
                        turnStatus.Text = done.StopReason == AgentStopReason.Aborted
                            ? "Stopped"
                            : $"{done.Model} · {done.InputTokens}+{done.OutputTokens} tok";
                        break;
                    case AgentStreamEvent.RunFailed failed:
                        assistant.AppendBody((assistant.Body.Length > 0 ? "\n\n" : string.Empty) + "Error: " + failed.Message);
                        turnStatus.Text = "Failed";
                        break;
                }
            }
        }
        finally
        {
            activeTurn.Dispose();
            activeTurn = null;
            UpdateComposerState();
            ScrollToEnd();
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
        attachFileButton.IsEnabled = !running && hasFolder;
        attachFolderButton.IsEnabled = !running && hasFolder;
        attachImageButton.IsEnabled = !running && hasFolder;
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

    private Control BuildAttachmentChip()
    {
        TextBlock label = new() { Classes = { StyleClasses.Caption }, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 180 };
        Button remove = ToolButtons.Small(IconKeys.Close, "Remove Attachment", () => { });
        StackPanel row = new() { Orientation = Orientation.Horizontal, Spacing = Gaps.Tight, Children = { label, remove } };
        Border chip = new()
        {
            Classes = { StyleClasses.Framed },
            Padding = new Thickness(Gaps.Small, Gaps.Hair),
            Child = row,
        };
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

    private static Control BuildConversationCard()
    {
        TextBlock title = new() { Classes = { StyleClasses.Strong, StyleClasses.Caption }, TextTrimming = TextTrimming.CharacterEllipsis };
        TextBlock thinking = new() { Classes = { StyleClasses.Muted, StyleClasses.Caption }, TextWrapping = TextWrapping.Wrap, IsVisible = false };
        TextBlock body = new() { TextWrapping = TextWrapping.Wrap };
        StackPanel bodyStack = new() { Spacing = Gaps.Tight, Children = { title, thinking, body } };
        Border card = new()
        {
            Classes = { StyleClasses.Framed },
            Margin = new Thickness(0, 0, 0, Gaps.Normal),
            Padding = new Thickness(Gaps.Normal),
            Child = bodyStack,
        };
        return card.Showing<ConversationItem>(item =>
        {
            title.DataContext = item;
            body.Classes.Set(StyleClasses.Code, item.Role == ConversationRole.Tool);
            title.Bind(TextBlock.TextProperty, new Avalonia.Data.Binding(nameof(ConversationItem.Title)));
            body.Bind(TextBlock.TextProperty, new Avalonia.Data.Binding(nameof(ConversationItem.Body)));
            thinking.Bind(TextBlock.TextProperty, new Avalonia.Data.Binding(nameof(ConversationItem.Thinking)));
            thinking.Bind(IsVisibleProperty, new Avalonia.Data.Binding(nameof(ConversationItem.HasThinking)));
        });
    }

    private static string NewSessionId() => Guid.NewGuid().ToString("N")[..12];

    private enum ConversationRole
    {
        User,
        Assistant,
        Tool,
    }

    private sealed class ConversationItem(ConversationRole role, string title, string body) : Observable
    {
        private string currentTitle = title;
        private string currentBody = body;
        private string thinking = string.Empty;

        public ConversationRole Role { get; } = role;

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

        public void AppendBody(string delta) => Body += delta;

        public void SetBody(string value) => Body = value;

        public void AppendThinking(string delta) => Thinking += delta;
    }
}
