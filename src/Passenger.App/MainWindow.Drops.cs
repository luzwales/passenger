using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using Passenger.Editor;
using Passenger.Terminal;
using Passenger.Text;
using Passenger.Workspace;

namespace Passenger.App;

/// <summary>Files dropped on the window from the system's file manager (Files, Finder, Explorer): a folder, solution or
/// project opens as Open Solution and Open Folder open one, any other file in the editor of the window it was dropped
/// on, and on the terminal each path is typed for the shell.</summary>
public partial class MainWindow
{
    private void AcceptDrops(Window window)
    {
        DragDrop.SetAllowDrop(window, true);
        window.AddHandler(DragDrop.DragOverEvent, OnDragOver, RoutingStrategies.Bubble);
        window.AddHandler(DragDrop.DropEvent, (sender, e) => OnDrop(window, e), RoutingStrategies.Bubble);
    }

    private static void OnDragOver(object? sender, DragEventArgs e) =>
        e.DragEffects = e.DataTransfer.Contains(DataFormat.File) || e.DataTransfer.Contains(DataFormat.Text)
            ? DragDropEffects.Copy
            : DragDropEffects.None;

    private void OnDrop(Window window, DragEventArgs e)
    {
        if (!e.DataTransfer.Contains(DataFormat.File)
            && e.DataTransfer.TryGetText() is { Length: > 0 } droppedText
            && (e.Source as Visual)?.FindAncestorOfType<Editor.CodeEditor>(includeSelf: true) is { } editor
            && Editors.Active is { IsReadOnly: false })
        {
            e.Handled = true;
            e.DragEffects = DragDropEffects.Copy;
            EditPlan plan = TextEditing.Insert(editor.Document.Snapshot, editor.Selections, droppedText);
            editor.ApplyEdits(plan.Edits, plan.Selections);
            editor.Focus();
            if (Editors.Active is { IsReadOnly: false, Path: var activePath }
                && activePath.EndsWith(".linq", StringComparison.OrdinalIgnoreCase)
                && linqConnectionsView?.SelectedConnection is { } conn)
            {
                EnsureLinqConnectionHeader(editor, conn);
            }
            return;
        }
        List<string> paths = [.. (e.DataTransfer.TryGetFiles() ?? []).Select(item => item.TryGetLocalPath()).OfType<string>()];
        if (paths.Count == 0)
        {
            return;
        }
        e.Handled = true;
        e.DragEffects = DragDropEffects.Copy;
        if ((e.Source as Visual)?.FindAncestorOfType<TerminalView>(includeSelf: true) is { Session: { } shell } terminal)
        {
            terminal.Focus();
            shell.Paste(string.Join(' ', paths.Select(path => ShellWords.Quoted(path, shell.Shell))) + " ");
            return;
        }
        Editors.Front(window);
        string? workspace = null;
        foreach (string path in paths)
        {
            if (Directory.Exists(path) || WorkspaceModel.Opens(path))
            {
                workspace ??= path;
            }
            else if (File.Exists(path))
            {
                OpenFile(path);
            }
        }
        if (workspace is not null)
        {
            Open(workspace);
        }
    }
}
