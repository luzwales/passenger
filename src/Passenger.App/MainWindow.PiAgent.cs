using Passenger.Agent;
using Passenger.App.Agent;
using Passenger.App.State;

namespace Passenger.App;

/// <summary>Hosts the Pi Agent tool window in the right-bottom dock and binds its workspace, trust state,
/// and model/protocol configuration to the user's state store.</summary>
public partial class MainWindow
{
    private readonly PiAgentRuntime piAgentRuntime = new();
    private ToolWindows.PiAgentView? piAgentView;
    private PiAgentConfig piAgentConfig = new PiAgentConfig { Models = [AgentModelProfile.Create(AgentProviderKind.OpenAI)] }.Clamped();

    public ToolWindows.PiAgentView PiAgentView => piAgentView ?? (ToolWindows.PiAgentView)Dock[ToolWindowIds.PiAgent].Content;

    private void AddPiAgent()
    {
        Dock.Add(new Docking.ToolWindow(ToolWindowIds.PiAgent, "Pi Agent", IconKeys.PiAgent, MakePiAgent, Docking.ToolAnchor.RightBottom)
        {
            Serves = ToolWindowServes.EveryFolder,
            Action = AppActions.ShowPiAgent,
            Menus = [new MenuPlace("Tools", 0, 3)],
            TakesKeysAsShown = true,
        });
        actions!.Add(new IdeAction(AppActions.PiAgentNewSession, "New Pi Agent Session", "Pi Agent", _ =>
        {
            PiAgentView.StartNewSession();
            ShowToolWindow(ToolWindowIds.PiAgent);
        }));
        actions.Add(new IdeAction(AppActions.PiAgentSettings, "Pi Agent Settings…", "Pi Agent", _ => IdeErrors.Observe(EditPiAgentSettingsAsync())));
        if (keepsWorkspace)
        {
            IdeErrors.Observe(LoadPiAgentConfigAsync());
        }
    }

    private ToolWindows.PiAgentView MakePiAgent()
    {
        ToolWindows.PiAgentView view = piAgentView = new ToolWindows.PiAgentView(
            piAgentRuntime,
            () => session.Folder,
            () => session.Trusted);
        view.ApplyConfig(piAgentConfig, persist: false);
        view.ActionRequested += (_, action) => Request(action);
        view.ConfigChanged += (_, next) => SavePiAgentConfig(next);
        return view;
    }

    private async Task LoadPiAgentConfigAsync()
    {
        PiAgentState loaded = await Store().PiAgent.ValueAsync();
        piAgentConfig = loaded.ToConfig();
        piAgentView?.ApplyConfig(piAgentConfig, persist: false);
    }

    private void SavePiAgentConfig(PiAgentConfig next)
    {
        piAgentConfig = next.Clamped();
        if (keepsWorkspace)
        {
            Store().PiAgent.Set(PiAgentState.FromConfig(piAgentConfig));
        }
    }

    private async Task EditPiAgentSettingsAsync()
    {
        PiAgentSettingsDialog dialog = new(piAgentConfig, piAgentRuntime);
        if (await dialog.ShowDialog<bool>(this) && dialog.Result is { } updated)
        {
            SavePiAgentConfig(updated);
            piAgentView?.ApplyConfig(piAgentConfig, persist: false);
        }
    }

    private void FollowPiAgentWorkspace()
    {
        piAgentRuntime.InvalidateSessions();
        piAgentView?.RefreshWorkspaceState();
    }
}
