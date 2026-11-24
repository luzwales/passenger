using Passenger.Agent;
using Passenger.App.Agent;

namespace Passenger.App;

/// <summary>Pi Agent tool window: embedded workspace coding assistant docked at the bottom-right with shared ~/.pi/agent/ config and sessions.</summary>
public partial class MainWindow
{
    private readonly PiAgentRuntime piAgentRuntime = new();
    private ToolWindows.PiAgentView? piAgent;
    private PiAgentConfig piAgentConfig = new PiAgentConfig { Models = [AgentModelProfile.Create(AgentProviderKind.OpenAI)] }.Clamped();
    private bool piAgentSidebarOpen;

    public ToolWindows.PiAgentView PiAgentView => piAgent ?? (ToolWindows.PiAgentView)Dock[ToolWindowIds.PiAgent].Content;

    private void AddPiAgent()
    {
        LoadPiAgentState();
        Dock.Add(new Docking.ToolWindow(ToolWindowIds.PiAgent, "Pi Agent", IconKeys.PiAgent, MakePiAgent, Docking.ToolAnchor.RightBottom)
        {
            Serves = ToolWindowServes.EveryFolder,
            Action = AppActions.ShowPiAgent,
            Tools = () => piAgent?.HeaderTools,
            Menus = [new MenuPlace("Tools", 0, 2)],
        });
        actions!.Add(new IdeAction(AppActions.PiAgentNewSession, "New Pi Agent Session", "Pi Agent", _ =>
        {
            ShowToolWindow(ToolWindowIds.PiAgent);
            PiAgentView.StartNewSession();
        }));
        actions!.Add(new IdeAction(AppActions.PiAgentSettings, "Provider Settings…", "Pi Agent", _ => IdeErrors.Observe(ShowPiAgentSettingsAsync())));
    }

    private ToolWindows.PiAgentView MakePiAgent()
    {
        ToolWindows.PiAgentView view = piAgent = new ToolWindows.PiAgentView(piAgentRuntime, () => session.Folder, () => session.Trusted);
        view.ApplyConfig(piAgentConfig, persist: false);
        view.SetSidebarOpen(piAgentSidebarOpen, notify: false);
        view.ActionRequested += (_, action) => Request(action);
        view.ConfigChanged += (_, updated) => SavePiAgentConfig(updated);
        view.SidebarToggled += (_, open) =>
        {
            piAgentSidebarOpen = open;
            if (keepsWorkspace && state is not null)
            {
                state.PiAgent.Set(state.PiAgent.Value with { SidebarOpen = open });
            }
        };
        return view;
    }

    private void LoadPiAgentState()
    {
        if (keepsWorkspace && state is not null)
        {
            PiAgentState kept = state.PiAgent.Value;
            piAgentConfig = kept.ToConfig();
            piAgentSidebarOpen = kept.SidebarOpen;
        }
        piAgentConfig = PiAgentModelsSync.LoadSharedConfig(piAgentConfig);
    }

    private void SavePiAgentConfig(PiAgentConfig updated)
    {
        piAgentConfig = updated.Clamped();
        if (keepsWorkspace && state is not null)
        {
            state.PiAgent.Set(PiAgentState.FromConfig(piAgentConfig, piAgentSidebarOpen));
            PiAgentModelsSync.SaveSharedConfig(piAgentConfig);
        }
    }

    private async Task ShowPiAgentSettingsAsync()
    {
        if (AgentAsks.Refused())
        {
            Say(Refusals.AsksThePerson);
            return;
        }
        PiAgentSettingsDialog dialog = new(piAgentConfig, piAgentRuntime);
        if (await dialog.ShowDialog<bool>(this) && dialog.Result is { } updated)
        {
            SavePiAgentConfig(updated);
            piAgent?.ApplyConfig(piAgentConfig, persist: false);
        }
    }

    private void FollowPiAgentWorkspace()
    {
        piAgentRuntime.InvalidateSessions();
        piAgent?.RefreshWorkspaceState();
    }
}
