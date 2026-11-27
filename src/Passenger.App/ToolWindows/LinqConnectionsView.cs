using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Passenger.App.Linq;
using Passenger.Linq;

namespace Passenger.App.ToolWindows;

public enum LinqTreeRowKind
{
    KindGroup,
    Connection,
    Schema,
    Table,
    Column,
}

/// <summary>A row in the hierarchical <see cref="LinqConnectionsView"/> tree:
/// Kind -> Connection -> Schema -> Table -> Column.</summary>
public sealed class LinqTreeRow(
    LinqTreeRowKind kind,
    string key,
    string title,
    int depth,
    LinqConnectionInfo? connection = null,
    LinqTableSchema? table = null,
    LinqColumnSchema? column = null,
    string? subtitle = null) : Observable, ITreeRow
{
    private bool isExpanded;

    public LinqTreeRowKind Kind { get; } = kind;
    public string Key { get; } = key;
    public string Title { get; } = title;
    public int Depth { get; } = depth;
    public LinqConnectionInfo? Connection { get; } = connection;
    public LinqTableSchema? Table { get; } = table;
    public LinqColumnSchema? Column { get; } = column;
    public string? Subtitle { get; } = subtitle;
    public List<LinqTreeRow> Children { get; } = [];

    public bool Expands => Children.Count > 0;

    public bool IsExpanded
    {
        get => isExpanded;
        set => Set(ref isExpanded, value);
    }

    public string? InsertSnippet => Kind switch
    {
        LinqTreeRowKind.Table when Table is not null => Table.PropertyName,
        LinqTreeRowKind.Column when Table is not null && Column is not null => $"{Table.PropertyName}.Select(x => x.{Column.PropertyName})",
        _ => null,
    };
}

/// <summary>Hierarchical tool window listing saved LINQ data connections grouped by kind:
/// Kind -> Connection -> Schema -> Table -> Column. Supports clicking/double-clicking or dragging a table
/// into the active <c>.linq</c> editor.</summary>
public sealed class LinqConnectionsView : DockPanel
{
    private readonly LinqConnectionService service;
    private readonly TreeList tree = new() { SelectionMode = SelectionMode.Single };
    private readonly TreeRows<LinqTreeRow> rows = [];
    private readonly HashSet<string> expandedKeys = new(StringComparer.Ordinal);
    private readonly List<LinqTreeRow> roots = [];

    private readonly TextBlock status = new();
    private readonly Button editBtn;
    private readonly Button refreshBtn;
    private readonly Button removeBtn;
    private readonly Button newQueryBtn;

    private Point? dragStart;
    private PointerPressedEventArgs? dragPress;
    private LinqTreeRow? dragRow;

    public LinqConnectionsView(LinqConnectionService service)
    {
        this.service = service;
        this.Bind(BackgroundProperty, this.GetResourceObservable(TokenKeys.Panel));

        Button addBtn = ToolButtons.Of(IconKeys.Add, "Add Connection…", () => AddRequested?.Invoke(this, EventArgs.Empty));
        editBtn = ToolButtons.Of(IconKeys.Edit, "Edit Connection…", () =>
        {
            if (SelectedConnection is { } c)
            {
                EditRequested?.Invoke(this, c);
            }
        });
        refreshBtn = ToolButtons.Of(IconKeys.Rerun, "Probe Schema", () =>
        {
            if (SelectedConnection is { } c)
            {
                RefreshRequested?.Invoke(this, c);
            }
        });
        removeBtn = ToolButtons.Of(IconKeys.Clear, "Remove Connection", () =>
        {
            if (SelectedConnection is { } c)
            {
                RemoveRequested?.Invoke(this, c);
            }
        });
        newQueryBtn = ToolButtons.Of(IconKeys.RunOutline, "New .linq Query with Connection", () =>
        {
            if (SelectedConnection is { } c)
            {
                NewQueryRequested?.Invoke(this, c);
            }
        });

        StackPanel toolbar = new()
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            Margin = new Thickness(6, 4),
            Children = { addBtn, editBtn, refreshBtn, newQueryBtn, removeBtn },
        };
        DockPanel.SetDock(toolbar, Dock.Top);
        Children.Add(toolbar);

        DockPanel.SetDock(status, Dock.Bottom);
        status.Margin = new Thickness(8, 4);
        status.Classes.Add(StyleClasses.Caption);
        status.Classes.Add(StyleClasses.Muted);
        Children.Add(status);

        tree.Classes.Add(StyleClasses.Tree);
        tree.ItemsSource = rows;
        tree.ItemTemplate = RowTemplate();
        tree.Styles.Add(new Avalonia.Styling.Style(x => Avalonia.Styling.Selectors.OfType(x, typeof(ListBoxItem)))
        {
            Setters =
            {
                new Avalonia.Styling.Setter(MinHeightProperty, 18.0),
                new Avalonia.Styling.Setter(Avalonia.Controls.Primitives.TemplatedControl.PaddingProperty, new Thickness(4, 0)),
            },
        });
        tree.Rows<LinqTreeRow>(row => row, row => OpenRow(row));
        TreeKeys.Attach<LinqTreeRow>(tree, row => row, Toggle, OpenRow);
        tree.SelectionChanged += (_, _) => UpdateSelection();
        tree.AddHandler(PointerPressedEvent, OnTreePointerPressed, RoutingStrategies.Tunnel);
        tree.AddHandler(PointerMovedEvent, OnTreePointerMoved, RoutingStrategies.Tunnel);
        tree.AddHandler(PointerReleasedEvent, OnTreePointerReleased, RoutingStrategies.Tunnel);
        tree.DoubleTapped += (_, _) =>
        {
            if (SelectedRow is { } row)
            {
                OpenRow(row);
            }
        };

        Children.Add(tree);

        this.service.Changed += (_, _) => RebuildTree();
        RebuildTree();
    }

    public event EventHandler? AddRequested;
    public event EventHandler<LinqConnectionInfo>? EditRequested;
    public event EventHandler<LinqConnectionInfo>? RefreshRequested;
    public event EventHandler<LinqConnectionInfo>? RemoveRequested;
    public event EventHandler<LinqConnectionInfo>? NewQueryRequested;
    public event EventHandler<string>? InsertSnippetRequested;

    public LinqTreeRow? SelectedRow => tree.SelectedItem as LinqTreeRow;

    public LinqConnectionInfo? SelectedConnection => SelectedRow?.Connection;

    public void SetStatus(string text) => status.Text = text;

    private bool OpenRow(LinqTreeRow row)
    {
        if (row.InsertSnippet is { } snippet)
        {
            InsertSnippetRequested?.Invoke(this, snippet);
            return true;
        }
        if (row.Expands)
        {
            Toggle(row);
            return true;
        }
        return false;
    }

    private void Toggle(LinqTreeRow row)
    {
        if (!row.Expands)
        {
            return;
        }
        if (row.IsExpanded)
        {
            row.IsExpanded = false;
            expandedKeys.Remove(row.Key);
            rows.Close(row);
        }
        else
        {
            row.IsExpanded = true;
            expandedKeys.Add(row.Key);
            rows.Open(row, TreeRows<LinqTreeRow>.Shown(row.Children, r => r.Children));
        }
    }

    private void OnTreePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(tree).Properties.IsLeftButtonPressed)
        {
            return;
        }
        if ((e.Source as StyledElement)?.DataContext is LinqTreeRow row && row.InsertSnippet is not null)
        {
            dragStart = e.GetPosition(tree);
            dragPress = e;
            dragRow = row;
        }
        else
        {
            dragStart = null;
            dragPress = null;
            dragRow = null;
        }
    }

    private void OnTreePointerMoved(object? sender, PointerEventArgs e)
    {
        if (dragStart is not { } start || dragPress is not { } press || dragRow is not { InsertSnippet: { } snippet })
        {
            return;
        }
        Point now = e.GetPosition(tree);
        if (Math.Abs(now.X - start.X) < 6 && Math.Abs(now.Y - start.Y) < 6)
        {
            return;
        }
        dragStart = null;
        dragPress = null;
        dragRow = null;
        DataTransfer data = new();
        data.Add(DataTransferItem.CreateText(snippet));
        IdeErrors.Observe(DragDrop.DoDragDropAsync(press, data, DragDropEffects.Copy));
    }

    private void OnTreePointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        dragStart = null;
        dragPress = null;
        dragRow = null;
    }

    private void RebuildTree()
    {
        string? selectedKey = SelectedRow?.Key;
        roots.Clear();

        foreach (IGrouping<LinqDataSourceKind, LinqConnectionInfo> group in service.Connections
            .GroupBy(c => c.Kind)
            .OrderBy(g => g.Key.ToString(), StringComparer.Ordinal))
        {
            string kindKey = $"kind:{group.Key}";
            LinqTreeRow kindRow = new(
                LinqTreeRowKind.KindGroup,
                kindKey,
                group.Key.ToString(),
                depth: 0,
                subtitle: $"({group.Count()})")
            {
                IsExpanded = expandedKeys.Contains(kindKey) || expandedKeys.Count == 0,
            };
            if (kindRow.IsExpanded)
            {
                expandedKeys.Add(kindKey);
            }

            foreach (LinqConnectionInfo conn in group.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
            {
                string connKey = $"conn:{conn.Id}";
                LinqDatabaseSchema? schema = service.GetCachedSchema(conn.Id);
                string connSub = schema is null ? "(not probed)" : $"({schema.Tables.Count} tables)";
                LinqTreeRow connRow = new(
                    LinqTreeRowKind.Connection,
                    connKey,
                    conn.Name,
                    depth: 1,
                    connection: conn,
                    subtitle: connSub)
                {
                    IsExpanded = expandedKeys.Contains(connKey),
                };

                if (schema is not null)
                {
                    List<IGrouping<string, LinqTableSchema>> schemaGroups = [.. schema.Tables
                        .GroupBy(t => string.IsNullOrWhiteSpace(t.SchemaName) ? string.Empty : t.SchemaName)
                        .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)];

                    bool multipleSchemas = schemaGroups.Count > 1 || (schemaGroups.Count == 1 && !string.IsNullOrEmpty(schemaGroups[0].Key));
                    foreach (IGrouping<string, LinqTableSchema> sg in schemaGroups)
                    {
                        if (multipleSchemas)
                        {
                            string sName = string.IsNullOrEmpty(sg.Key) ? "default" : sg.Key;
                            string sKey = $"schema:{conn.Id}:{sName}";
                            LinqTreeRow schemaRow = new(
                                LinqTreeRowKind.Schema,
                                sKey,
                                sName,
                                depth: 2,
                                connection: conn,
                                subtitle: $"({sg.Count()})")
                            {
                                IsExpanded = expandedKeys.Contains(sKey),
                            };
                            foreach (LinqTableSchema tbl in sg.OrderBy(t => t.PropertyName, StringComparer.OrdinalIgnoreCase))
                            {
                                schemaRow.Children.Add(BuildTableRow(conn, tbl, depth: 3));
                            }
                            connRow.Children.Add(schemaRow);
                        }
                        else
                        {
                            foreach (LinqTableSchema tbl in sg.OrderBy(t => t.PropertyName, StringComparer.OrdinalIgnoreCase))
                            {
                                connRow.Children.Add(BuildTableRow(conn, tbl, depth: 2));
                            }
                        }
                    }
                }

                kindRow.Children.Add(connRow);
            }

            roots.Add(kindRow);
        }

        rows.Show([.. TreeRows<LinqTreeRow>.Shown(roots, r => r.Children)]);

        if (selectedKey is not null)
        {
            foreach (LinqTreeRow row in rows)
            {
                if (string.Equals(row.Key, selectedKey, StringComparison.Ordinal))
                {
                    tree.SelectedItem = row;
                    break;
                }
            }
        }
        UpdateSelection();
    }

    private LinqTreeRow BuildTableRow(LinqConnectionInfo conn, LinqTableSchema tbl, int depth)
    {
        string tblKey = $"table:{conn.Id}:{tbl.SchemaName}.{tbl.TableName}";
        LinqTreeRow tableRow = new(
            LinqTreeRowKind.Table,
            tblKey,
            tbl.PropertyName,
            depth: depth,
            connection: conn,
            table: tbl,
            subtitle: $"({tbl.Columns.Count})")
        {
            IsExpanded = expandedKeys.Contains(tblKey),
        };
        foreach (LinqColumnSchema col in tbl.Columns)
        {
            string colKey = $"col:{conn.Id}:{tbl.SchemaName}.{tbl.TableName}.{col.Name}";
            string typeStr = col.IsNullable && !col.ClrTypeName.EndsWith('?') ? col.ClrTypeName + "?" : col.ClrTypeName;
            tableRow.Children.Add(new LinqTreeRow(
                LinqTreeRowKind.Column,
                colKey,
                col.PropertyName,
                depth: depth + 1,
                connection: conn,
                table: tbl,
                column: col,
                subtitle: typeStr));
        }
        return tableRow;
    }

    private void UpdateSelection()
    {
        bool hasConn = SelectedConnection is not null;
        editBtn.IsEnabled = hasConn;
        refreshBtn.IsEnabled = hasConn;
        removeBtn.IsEnabled = hasConn;
        newQueryBtn.IsEnabled = hasConn;
    }

    private Avalonia.Controls.Templates.FuncDataTemplate<LinqTreeRow> RowTemplate() => new((_, _) =>
    {
        RowExpander chevron = RowExpander.Of<LinqTreeRow>(Toggle);
        RowIcon mark = new() { Margin = new Thickness(0, 0, Gaps.Small, 0) };
        TextBlock name = new() { Classes = { StyleClasses.Caption }, VerticalAlignment = VerticalAlignment.Center };
        TextBlock sub = new() { Classes = { StyleClasses.BadgeText }, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(Gaps.Small, 0, 0, 0) };
        sub.Classes.Add(StyleClasses.Muted);
        ShrinkingRow row = new() { Height = 18, Children = { chevron, mark, name, sub } };
        return row.Showing<LinqTreeRow>(node =>
        {
            row.Margin = new Thickness(node.Depth * Gaps.Indent, 0, 0, 0);
            chevron.Expands = node.Expands;
            chevron.IsOpen = node.IsExpanded;
            mark.DataKey = node.Kind switch
            {
                LinqTreeRowKind.KindGroup => IconKeys.FolderOpen,
                LinqTreeRowKind.Connection => IconKeys.Database,
                LinqTreeRowKind.Schema => IconKeys.LinqSchema,
                LinqTreeRowKind.Table => IconKeys.LinqTable,
                LinqTreeRowKind.Column => IconKeys.Variable,
                _ => IconKeys.DocumentText,
            };
            name.Text = node.Title;
            name.Classes.Set(StyleClasses.Strong, node.Kind is LinqTreeRowKind.KindGroup or LinqTreeRowKind.Connection);
            sub.IsVisible = !string.IsNullOrEmpty(node.Subtitle);
            sub.Text = node.Subtitle;
        });
    });
}
