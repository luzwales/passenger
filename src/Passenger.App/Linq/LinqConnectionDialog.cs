using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Passenger.App.Welcome;
using Passenger.Linq;

namespace Passenger.App.Linq;

/// <summary>Dynamic modal dialog to add or edit a LINQ data connection: SQL providers (SQLite, SQL Server, PostgreSQL,
/// MySQL) with <c>appsettings*.json</c> discovery, <c>Test</c> button, and regex <c>Filter</c> tab; file providers (CSV,
/// XLSX, JSON); and Workspace <c>DbContext</c> references.</summary>
public sealed class LinqConnectionDialog : IdeWindow
{
    private readonly IReadOnlyList<DiscoveredConnectionString> discoveredStrings;
    private readonly IReadOnlyList<DiscoveredDbContext> discoveredContexts;

    private readonly ComboBox kindBox = new()
    {
        ItemsSource = new[]
        {
            LinqDataSourceKind.Sqlite,
            LinqDataSourceKind.SqlServer,
            LinqDataSourceKind.PostgreSql,
            LinqDataSourceKind.MySql,
            LinqDataSourceKind.Csv,
            LinqDataSourceKind.Xlsx,
            LinqDataSourceKind.Json,
            LinqDataSourceKind.WorkspaceDbContext,
        },
        SelectedIndex = 0,
    };

    private readonly TextBox nameBox = new() { PlaceholderText = "e.g. SalesDb" };
    private readonly ComboBox appSettingsBox = new() { PlaceholderText = "Import from workspace appsettings*.json…" };
    private readonly ComboBox dbContextBox = new() { PlaceholderText = "Select DbContext from workspace…" };
    private readonly TextBox targetBox = new() { PlaceholderText = "Connection string or file path", AcceptsReturn = false };
    private readonly Button browseBtn = new() { Content = "Browse…" };

    private readonly Control appSettingsRow;
    private readonly Control dbContextRow;
    private readonly Control targetRowControl;

    private readonly TextBox includeSchemasBox = new() { PlaceholderText = "e.g. ^(dbo|sales)$ (empty = all)" };
    private readonly TextBox excludeSchemasBox = new() { PlaceholderText = "e.g. ^(sys|HangFire)$" };
    private readonly TextBox includeTablesBox = new() { PlaceholderText = "e.g. ^(Orders|Customers)" };
    private readonly TextBox excludeTablesBox = new() { PlaceholderText = "e.g. ^__EFMigrationsHistory$" };

    private readonly Button testBtn = new() { Content = "Test" };
    private readonly TextBlock statusLabel = new() { TextWrapping = TextWrapping.Wrap, IsVisible = false };

    private bool suppressAppSettingsSelect;

    public LinqConnectionDialog(
        LinqConnectionInfo? existing = null,
        IReadOnlyList<DiscoveredConnectionString>? discoveredStrings = null,
        IReadOnlyList<DiscoveredDbContext>? discoveredContexts = null)
    {
        this.discoveredStrings = discoveredStrings ?? [];
        this.discoveredContexts = discoveredContexts ?? [];

        FormRows.Scope(this);
        Title = existing is null ? "Add LINQ Data Connection" : "Edit LINQ Data Connection";
        Width = DialogWidths.Form + 80;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        if (this.discoveredStrings.Count > 0)
        {
            appSettingsBox.ItemsSource = this.discoveredStrings.Select(s => $"{s.Name} ({s.SourceFile})").ToList();
        }
        else
        {
            appSettingsBox.IsEnabled = false;
            appSettingsBox.PlaceholderText = "No ConnectionStrings found in appsettings*.json";
        }

        if (this.discoveredContexts.Count > 0)
        {
            dbContextBox.ItemsSource = this.discoveredContexts.Select(c => $"{c.FullTypeName} ({c.ProjectName})").ToList();
        }
        else
        {
            dbContextBox.IsEnabled = false;
            dbContextBox.PlaceholderText = "No DbContext classes found in workspace";
        }

        browseBtn.Click += (_, _) => IdeErrors.Observe(BrowseFileAsync());
        appSettingsBox.SelectionChanged += (_, _) => OnAppSettingsSelected();
        dbContextBox.SelectionChanged += (_, _) => OnDbContextSelected();
        kindBox.SelectionChanged += (_, _) => UpdateKindVisibility();
        testBtn.Click += (_, _) => IdeErrors.Observe(TestAsync());

        Grid targetGrid = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        targetGrid.Children.Add(targetBox);
        Grid.SetColumn(browseBtn, 1);
        browseBtn.Margin = new Thickness(Gaps.Tight, 0, 0, 0);
        targetGrid.Children.Add(browseBtn);

        appSettingsRow = FormRows.Of("appsettings", appSettingsBox);
        dbContextRow = FormRows.Of("DbContext", dbContextBox);
        targetRowControl = FormRows.Of("Target", targetGrid);

        StackPanel connectionFields = new()
        {
            Spacing = FormRows.Between,
            Margin = new Thickness(0, FormRows.Between, 0, 0),
            Children =
            {
                FormRows.Of("Kind", kindBox),
                FormRows.Of("Name", nameBox),
                appSettingsRow,
                dbContextRow,
                targetRowControl,
            },
        };

        StackPanel filterFields = new()
        {
            Spacing = FormRows.Between,
            Margin = new Thickness(0, FormRows.Between, 0, 0),
            Children =
            {
                FormRows.Of("Include Schemas", includeSchemasBox),
                FormRows.Of("Exclude Schemas", excludeSchemasBox),
                FormRows.Of("Include Tables", includeTablesBox),
                FormRows.Of("Exclude Tables", excludeTablesBox),
            },
        };

        TabControl tabs = new()
        {
            Items =
            {
                new TabItem { Header = "Connection", Content = connectionFields },
                new TabItem { Header = "Filter", Content = filterFields },
            },
        };
        tabs.Bind(BackgroundProperty, this.GetResourceObservable(TokenKeys.Panel));

        StackPanel body = new()
        {
            Spacing = FormRows.Between,
            Children = { tabs, statusLabel },
        };

        if (existing is not null)
        {
            kindBox.SelectedItem = existing.Kind;
            nameBox.Text = existing.Name;
            targetBox.Text = existing.Target;
            includeSchemasBox.Text = existing.IncludeSchemasRegex ?? string.Empty;
            excludeSchemasBox.Text = existing.ExcludeSchemasRegex ?? string.Empty;
            includeTablesBox.Text = existing.IncludeTablesRegex ?? string.Empty;
            excludeTablesBox.Text = existing.ExcludeTablesRegex ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(existing.DbContextTypeName))
            {
                int idx = -1;
                for (int i = 0; i < this.discoveredContexts.Count; i++)
                {
                    if (string.Equals(this.discoveredContexts[i].FullTypeName, existing.DbContextTypeName, StringComparison.Ordinal))
                    {
                        idx = i;
                        break;
                    }
                }
                if (idx >= 0)
                {
                    dbContextBox.SelectedIndex = idx;
                }
            }
        }

        UpdateKindVisibility();

        Button ok = DialogFooter.Default("Save");
        ok.Click += (_, _) =>
        {
            LinqConnectionInfo? built = BuildConnection(existing?.Id);
            if (built is null)
            {
                return;
            }
            Result = built;
            Close(Result);
        };

        Content = DialogFooter.Page(body, DialogFooter.OkCancel(ok, () => Close(null), left: null, testBtn));
    }

    public LinqConnectionInfo? Result { get; private set; }

    private LinqDataSourceKind SelectedKind =>
        kindBox.SelectedItem is LinqDataSourceKind k ? k : LinqDataSourceKind.Sqlite;

    private static bool IsSqlKind(LinqDataSourceKind kind) =>
        kind is LinqDataSourceKind.Sqlite or LinqDataSourceKind.SqlServer or LinqDataSourceKind.PostgreSql or LinqDataSourceKind.MySql;

    private void UpdateKindVisibility()
    {
        LinqDataSourceKind kind = SelectedKind;
        bool isSql = IsSqlKind(kind);
        bool isCtx = kind == LinqDataSourceKind.WorkspaceDbContext;

        appSettingsRow.IsVisible = isSql || isCtx;
        dbContextRow.IsVisible = isCtx;
        browseBtn.IsVisible = !isCtx && (kind is LinqDataSourceKind.Csv or LinqDataSourceKind.Xlsx or LinqDataSourceKind.Json or LinqDataSourceKind.Sqlite);

        targetBox.PlaceholderText = kind switch
        {
            LinqDataSourceKind.Sqlite => "Data Source=path/to/db.sqlite  or  path/to/db.sqlite",
            LinqDataSourceKind.SqlServer => "Server=localhost;Database=MyDb;Trusted_Connection=True;TrustServerCertificate=True",
            LinqDataSourceKind.PostgreSql => "Host=localhost;Database=mydb;Username=postgres;Password=...",
            LinqDataSourceKind.MySql => "Server=localhost;Database=mydb;Uid=root;Pwd=...",
            LinqDataSourceKind.Csv => "Path to .csv file",
            LinqDataSourceKind.Xlsx => "Path to .xlsx workbook",
            LinqDataSourceKind.Json => "Path to .json file",
            LinqDataSourceKind.WorkspaceDbContext => "Optional connection string (empty = DbContext default)",
            _ => "Connection string or file path",
        };
    }

    private void OnAppSettingsSelected()
    {
        if (suppressAppSettingsSelect)
        {
            return;
        }
        int idx = appSettingsBox.SelectedIndex;
        if (idx < 0 || idx >= discoveredStrings.Count)
        {
            return;
        }
        DiscoveredConnectionString picked = discoveredStrings[idx];
        targetBox.Text = picked.ConnectionString;
        if (string.IsNullOrWhiteSpace(nameBox.Text))
        {
            nameBox.Text = picked.Name;
        }
        if (SelectedKind != LinqDataSourceKind.WorkspaceDbContext)
        {
            suppressAppSettingsSelect = true;
            kindBox.SelectedItem = picked.GuessedKind;
            suppressAppSettingsSelect = false;
        }
    }

    private void OnDbContextSelected()
    {
        int idx = dbContextBox.SelectedIndex;
        if (idx < 0 || idx >= discoveredContexts.Count)
        {
            return;
        }
        DiscoveredDbContext picked = discoveredContexts[idx];
        if (string.IsNullOrWhiteSpace(nameBox.Text))
        {
            nameBox.Text = picked.FullTypeName.Split('.').Last();
        }
    }

    private LinqConnectionInfo? BuildConnection(string? existingId)
    {
        string name = (nameBox.Text ?? string.Empty).Trim();
        string target = (targetBox.Text ?? string.Empty).Trim();
        LinqDataSourceKind kind = SelectedKind;

        string? dbCtxProj = null;
        string? dbCtxType = null;
        if (kind == LinqDataSourceKind.WorkspaceDbContext)
        {
            int idx = dbContextBox.SelectedIndex;
            if (idx < 0 || idx >= discoveredContexts.Count)
            {
                ShowStatus("Select a DbContext from the workspace.", isError: true);
                return null;
            }
            dbCtxProj = discoveredContexts[idx].ProjectPath;
            dbCtxType = discoveredContexts[idx].FullTypeName;
        }
        else if (target.Length == 0)
        {
            ShowStatus("Target path or connection string is required.", isError: true);
            return null;
        }

        if (name.Length == 0)
        {
            ShowStatus("Name is required.", isError: true);
            return null;
        }

        string id = existingId ?? Guid.NewGuid().ToString("N")[..12];
        return new LinqConnectionInfo(
            id,
            name,
            kind,
            target,
            NullIfBlank(includeSchemasBox.Text),
            NullIfBlank(excludeSchemasBox.Text),
            NullIfBlank(includeTablesBox.Text),
            NullIfBlank(excludeTablesBox.Text),
            dbCtxProj,
            dbCtxType);
    }

    private static string? NullIfBlank(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private void ShowStatus(string message, bool isError)
    {
        statusLabel.Text = message;
        statusLabel.IsVisible = true;
        statusLabel.Classes.Set(StyleClasses.Error, isError);
    }

    private async Task TestAsync()
    {
        LinqConnectionInfo? info = BuildConnection(existingId: "test");
        if (info is null)
        {
            return;
        }
        testBtn.IsEnabled = false;
        ShowStatus("Testing connection…", isError: false);
        try
        {
            string? err = await LinqSchemaProber.TestConnectionAsync(info);
            ShowStatus(err ?? "Connection succeeded.", isError: err is not null);
        }
        finally
        {
            testBtn.IsEnabled = true;
        }
    }

    private async Task BrowseFileAsync()
    {
        IReadOnlyList<Avalonia.Platform.Storage.IStorageFile> picked = await StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
        {
            Title = "Select Data File",
            AllowMultiple = false,
        });
        if (picked.Count > 0 && picked[0].TryGetLocalPath() is { } local)
        {
            string ext = Path.GetExtension(local).ToLowerInvariant();
            if (ext == ".csv")
            {
                kindBox.SelectedItem = LinqDataSourceKind.Csv;
                targetBox.Text = local;
            }
            else if (ext == ".xlsx")
            {
                kindBox.SelectedItem = LinqDataSourceKind.Xlsx;
                targetBox.Text = local;
            }
            else if (ext == ".json")
            {
                kindBox.SelectedItem = LinqDataSourceKind.Json;
                targetBox.Text = local;
            }
            else if (ext is ".db" or ".sqlite" or ".sqlite3")
            {
                kindBox.SelectedItem = LinqDataSourceKind.Sqlite;
                targetBox.Text = $"Data Source={local}";
            }
            else
            {
                targetBox.Text = local;
            }

            if (string.IsNullOrWhiteSpace(nameBox.Text))
            {
                nameBox.Text = Path.GetFileNameWithoutExtension(local);
            }
        }
    }
}
