using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Passenger.Agent;
using Passenger.App;
using Passenger.App.Agent;
using Passenger.App.ToolWindows;
using Passenger.Core;

namespace Passenger.Tests;

/// <summary>Verifies Pi Agent's workspace boundary, attachment safety, protocol switching,
/// settings dialog layout, and tool-window isolation to the open folder.</summary>
public sealed class PiAgentTests : IDisposable
{
    private readonly string workspace = TestFolders.Create("pi-agent-ws-");
    private readonly string outside = TestFolders.Create("pi-agent-out-");

    public void Dispose()
    {
        TestFolders.Delete(workspace);
        TestFolders.Delete(outside);
    }

    [Fact]
    public void Workspace_guard_blocks_paths_outside_the_open_folder_and_mutating_calls_when_untrusted()
    {
        string insideFile = Path.Combine(workspace, "Program.cs");
        string outsideFile = Path.Combine(outside, "secret.txt");
        File.WriteAllText(insideFile, "class Program {}\n");
        File.WriteAllText(outsideFile, "top secret\n");

        Assert.Null(WorkspaceScopedTools.CheckCall("read", new { path = "Program.cs" }, workspace));
        Assert.Null(WorkspaceScopedTools.CheckCall("read", new { path = insideFile }, workspace));
        Assert.NotNull(WorkspaceScopedTools.CheckCall("read", new { path = "../secret.txt" }, workspace));
        Assert.NotNull(WorkspaceScopedTools.CheckCall("read", new { path = outsideFile }, workspace));
        Assert.NotNull(WorkspaceScopedTools.CheckCall("write_file", new { path = outsideFile, content = "x" }, workspace));
        Assert.NotNull(WorkspaceScopedTools.CheckCall("mcp__windows__Click", new { }, workspace));

        using (WorkspaceTrust.Ask(workspace))
        {
            Assert.False(WorkspaceTrust.Trusts(workspace));
            Assert.Null(WorkspaceScopedTools.CheckCall("read", new { path = "Program.cs" }, workspace));
            Assert.Equal(WorkspaceTrust.Untrusted, WorkspaceScopedTools.CheckCall("edit", new { path = "Program.cs" }, workspace));
            Assert.Equal(WorkspaceTrust.Untrusted, WorkspaceScopedTools.CheckCall("write_file", new { path = "a.txt", content = "hi" }, workspace));
            Assert.Equal(WorkspaceTrust.Untrusted, WorkspaceScopedTools.CheckCall("run_command", new { command = "echo hi" }, workspace));
        }
    }

    [Fact]
    public void Workspace_attachments_load_workspace_files_folders_and_images_safely()
    {
        string code = Path.Combine(workspace, "src", "App.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(code)!);
        File.WriteAllText(code, "Console.WriteLine(42);\n");
        string png = Path.Combine(workspace, "shot.png");
        File.WriteAllBytes(png, [137, 80, 78, 71, 13, 10, 26, 10]);

        AgentAttachment file = WorkspaceAttachments.LoadFile(code, workspace);
        AgentAttachment folder = WorkspaceAttachments.LoadFolder(Path.Combine(workspace, "src"), workspace);
        AgentAttachment image = WorkspaceAttachments.LoadImage(png, workspace);

        Assert.Null(file.Error);
        Assert.Equal("src/App.cs", file.DisplayPath);
        Assert.Contains("Console.WriteLine(42);", file.Text, StringComparison.Ordinal);
        Assert.Null(folder.Error);
        Assert.Contains("App.cs", folder.Text, StringComparison.Ordinal);
        Assert.Null(image.Error);
        Assert.Equal(AgentAttachmentKind.Image, image.Kind);
        Assert.Equal("image/png", image.MimeType);

        string composed = WorkspaceAttachments.ComposePrompt("Explain this", [file, folder, image]);
        Assert.Contains("<attached_context>", composed, StringComparison.Ordinal);
        Assert.Contains("src/App.cs", composed, StringComparison.Ordinal);
        Assert.Contains("Explain this", composed, StringComparison.Ordinal);
    }

    [Fact]
    public void Protocol_catalog_covers_anthropic_openai_responses_and_openai_compatible()
    {
        Assert.Equal(3, AgentProtocolCatalog.All.Count);
        Assert.Equal("anthropic-messages", AgentProtocolCatalog.ApiId(AgentProtocol.AnthropicMessages));
        Assert.Equal("openai-responses", AgentProtocolCatalog.ApiId(AgentProtocol.OpenAiResponses));
        Assert.Equal("openai-completions", AgentProtocolCatalog.ApiId(AgentProtocol.OpenAiCompatible));

        PiAgentConfig config = new PiAgentConfig
        {
            Models =
            [
                AgentModelProfile.Create(AgentProviderKind.Anthropic),
                AgentModelProfile.Create(AgentProviderKind.OpenAI),
                AgentModelProfile.Create(AgentProviderKind.DeepSeek),
            ],
            Temperature = 9.0,
            MaxTokens = 10,
        }.Clamped();

        Assert.Equal(2.0, config.Temperature);
        Assert.Equal(256, config.MaxTokens);
        Assert.Equal(AgentProtocol.AnthropicMessages, config.ActiveModel!.Protocol);
    }

    [AvaloniaFact]
    public void Pi_agent_tool_window_and_settings_dialog_follow_ui_and_workspace_rules()
    {
        File.WriteAllText(Path.Combine(workspace, "Inside.cs"), "// inside\n");
        File.WriteAllText(Path.Combine(outside, "Outside.cs"), "// outside\n");

        MainWindow window = Ui.OpenFolder(workspace, languageServer: false);
        try
        {
            Assert.True(window.Perform(AppActions.ShowPiAgent));
            Ui.Settle();

            PiAgentView view = window.PiAgentView;
            Assert.Equal(AgentProtocol.OpenAiResponses, view.Config.ActiveModel!.Protocol);

            view.SwitchProtocol(AgentProtocol.AnthropicMessages);
            Assert.Equal(AgentProtocol.AnthropicMessages, view.Config.ActiveModel!.Protocol);

            view.SwitchProtocol(AgentProtocol.OpenAiCompatible);
            Assert.Equal(AgentProtocol.OpenAiCompatible, view.Config.ActiveModel!.Protocol);

            // Attachments outside the open folder are rejected.
            Assert.False(view.AttachPath(Path.Combine(outside, "Outside.cs")));
            Assert.True(view.AttachPath(Path.Combine(workspace, "Inside.cs")));
            Assert.Single(view.Attachments);

            // Settings dialog passes DialogButtonsTests layout, alignment, and label rules.
            PiAgentSettingsDialog dialog = new(view.Config, new PiAgentRuntime());
            dialog.Show(window);
            Ui.Settle();
            DialogButtonsTests.Check(dialog).Close();
        }
        finally
        {
            window.Close();
        }
    }
}
