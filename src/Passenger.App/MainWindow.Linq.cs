using System.Diagnostics;
using System.Text;
using Passenger.App.Linq;
using Passenger.App.ToolWindows;
using Passenger.Core;
using Passenger.Linq;
using Passenger.Text;
using Passenger.Workspace;
using A = Passenger.App.AppActions;

namespace Passenger.App;

/// <summary>Executes <c>.linq</c> queries (Statements, Expression, Program) in an isolated generated project referencing
/// <c>linqruntime/Passenger.Linq.Runtime.dll</c> and optional strongly-typed <c>TypedDataContext</c>, streaming
/// <c>.Dump()</c> HTML + SQL output into <see cref="ToolWindowIds.LinqResult"/> and managing data sources in
/// <see cref="ToolWindowIds.LinqConnections"/>.</summary>
public partial class MainWindow
{
    private readonly LinqConnectionService linqConnections = new();
    private LinqConnectionsView? linqConnectionsView;
    private Process? linqProcess;
    private LinqResultServer? linqServer;
    private CancellationTokenSource? linqCts;

    private void AddLinq()
    {
        Dock.Add(new Docking.ToolWindow(ToolWindowIds.LinqResult, "Query Result", IconKeys.LinqResult, Bottom.LinqResult, Docking.ToolAnchor.BottomLeft)
        {
            Serves = ToolWindowServes.EveryFolder,
            Action = AppActions.ShowLinqResult,
            ActionTitle = "Query Result Tool Window",
        });

        Dock.Add(new Docking.ToolWindow(ToolWindowIds.LinqConnections, "Data Connections", IconKeys.LinqConnections, MakeLinqConnectionsView, Docking.ToolAnchor.LeftBottom)
        {
            Serves = ToolWindowServes.EveryFolder,
            Action = AppActions.ShowLinqConnections,
            ActionTitle = "Data Connections Tool Window",
        });

        Bottom.LinqResult.RunClicked += (_, _) => IdeErrors.Observe(RunActiveLinqAsync(debug: false));
        Bottom.LinqResult.DebugClicked += (_, _) => IdeErrors.Observe(RunActiveLinqAsync(debug: true));
        Bottom.LinqResult.StopClicked += (_, _) => StopActiveLinq();
        Bottom.LinqResult.SetDark(ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark);
        ActualThemeVariantChanged += (_, _) => Bottom.LinqResult.SetDark(ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark);

        actions!.Add(new IdeAction(A.RunLinq, "Run Query (.linq / .sql)", "Run", _ => IdeErrors.Observe(RunActiveLinqAsync(debug: false)))
        {
            WhyNot = c => ActiveQueryFile(c) is null ? "Open a .linq or .sql file first" : null,
        });
        actions!.Add(new IdeAction(A.DebugLinq, "Debug .linq Query", "Run", _ => IdeErrors.Observe(RunActiveLinqAsync(debug: true)))
        {
            WhyNot = c =>
            {
                string? p = ActiveQueryFile(c);
                return p is null || !p.EndsWith(".linq", StringComparison.OrdinalIgnoreCase) ? "Open a .linq file first" : null;
            },
        });
        actions!.Add(new IdeAction(A.StopLinq, "Stop .linq Query", "Run", _ => StopActiveLinq())
        {
            WhyNot = _ => linqProcess is null && !session.Debug.IsDebugging ? "No .linq query is running" : null,
        });
    }

    private LinqConnectionsView MakeLinqConnectionsView()
    {
        LinqConnectionsView view = linqConnectionsView = new LinqConnectionsView(linqConnections);
        view.AddRequested += (_, _) => IdeErrors.Observe(AddLinqConnectionAsync());
        view.EditRequested += (_, conn) => IdeErrors.Observe(EditLinqConnectionAsync(conn));
        view.RefreshRequested += (_, conn) => IdeErrors.Observe(ProbeLinqConnectionAsync(conn));
        view.RemoveRequested += (_, conn) => RemoveLinqConnection(conn);
        view.NewQueryRequested += (_, conn) => IdeErrors.Observe(NewLinqQueryWithConnectionAsync(conn));
        view.InsertSnippetRequested += (_, snippet) => InsertLinqSnippet(snippet);
        return view;
    }

    private void InsertLinqSnippet(string snippet)
    {
        if (Editors.Active is { IsReadOnly: false } doc && Editors.ActiveGroup.Editor is { } editor)
        {
            EditPlan plan = TextEditing.Insert(editor.Document.Snapshot, editor.Selections, snippet);
            editor.ApplyEdits(plan.Edits, plan.Selections);
            editor.Focus();
            if (doc.Path.EndsWith(".linq", StringComparison.OrdinalIgnoreCase)
                && linqConnectionsView?.SelectedConnection is { } conn)
            {
                EnsureLinqConnectionHeader(editor, conn);
            }
        }
    }

    private static void EnsureLinqConnectionHeader(Editor.CodeEditor editor, LinqConnectionInfo conn)
    {
        string text = editor.Document.Snapshot.ToString();
        LinqDocument parsed = LinqDocument.Parse(text);
        if (!string.IsNullOrWhiteSpace(parsed.ConnectionId) || !string.IsNullOrWhiteSpace(parsed.ConnectionName))
        {
            return;
        }
        string lineEnding = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        string header = string.Join(lineEnding,
            "<Query Kind=\"Statements\">",
            "  <Connection>",
            $"    <ID>{XmlEntities.Encode(conn.Id)}</ID>",
            $"    <Name>{XmlEntities.Encode(conn.Name)}</Name>",
            "  </Connection>",
            "</Query>",
            string.Empty);
        int replaceLen = 0;
        if (parsed.HasXmlHeader)
        {
            int close = text.IndexOf("</Query>", StringComparison.Ordinal);
            if (close >= 0)
            {
                int lineEnd = text.IndexOf('\n', close);
                replaceLen = lineEnd >= 0 ? lineEnd + 1 : close + "</Query>".Length;
            }
            else
            {
                int gt = text.IndexOf('>');
                if (gt >= 0)
                {
                    int lineEnd = text.IndexOf('\n', gt);
                    replaceLen = lineEnd >= 0 ? lineEnd + 1 : gt + 1;
                }
            }
        }
        else
        {
            header += lineEnding;
        }
        TextEdit change = new(0, replaceLen, header);
        int delta = header.Length - replaceLen;
        SelectionSet shifted = editor.Selections.With(s => new Selection(Math.Max(0, s.Anchor + delta), Math.Max(0, s.Caret + delta)));
        editor.ApplyEdits([change], shifted);
    }

    private void LoadLinqConnectionsState()
    {
        if (state is not null)
        {
            linqConnections.Load(state.LinqConnections.Value);
            foreach (LinqConnectionInfo conn in linqConnections.Connections)
            {
                IdeErrors.Observe(ProbeLinqConnectionAsync(conn));
            }
        }
    }

    private void SaveLinqConnectionsState()
    {
        if (keepsWorkspace && state is not null)
        {
            state.LinqConnections.Set(linqConnections.ToState());
        }
    }

    private LinqConnectionDialog CreateLinqConnectionDialog(LinqConnectionInfo? existing = null)
    {
        IReadOnlyList<DiscoveredConnectionString> strings = LinqWorkspaceDiscovery.FindConnectionStrings(session.Folder);
        IReadOnlyList<DiscoveredDbContext> contexts = LinqWorkspaceDiscovery.FindDbContexts(session.Folder, session.Projects);
        return new LinqConnectionDialog(existing, strings, contexts);
    }

    private async Task AddLinqConnectionAsync()
    {
        if (AgentAsks.Refused())
        {
            Say(Refusals.AsksThePerson);
            return;
        }
        LinqConnectionDialog dialog = CreateLinqConnectionDialog();
        if (await dialog.ShowDialog<LinqConnectionInfo?>(this) is { } created)
        {
            linqConnections.AddOrUpdate(created);
            SaveLinqConnectionsState();
            await ProbeLinqConnectionAsync(created);
        }
    }

    private async Task EditLinqConnectionAsync(LinqConnectionInfo existing)
    {
        if (AgentAsks.Refused())
        {
            Say(Refusals.AsksThePerson);
            return;
        }
        LinqConnectionDialog dialog = CreateLinqConnectionDialog(existing);
        if (await dialog.ShowDialog<LinqConnectionInfo?>(this) is { } updated)
        {
            linqConnections.AddOrUpdate(updated);
            SaveLinqConnectionsState();
            await ProbeLinqConnectionAsync(updated);
        }
    }

    private async Task ProbeLinqConnectionAsync(LinqConnectionInfo conn)
    {
        linqConnectionsView?.SetStatus($"Probing {conn.Name}…");
        try
        {
            LinqDatabaseSchema schema = await linqConnections.ProbeAndGenerateAsync(conn, StateFolder);
            linqConnectionsView?.SetStatus($"Ready: {conn.Name} ({schema.Tables.Count} tables)");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            linqConnectionsView?.SetStatus($"Probe failed: {ex.Message}");
        }
    }

    private void RemoveLinqConnection(LinqConnectionInfo conn)
    {
        linqConnections.Remove(conn.Id);
        SaveLinqConnectionsState();
        linqConnectionsView?.SetStatus($"Removed {conn.Name}");
    }

    private async Task NewLinqQueryWithConnectionAsync(LinqConnectionInfo conn)
    {
        if (session.Folder is not { } folder)
        {
            Refuse("Open a folder before creating a .linq query.");
            return;
        }
        LinqDatabaseSchema? schema = linqConnections.GetCachedSchema(conn.Id);
        if (schema is null)
        {
            await ProbeLinqConnectionAsync(conn);
            schema = linqConnections.GetCachedSchema(conn.Id);
        }
        string sampleTable = schema?.Tables.FirstOrDefault()?.PropertyName ?? "// Write your LINQ query here";
        string body = schema?.Tables.FirstOrDefault() is not null
            ? $"{sampleTable}.Take(20).Dump();"
            : "\"Hello from Passenger LINQ\".Dump();";

        string fileName = $"Query_{FileNames.Safe(conn.Name)}.linq";
        string path = Path.Combine(folder, fileName);
        int n = 2;
        while (File.Exists(path))
        {
            path = Path.Combine(folder, $"Query_{FileNames.Safe(conn.Name)}_{n++}.linq");
        }
        string xml = $"""
            <Query Kind="Statements">
              <Connection>
                <ID>{XmlEntities.Encode(conn.Id)}</ID>
                <Name>{XmlEntities.Encode(conn.Name)}</Name>
              </Connection>
            </Query>

            {body}
            """;
        await BackgroundWork.Run(() => AtomicFile.Write(path, Encoding.UTF8.GetBytes(xml)));
        OpenFile(path);
    }

    private string? ActiveQueryFile(ActionContext? context = null)
    {
        string? path = context?.File?.Path ?? Editors.Active?.Path;
        return path is not null && (path.EndsWith(".linq", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".sql", StringComparison.OrdinalIgnoreCase)) ? path : null;
    }

    private string? ActiveLinqFile(ActionContext? context = null) => ActiveQueryFile(context);

    private void StopActiveLinq()
    {
        linqCts?.Cancel();
        if (linqProcess is { } proc)
        {
            try
            {
                ChildProcesses.Kill(proc);
            }
            catch (InvalidOperationException)
            {
            }
        }
    }

    private async Task RunActiveLinqAsync(bool debug = false, string? selectedBody = null)
    {
        if (ActiveQueryFile() is not { } linqPath)
        {
            return;
        }
        if (session.Folder is not null && !session.Trusted)
        {
            Refuse(Refusals.Untrusted);
            return;
        }
        if (await Editors.SaveAllLandedAsync(null, CancellationToken.None) is { } unsaved)
        {
            Refuse(unsaved);
            return;
        }

        if (linqPath.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
        {
            await RunActiveSqlAsync(linqPath, selectedBody);
            return;
        }

        StopActiveLinq();
        linqCts?.Dispose();
        linqCts = new CancellationTokenSource();
        CancellationToken token = linqCts.Token;

        Dock.Show(ToolWindowIds.LinqResult);
        Bottom.LinqResult.Clear();
        Bottom.LinqResult.SetRunning(true, $"Building {Path.GetFileName(linqPath)}…");

        string rawText = await BackgroundWork.Run(() => File.ReadAllText(linqPath, Encoding.UTF8), token);
        LinqDocument doc = LinqDocument.Parse(rawText);
        if (selectedBody is not null)
        {
            string trimmed = selectedBody.Trim();
            string withoutTrailingSemi = trimmed.EndsWith(';') ? trimmed[..^1].TrimEnd() : trimmed;
            LinqQueryKind kind = !withoutTrailingSemi.Contains('\n') && !withoutTrailingSemi.Contains(';')
                ? LinqQueryKind.Expression
                : LinqQueryKind.Statements;
            doc = doc with { Kind = kind, Body = selectedBody, HasXmlHeader = false };
        }

        string runtimeDll = Path.Combine(CarriedFiles.Folder("linqruntime"), "Passenger.Linq.Runtime.dll");
        if (!File.Exists(runtimeDll))
        {
            Bottom.LinqResult.SetRunning(false, "Passenger.Linq.Runtime.dll not found under linqruntime/.");
            return;
        }

        LinqConnectionInfo? conn = linqConnections.Find(doc.ConnectionId) ?? linqConnections.Find(doc.ConnectionName);
        if (conn is null && string.IsNullOrWhiteSpace(doc.ConnectionId) && string.IsNullOrWhiteSpace(doc.ConnectionName))
        {
            conn = linqConnectionsView?.SelectedConnection ?? linqConnections.Connections.FirstOrDefault();
        }
        if (conn is not null)
        {
            Bottom.LinqResult.SetRunning(true, $"Building {Path.GetFileName(linqPath)} ({conn.Name})…");
        }
        LinqDatabaseSchema? schema = null;
        string? generatedContextPath = null;
        if (conn is not null)
        {
            schema = linqConnections.GetCachedSchema(conn.Id);
            generatedContextPath = linqConnections.GetGeneratedContextPath(conn.Id);
            if (schema is null || generatedContextPath is null)
            {
                try
                {
                    schema = await linqConnections.ProbeAndGenerateAsync(conn, StateFolder, token);
                    generatedContextPath = linqConnections.GetGeneratedContextPath(conn.Id);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Bottom.LinqResult.AppendPacket(new LinqDumpPacket("html",
                        $"<pre class=\"linq-err\">Connection '{System.Net.WebUtility.HtmlEncode(conn.Name)}' failed: {System.Net.WebUtility.HtmlEncode(ex.Message)}</pre>",
                        "Connection Error"));
                    Bottom.LinqResult.SetRunning(false, "Connection failed");
                    return;
                }
            }
        }

        bool isWorkspaceCtx = conn?.Kind == LinqDataSourceKind.WorkspaceDbContext;
        string? contextTypeName = conn is null
            ? null
            : isWorkspaceCtx
                ? conn.DbContextTypeName
                : LinqContextGenerator.DefaultContextTypeName;
        IReadOnlyList<string>? tableProps = isWorkspaceCtx ? null : schema?.Tables.Select(t => t.PropertyName).ToArray();
        LinqProjectedSource projected = LinqPreprocessor.Project(
            doc,
            runtimeDll,
            generatedContextFile: null,
            contextTypeName: contextTypeName,
            connectionStringEnvVar: conn is not null && !isWorkspaceCtx ? "PASSENGER_LINQ_CONNECTION" : null,
            includeRunDirectives: false,
            tablePropertyNames: tableProps,
            sourceFilePathForLineDirective: selectedBody is null ? linqPath : null);

        string tempDir = UserTemporary.Own(Path.Combine(UserTemporary.Of(StateFolder), "linqrun", FileNames.Hash(linqPath, 10)));
        string csprojPath = Path.Combine(tempDir, "LinqRunner.csproj");
        string programPath = Path.Combine(tempDir, "Program.cs");
        string runnerContextPath = Path.Combine(tempDir, "TypedDataContext.g.cs");

        StringBuilder csproj = new();
        csproj.AppendLine("<Project Sdk=\"Microsoft.NET.Sdk\">");
        csproj.AppendLine("  <PropertyGroup>");
        csproj.AppendLine("    <OutputType>Exe</OutputType>");
        csproj.AppendLine("    <TargetFramework>net11.0</TargetFramework>");
        csproj.AppendLine("    <ImplicitUsings>enable</ImplicitUsings>");
        csproj.AppendLine("    <Nullable>enable</Nullable>");
        csproj.AppendLine("    <UseAppHost>false</UseAppHost>");
        csproj.AppendLine("  </PropertyGroup>");
        csproj.AppendLine("  <ItemGroup>");
        csproj.AppendLine($"    <Reference Include=\"Passenger.Linq.Runtime\"><HintPath>{XmlEntities.Encode(runtimeDll)}</HintPath></Reference>");
        if (conn is not null)
        {
            if (conn.Kind == LinqDataSourceKind.WorkspaceDbContext)
            {
                if (!string.IsNullOrWhiteSpace(conn.DbContextProjectPath) && !string.IsNullOrWhiteSpace(conn.DbContextTypeName))
                {
                    csproj.AppendLine($"    <ProjectReference Include=\"{XmlEntities.Encode(conn.DbContextProjectPath)}\" />");
                }
            }
            else
            {
                switch (conn.Kind)
                {
                    case LinqDataSourceKind.Sqlite:
                        csproj.AppendLine("    <PackageReference Include=\"Microsoft.EntityFrameworkCore.Sqlite\" Version=\"10.0.15\" />");
                        break;
                    case LinqDataSourceKind.PostgreSql:
                        csproj.AppendLine("    <PackageReference Include=\"Npgsql.EntityFrameworkCore.PostgreSQL\" Version=\"10.0.3\" />");
                        break;
                    case LinqDataSourceKind.SqlServer:
                        csproj.AppendLine("    <PackageReference Include=\"Microsoft.EntityFrameworkCore.SqlServer\" Version=\"10.0.15\" />");
                        break;
                    case LinqDataSourceKind.MySql:
                        csproj.AppendLine("    <PackageReference Include=\"MySqlConnector\" Version=\"2.5.0\" />");
                        break;
                }
            }
        }
        foreach (string reference in doc.References)
        {
            if (Path.IsPathRooted(reference) || reference.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                string resolved = Path.IsPathRooted(reference) ? reference : Path.GetFullPath(reference, Path.GetDirectoryName(linqPath)!);
                csproj.AppendLine($"    <Reference Include=\"{XmlEntities.Encode(Path.GetFileNameWithoutExtension(resolved))}\"><HintPath>{XmlEntities.Encode(resolved)}</HintPath></Reference>");
            }
        }
        foreach (LinqPackageRef pkg in doc.Packages)
        {
            if (string.IsNullOrWhiteSpace(pkg.Version))
            {
                csproj.AppendLine($"    <PackageReference Include=\"{XmlEntities.Encode(pkg.Name)}\" Version=\"*\" />");
            }
            else
            {
                csproj.AppendLine($"    <PackageReference Include=\"{XmlEntities.Encode(pkg.Name)}\" Version=\"{XmlEntities.Encode(pkg.Version)}\" />");
            }
        }
        csproj.AppendLine("  </ItemGroup>");
        csproj.AppendLine("</Project>");

        await BackgroundWork.Run(() =>
        {
            AtomicFile.Write(csprojPath, Encoding.UTF8.GetBytes(csproj.ToString()));
            AtomicFile.Write(programPath, Encoding.UTF8.GetBytes(projected.CSharpCode));
            if (generatedContextPath is not null && File.Exists(generatedContextPath))
            {
                AtomicFile.Write(runnerContextPath, File.ReadAllBytes(generatedContextPath));
            }
            else if (File.Exists(runnerContextPath))
            {
                File.Delete(runnerContextPath);
            }
        }, token);

        linqServer?.Dispose();
        linqServer = new LinqResultServer(StateFolder);
        linqServer.PacketReceived += (_, packet) => UiThread.Post(() => Bottom.LinqResult.AppendPacket(packet));

        string workDir = Path.GetDirectoryName(linqPath) ?? tempDir;
        string? dotnetRoot = session.Configurations.Chosen is { DotnetRoot.Length: > 0 } chosenCfg
            ? chosenCfg.DotnetRoot
            : session.Configurations.All.FirstOrDefault(c => c.DotnetRoot.Length > 0)?.DotnetRoot;
        string dotnetHost = string.IsNullOrWhiteSpace(dotnetRoot)
            ? Dotnet.Host(workDir)
            : Dotnet.HostFor(workDir, dotnetRoot);

        ProcessStartInfo buildStart = ChildProcesses.DotnetCommand(dotnetHost, ["build", csprojPath, "--nologo", "-v:q"], tempDir);
        if (!string.IsNullOrWhiteSpace(dotnetRoot))
        {
            buildStart.Environment[RuntimeVariables.Root] = dotnetRoot;
        }
        buildStart.RedirectStandardOutput = true;
        buildStart.RedirectStandardError = true;
        StringBuilder buildErrors = new();
        Process buildProc = ChildProcesses.Start(buildStart);
        Task hearBuild = ChildProcesses.Hear(buildProc,
            line =>
            {
                if (line.Contains(": error ", StringComparison.Ordinal))
                {
                    buildErrors.AppendLine(line);
                }
            },
            errLine => buildErrors.AppendLine(errLine));
        int buildExit = await WaitForProcessExitAsync(buildProc, hearBuild, token);
        if (buildExit != 0)
        {
            string msg = buildErrors.Length > 0 ? buildErrors.ToString().Trim() : $"Build failed (exit code {buildExit}).";
            Bottom.LinqResult.AppendPacket(new LinqDumpPacket("html", $"<pre class=\"linq-err\">{System.Net.WebUtility.HtmlEncode(msg)}</pre>", "Build Error"));
            Bottom.LinqResult.SetRunning(false, "Build failed");
            return;
        }

        string dllPath = Path.Combine(tempDir, "bin", "Debug", "net11.0", "LinqRunner.dll");
        if (!File.Exists(dllPath))
        {
            Bottom.LinqResult.SetRunning(false, "Built assembly not found.");
            return;
        }

        Bottom.LinqResult.SetRunning(true, debug ? $"Debugging {Path.GetFileName(linqPath)}…" : $"Running {Path.GetFileName(linqPath)}…");
        Dictionary<string, string> env = new(StringComparer.Ordinal)
        {
            ["PASSENGER_LINQ_SOCKET"] = linqServer.SocketPath,
        };
        if (conn is not null)
        {
            env["PASSENGER_LINQ_CONNECTION"] = conn.Kind == LinqDataSourceKind.Sqlite && !conn.Target.Contains('=')
                ? $"Data Source={conn.Target}"
                : conn.Target;
        }

        if (debug)
        {
            Run.LaunchPlan plan = new()
            {
                Name = Path.GetFileName(linqPath),
                Program = dotnetHost,
                Arguments = [dllPath],
                Leading = 1,
                Built = dllPath,
                WorkingDirectory = workDir,
                Environment = env,
            };
            Dock.Show(ToolWindowIds.Debug);
            string? debugError = await session.Debug.StartAsync(plan);
            if (debugError is not null)
            {
                Bottom.LinqResult.AppendPacket(new LinqDumpPacket("html",
                    $"<pre class=\"linq-err\">{System.Net.WebUtility.HtmlEncode(debugError)}</pre>", "Debug Error"));
            }
            Bottom.LinqResult.SetRunning(false, debugError is null ? "Debug session started" : "Debug failed");
            return;
        }

        ProcessStartInfo runStart = ChildProcesses.WithVariables(
            ChildProcesses.DotnetCommand(dotnetHost, ["exec", dllPath], workDir),
            env);
        if (!string.IsNullOrWhiteSpace(dotnetRoot))
        {
            runStart.Environment[RuntimeVariables.Root] = dotnetRoot;
        }
        runStart.RedirectStandardOutput = true;
        runStart.RedirectStandardError = true;
        Process runProc = ChildProcesses.Start(runStart);
        linqProcess = runProc;

        StringBuilder stdErr = new();
        Task hearRun = ChildProcesses.Hear(runProc,
            line =>
            {
                if (!string.IsNullOrEmpty(line))
                {
                    UiThread.Post(() => Bottom.LinqResult.AppendPacket(new LinqDumpPacket("html",
                        $"<pre>{System.Net.WebUtility.HtmlEncode(line)}</pre>", null)));
                }
            },
            errLine => stdErr.AppendLine(errLine));

        try
        {
            await Task.WhenAll(runProc.WaitForExitAsync(token), hearRun);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (ReferenceEquals(linqProcess, runProc))
            {
                linqProcess = null;
            }
        }

        int exitCode = !DotnetProcesses.Runs(runProc) ? runProc.ExitCode : -1;
        if (stdErr.Length > 0)
        {
            Bottom.LinqResult.AppendPacket(new LinqDumpPacket("html",
                $"<pre class=\"linq-err\">{System.Net.WebUtility.HtmlEncode(stdErr.ToString().Trim())}</pre>", "Stderr"));
        }
        Bottom.LinqResult.SetRunning(false, token.IsCancellationRequested ? "Stopped" : $"Finished (exit {exitCode})");
    }

    private async Task RunActiveSqlAsync(string sqlPath, string? selectedSql)
    {
        LinqConnectionInfo? conn = linqConnectionsView?.SelectedConnection ?? linqConnections.Connections.FirstOrDefault();
        if (conn is null)
        {
            Dock.Show(ToolWindowIds.LinqConnections);
            Say("Select or add a Data Connection first.");
            return;
        }

        StopActiveLinq();
        linqCts?.Dispose();
        linqCts = new CancellationTokenSource();
        CancellationToken token = linqCts.Token;

        Dock.Show(ToolWindowIds.LinqResult);
        Bottom.LinqResult.Clear();
        Bottom.LinqResult.SetRunning(true, $"Running SQL on {conn.Name}…");

        try
        {
            string sql = selectedSql ?? await BackgroundWork.Run(() => File.ReadAllText(sqlPath, Encoding.UTF8), token);
            if (string.IsNullOrWhiteSpace(sql))
            {
                Bottom.LinqResult.SetRunning(false, "Empty SQL");
                return;
            }
            Bottom.LinqResult.AppendPacket(new LinqDumpPacket("sql", sql, conn.Name));

            string? html = await BackgroundWork.Run(() => ExecuteSqlToHtmlAsync(conn, sql, token), token);
            if (!string.IsNullOrEmpty(html))
            {
                Bottom.LinqResult.AppendPacket(new LinqDumpPacket("html", html, $"{conn.Name} ({conn.Kind})"));
            }
            Bottom.LinqResult.SetRunning(false, "Finished");
        }
        catch (OperationCanceledException)
        {
            Bottom.LinqResult.SetRunning(false, "Stopped");
        }
        catch (Exception ex) when (ex is System.Data.Common.DbException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            Bottom.LinqResult.AppendPacket(new LinqDumpPacket("html",
                $"<pre class=\"linq-err\">{System.Net.WebUtility.HtmlEncode(ex.Message)}</pre>", "SQL Error"));
            Bottom.LinqResult.SetRunning(false, "SQL Error");
        }
    }

    private static async Task<string> ExecuteSqlToHtmlAsync(LinqConnectionInfo conn, string sql, CancellationToken token)
    {
        using System.Data.Common.DbConnection db = conn.Kind switch
        {
            LinqDataSourceKind.Sqlite => new Microsoft.Data.Sqlite.SqliteConnection(
                conn.Target.Contains('=') ? conn.Target : $"Data Source={conn.Target}"),
            LinqDataSourceKind.PostgreSql => new Npgsql.NpgsqlConnection(conn.Target),
            LinqDataSourceKind.SqlServer => new Microsoft.Data.SqlClient.SqlConnection(conn.Target),
            LinqDataSourceKind.MySql => new MySqlConnector.MySqlConnection(conn.Target),
            _ => throw new NotSupportedException($"SQL execution is not supported for {conn.Kind} connections."),
        };
        await db.OpenAsync(token).ConfigureAwait(false);
        using System.Data.Common.DbCommand cmd = db.CreateCommand();
        cmd.CommandText = sql;
        using System.Data.Common.DbDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false);

        StringBuilder sb = new();
        int setIndex = 0;
        do
        {
            setIndex++;
            if (reader.FieldCount == 0)
            {
                if (reader.RecordsAffected >= 0)
                {
                    sb.Append($"<div class=\"lp-empty\">({reader.RecordsAffected} row(s) affected)</div>");
                }
                continue;
            }

            List<string[]> rows = [];
            const int maxRows = 1000;
            bool truncated = false;
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                if (rows.Count >= maxRows)
                {
                    truncated = true;
                    break;
                }
                string[] row = new string[reader.FieldCount];
                for (int i = 0; i < reader.FieldCount; i++)
                {
                    row[i] = reader.IsDBNull(i) ? "null" : Convert.ToString(reader.GetValue(i), System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
                }
                rows.Add(row);
            }

            sb.Append("<details class=\"lp-box\" open>");
            sb.Append($"<summary><span class=\"lp-type\">ResultSet #{setIndex}</span> <span class=\"lp-count\">({rows.Count}{(truncated ? "+" : "")} items)</span></summary>");
            sb.Append("<table class=\"lp-table\"><thead><tr>");
            for (int i = 0; i < reader.FieldCount; i++)
            {
                string colName = System.Net.WebUtility.HtmlEncode(reader.GetName(i));
                string typeName = System.Net.WebUtility.HtmlEncode(reader.GetDataTypeName(i));
                sb.Append($"<th>{colName}<div class=\"lp-coltype\">{typeName}</div></th>");
            }
            sb.Append("</tr></thead><tbody>");
            foreach (string[] row in rows)
            {
                sb.Append("<tr>");
                for (int i = 0; i < row.Length; i++)
                {
                    string val = row[i];
                    if (val == "null")
                    {
                        sb.Append("<td class=\"lp-null\">null</td>");
                    }
                    else
                    {
                        sb.Append($"<td>{System.Net.WebUtility.HtmlEncode(val)}</td>");
                    }
                }
                sb.Append("</tr>");
            }
            sb.Append("</tbody></table>");
            if (truncated)
            {
                sb.Append($"<div class=\"lp-more\">Showing first {maxRows} rows…</div>");
            }
            sb.Append("</details>");
        }
        while (await reader.NextResultAsync(token).ConfigureAwait(false));

        return sb.ToString();
    }

    private static async Task<int> WaitForProcessExitAsync(Process process, Task hearTask, CancellationToken token)
    {
        try
        {
            await Task.WhenAll(process.WaitForExitAsync(token), hearTask);
            return process.ExitCode;
        }
        catch (OperationCanceledException)
        {
            return -1;
        }
    }
}
