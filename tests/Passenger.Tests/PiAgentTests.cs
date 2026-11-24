using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Passenger.Agent;
using Passenger.App;
using Passenger.App.Agent;
using Passenger.App.ToolWindows;
using Passenger.Core;
using Pi.Ai.Types;
using Pi.CodingAgent.Core;

namespace Passenger.Tests;

public sealed class PiAgentTests
{
    [Fact]
    public void Workspace_guard_blocks_paths_outside_open_folder_and_mutating_tools_when_untrusted()
    {
        string temp = TestFolders.Create("pi-guard");
        string inside = Path.Combine(temp, "src", "Program.cs");
        string outside = Path.GetFullPath(Path.Combine(temp, "..", "outside.txt"));

        Assert.Null(WorkspaceScopedTools.CheckCall("read", new { path = inside }, temp));
        Assert.NotNull(WorkspaceScopedTools.CheckCall("read", new { path = outside }, temp));
        Assert.NotNull(WorkspaceScopedTools.CheckCall("mcp__github__search", new { query = "x" }, temp));

        string? untrustedEdit;
        using (WorkspaceTrust.Ask(temp))
        {
            untrustedEdit = WorkspaceScopedTools.CheckCall("edit", new { path = inside }, temp);
        }
        Assert.Equal(WorkspaceTrust.Untrusted, untrustedEdit);
    }

    [Fact]
    public void Attachments_read_workspace_files_and_folders_and_reject_outside_paths()
    {
        string temp = TestFolders.Create("pi-attach");
        string file = Path.Combine(temp, "hello.cs");
        File.WriteAllText(file, "class A {}\n");

        AgentAttachment loadedFile = WorkspaceAttachments.LoadFile(file, temp);
        Assert.Null(loadedFile.Error);
        Assert.Equal("hello.cs", loadedFile.DisplayPath);
        Assert.Contains("class A {}", loadedFile.Text);

        AgentAttachment loadedFolder = WorkspaceAttachments.LoadFolder(temp, temp);
        Assert.Null(loadedFolder.Error);
        Assert.Contains("hello.cs", loadedFolder.Text);

        string outside = Path.GetFullPath(Path.Combine(temp, "..", "escape.txt"));
        AgentAttachment rejected = WorkspaceAttachments.LoadFile(outside, temp);
        Assert.NotNull(rejected.Error);
    }

    [Fact]
    public void Multi_model_endpoints_and_shared_models_json_sync_per_model_thinking_and_tokens()
    {
        string agentDir = TestFolders.Create("pi-models");
        AgentModelEntry flash = AgentModelEntry.Create("gemini-3.8-flash", "Gemini 3.8 Flash", AgentThinkingLevel.Low, maxTokens: 4096, contextWindow: 500_000);
        AgentModelEntry hydrogen = AgentModelEntry.Create("gemini-4-hydrogen", "Gemini 4 Hydrogen", AgentThinkingLevel.High, maxTokens: 32_000, contextWindow: 1_048_576);
        AgentProviderEndpoint router = new()
        {
            Id = "prv-router",
            Name = "router",
            Provider = AgentProviderKind.Custom,
            Protocol = AgentProtocol.OpenAiCompatible,
            BaseUrl = "http://localhost:20128/v1",
            ApiKey = "sk-test",
            Models = [flash, hydrogen],
        };
        PiAgentConfig initial = new PiAgentConfig
        {
            Providers = [router],
            ActiveProviderId = router.Id,
            ActiveModelEntryId = hydrogen.Id,
        }.Clamped();

        PiAgentModelsSync.SaveSharedConfig(initial, agentDir);
        PiAgentConfig reloaded = PiAgentModelsSync.LoadSharedConfig(new PiAgentConfig(), agentDir);

        AgentProviderEndpoint? loadedRouter = reloaded.Providers.FirstOrDefault(p => p.Name == "router");
        Assert.NotNull(loadedRouter);
        Assert.Equal(2, loadedRouter.Models.Count);
        AgentModelEntry? loadedHydrogen = loadedRouter.Models.FirstOrDefault(m => m.ModelId == "gemini-4-hydrogen");
        Assert.NotNull(loadedHydrogen);
        Assert.Equal(AgentThinkingLevel.High, loadedHydrogen.ThinkingLevel);
        Assert.Equal(32_000, loadedHydrogen.MaxTokens);
        Assert.Equal(1_048_576, loadedHydrogen.ContextWindow);
    }

    [Fact]
    public async Task Shared_session_store_lists_and_restores_transcript_from_pi_sessions()
    {
        string workspace = TestFolders.Create("pi-ws");
        string agentDir = TestFolders.Create("pi-agentdir");
        string sessionDir = PiAgentSessionStore.SessionDirectoryFor(workspace, agentDir);
        Directory.CreateDirectory(sessionDir);
        SessionManager manager = SessionManager.Create(workspace, sessionDir);
        manager.AppendMessage(Messages.UserText("Explain Program.cs"));
        manager.AppendMessage(Messages.AssistantText("Program.cs is the entry point."));
        manager.AppendSessionInfo("Inspect Program");

        IReadOnlyList<PiAgentSessionSummary> list = await PiAgentSessionStore.ListAsync(workspace, agentDir, TestContext.Current.CancellationToken);
        PiAgentSessionSummary summary = Assert.Single(list);
        Assert.Equal("Inspect Program", summary.Title);

        IReadOnlyList<PiAgentMessageRecord> transcript = PiAgentSessionStore.LoadTranscript(summary.FilePath, workspace);
        Assert.Equal(2, transcript.Count);
        Assert.Equal(PiAgentMessageKind.User, transcript[0].Kind);
        Assert.Equal("Explain Program.cs", transcript[0].Body);
        Assert.Equal(PiAgentMessageKind.Assistant, transcript[1].Kind);
        Assert.Equal("Program.cs is the entry point.", transcript[1].Body);
    }

    [AvaloniaFact]
    public void View_switches_protocols_models_thinking_levels_and_sidebar_while_blocking_outside_attachments()
    {
        string temp = TestFolders.Create("pi-view");
        string inside = Path.Combine(temp, "note.txt");
        File.WriteAllText(inside, "hello");
        string outside = Path.GetFullPath(Path.Combine(temp, "..", "outside.txt"));

        PiAgentRuntime runtime = new();
        PiAgentView view = new(runtime, () => temp, () => true);

        view.SwitchProtocol(AgentProtocol.AnthropicMessages);
        Assert.Equal(AgentProtocol.AnthropicMessages, view.Config.ActiveModel!.Protocol);

        view.SetThinkingLevel(AgentThinkingLevel.XHigh);
        Assert.Equal(AgentThinkingLevel.XHigh, view.Config.ActiveModel!.ThinkingLevel);

        view.SetSidebarOpen(false, notify: false);
        Assert.False(view.IsSidebarOpen);
        view.SetSidebarOpen(true, notify: false);
        Assert.True(view.IsSidebarOpen);

        Assert.True(view.AttachPath(inside));
        Assert.False(view.AttachPath(outside));
        Assert.Single(view.Attachments);
    }

    [AvaloniaFact]
    public void Settings_dialog_buttons_pass_layout_rules()
    {
        PiAgentConfig config = new PiAgentConfig
        {
            Providers =
            [
                new AgentProviderEndpoint
                {
                    Id = "prv-test",
                    Name = "router",
                    Models =
                    [
                        AgentModelEntry.Create("first-model", "First Model", AgentThinkingLevel.Low, 4096, 128_000),
                        AgentModelEntry.Create("second-model", "Second Model", AgentThinkingLevel.High, 16384, 512_000),
                    ],
                },
            ],
            ActiveProviderId = "prv-test",
            ActiveModelEntryId = null,
        }.Clamped();

        PiAgentSettingsDialog dialog = new(config, new PiAgentRuntime());
        dialog.Show();
        try
        {
            Assert.Equal("Provider Settings", dialog.Title);
            dialog.SelectModel(1);
            Assert.Equal(1, dialog.SelectedModelIndex);
            DialogButtonsTests.Check(dialog);
        }
        finally
        {
            dialog.Close(false);
        }
    }
}
