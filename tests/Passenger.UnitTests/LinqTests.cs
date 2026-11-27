using System.IO.Compression;
using System.Text;
using Passenger.Linq;
using Passenger.Linq.Runtime;

namespace Passenger.UnitTests;

public sealed class LinqTests
{
    [Fact]
    public void Parse_reads_linq_header_packages_namespaces_and_body_start()
    {
        string raw = """
            <Query Kind="Statements">
              <Connection>
                <ID>conn-1</ID>
                <Name>SalesDb</Name>
              </Connection>
              <Reference>&lt;RuntimeDirectory&gt;\System.Net.Http.dll</Reference>
              <NuGetReference Version="8.0.0">Newtonsoft.Json</NuGetReference>
              <Namespace>System.Text.Json</Namespace>
            </Query>

            var x = new[] { 1, 2, 3 };
            x.Dump("Numbers");
            """;

        LinqDocument doc = LinqDocument.Parse(raw);

        Assert.Equal(LinqQueryKind.Statements, doc.Kind);
        Assert.Equal("conn-1", doc.ConnectionId);
        Assert.Equal("SalesDb", doc.ConnectionName);
        Assert.Single(doc.References);
        Assert.Single(doc.Packages);
        Assert.Equal("Newtonsoft.Json", doc.Packages[0].Name);
        Assert.Equal("8.0.0", doc.Packages[0].Version);
        Assert.Contains("System.Text.Json", doc.Namespaces);
        Assert.True(doc.BodyStartLine >= 8);
        Assert.Contains("x.Dump(\"Numbers\")", doc.Body);
    }

    [Fact]
    public void Preprocessor_projects_expression_and_maps_lines_roundtrip()
    {
        string raw = "<Query Kind=\"Expression\" />\nEnumerable.Range(1, 5)\n    .Select(x => x * 2)";
        LinqDocument doc = LinqDocument.Parse(raw);

        LinqProjectedSource projected = LinqPreprocessor.Project(
            doc,
            runtimeAssemblyPath: "Passenger.Linq.Runtime.dll",
            generatedContextFile: null,
            contextTypeName: "TypedDataContext",
            connectionStringEnvVar: "PASSENGER_LINQ_CONNECTION",
            includeRunDirectives: false,
            tablePropertyNames: ["Orders", "Customers"],
            sourceFilePathForLineDirective: @"C:\work\test.linq");

        Assert.Contains("var Orders = __ctx.Orders;", projected.CSharpCode);
        Assert.Contains("var Customers = __ctx.Customers;", projected.CSharpCode);
        int usingStaticIdx = projected.CSharpCode.IndexOf("using static Passenger.Linq.Runtime.LinqExtensions;", StringComparison.Ordinal);
        int ctxIdx = projected.CSharpCode.IndexOf("using var __ctx =", StringComparison.Ordinal);
        Assert.True(usingStaticIdx >= 0 && ctxIdx > usingStaticIdx, "using static must precede using var __ctx");
        Assert.Contains("#line ", projected.CSharpCode);
        Assert.Contains("test.linq\"", projected.CSharpCode);
        Assert.Contains("Dump(", projected.CSharpCode);

        LinqDocument bareExpr = LinqDocument.Parse("AbpRoles;");
        Assert.Equal(LinqQueryKind.Expression, bareExpr.Kind);
        LinqProjectedSource bareProj = LinqPreprocessor.Project(bareExpr, "Passenger.Linq.Runtime.dll");
        Assert.Contains("var __linqResult = (\nAbpRoles\n);\n__linqResult.Dump();", bareProj.CSharpCode.Replace("\r\n", "\n"));

        int genLine = projected.ToGeneratedLine(doc.BodyStartLine)!.Value;
        Assert.Equal(doc.BodyStartLine, projected.ToLinqLine(genLine));
    }

    [Fact]
    public async Task SchemaProber_and_ContextGenerator_handle_csv_and_xlsx()
    {
        string dir = Path.Combine(Path.GetTempPath(), "passenger-linq-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            string csvPath = Path.Combine(dir, "orders.csv");
            await File.WriteAllTextAsync(csvPath, "Id,Customer,Amount,IsPaid\n1,Alice,42.5,true\n2,Bob,10,false\n", Encoding.UTF8, TestContext.Current.CancellationToken);

            LinqDatabaseSchema csvSchema = await LinqSchemaProber.ProbeAsync(new LinqConnectionInfo("c1", "OrdersCsv", LinqDataSourceKind.Csv, csvPath), TestContext.Current.CancellationToken);
            Assert.Single(csvSchema.Tables);
            Assert.Equal(4, csvSchema.Tables[0].Columns.Count);
            Assert.Equal("long", csvSchema.Tables[0].Columns[0].ClrTypeName);
            Assert.Equal("decimal", csvSchema.Tables[0].Columns[2].ClrTypeName);
            Assert.Equal("bool", csvSchema.Tables[0].Columns[3].ClrTypeName);

            string csvCtx = LinqContextGenerator.Generate(csvSchema);
            Assert.Contains("public sealed class TypedDataContext : IDisposable", csvCtx);
            Assert.Contains(csvSchema.Tables[0].PropertyName, csvCtx);

            string xlsxPath = Path.Combine(dir, "sales.xlsx");
            CreateMinimalXlsx(xlsxPath);
            LinqDatabaseSchema xlsxSchema = await LinqSchemaProber.ProbeAsync(new LinqConnectionInfo("x1", "SalesXlsx", LinqDataSourceKind.Xlsx, xlsxPath), TestContext.Current.CancellationToken);
            Assert.Single(xlsxSchema.Tables);
            Assert.Equal("Sheet1", xlsxSchema.Tables[0].TableName);
            Assert.Equal(2, xlsxSchema.Tables[0].Columns.Count);

            string xlsxCtx = LinqContextGenerator.Generate(xlsxSchema);
            Assert.Contains("LoadSheet", xlsxCtx);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void DumpHtmlRenderer_renders_nested_collections_and_tables()
    {
        var data = new[]
        {
            new { Id = 1, Name = "Alpha", Tags = new[] { "a", "b" } },
            new { Id = 2, Name = "Beta", Tags = new[] { "c" } },
        };

        string html = DumpHtmlRenderer.Render(data, "Items");
        Assert.Contains("lp-dump", html);
        Assert.Contains("Items", html);
        Assert.Contains("Alpha", html);
        Assert.Contains("Beta", html);
        Assert.Contains("<table", html);
    }

    [Fact]
    public async Task SchemaProber_and_ContextGenerator_handle_json_and_mysql_and_filters()
    {
        string dir = Path.Combine(Path.GetTempPath(), "passenger-linq-json-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            string jsonPath = Path.Combine(dir, "products.json");
            await File.WriteAllTextAsync(jsonPath, """[{"Id":1,"Title":"Pen","Price":1.5,"Active":true},{"Id":2,"Title":"Book","Price":12,"Active":false}]""",
                Encoding.UTF8, TestContext.Current.CancellationToken);

            LinqDatabaseSchema jsonSchema = await LinqSchemaProber.ProbeAsync(
                new LinqConnectionInfo("j1", "ProductsJson", LinqDataSourceKind.Json, jsonPath),
                TestContext.Current.CancellationToken);
            Assert.Single(jsonSchema.Tables);
            Assert.Equal(4, jsonSchema.Tables[0].Columns.Count);
            Assert.Equal("long", jsonSchema.Tables[0].Columns[0].ClrTypeName);
            Assert.Equal("decimal", jsonSchema.Tables[0].Columns[2].ClrTypeName);
            Assert.Equal("bool", jsonSchema.Tables[0].Columns[3].ClrTypeName);

            string jsonCtx = LinqContextGenerator.Generate(jsonSchema);
            Assert.Contains("JsonDocument.Parse", jsonCtx);
            Assert.Contains("public sealed class TypedDataContext : IDisposable", jsonCtx);

            LinqConnectionInfo mysqlConn = new(
                "m1",
                "ShopMySql",
                LinqDataSourceKind.MySql,
                "Server=localhost;Database=shop;Uid=root;",
                IncludeSchemasRegex: "^sales$",
                ExcludeTablesRegex: "^__EF");

            LinqDatabaseSchema rawSqlSchema = new(
                mysqlConn.Id,
                mysqlConn.Name,
                mysqlConn.Kind,
                [
                    new LinqTableSchema("sales", "__EFMigrationsHistory", "EfMigrationsHistory", "EfMigrationsHistoryRow", [new LinqColumnSchema("MigrationId", "MigrationId", "string", false, true)]),
                    new LinqTableSchema("sales", "Orders", "Orders", "OrdersRow", [new LinqColumnSchema("Id", "Id", "long", false, true)]),
                    new LinqTableSchema("audit", "Logs", "Logs", "LogsRow", [new LinqColumnSchema("Id", "Id", "long", false, true)]),
                ]);

            LinqDatabaseSchema filtered = LinqSchemaProber.ApplyFilters(rawSqlSchema, mysqlConn);
            Assert.Single(filtered.Tables);
            Assert.Equal("Orders", filtered.Tables[0].TableName);

            string mysqlCtx = LinqContextGenerator.Generate(filtered);
            Assert.Contains("MySqlConnection", mysqlCtx);
            Assert.Contains("Orders", mysqlCtx);

            LinqDatabaseSchema compositePkSchema = new(
                "s1",
                "CompositePkDb",
                LinqDataSourceKind.SqlServer,
                [
                    new LinqTableSchema(
                        "dbo",
                        "verification_state",
                        "verification_state",
                        "verification_stateRow",
                        [
                            new LinqColumnSchema("TenantId", "TenantId", "string", false, true),
                            new LinqColumnSchema("Key", "Key", "string", false, true),
                        ]),
                ]);
            string compositeCtx = LinqContextGenerator.Generate(compositePkSchema);
            Assert.Contains("[PrimaryKey(nameof(TenantId), nameof(Key))]", compositeCtx);
            Assert.DoesNotContain("[Key]", compositeCtx);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task WorkspaceDiscovery_finds_appsettings_connection_strings_and_dbcontexts()
    {
        string dir = Path.Combine(Path.GetTempPath(), "passenger-linq-disc-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(dir, "appsettings.Development.json"),
                """
                {
                  "ConnectionStrings": {
                    "Default": "Server=(localdb)\\mssqllocaldb;Database=AppDb;Trusted_Connection=True;",
                    "Postgres": "Host=localhost;Database=app;Username=postgres;Password=secret"
                  }
                }
                """, Encoding.UTF8, TestContext.Current.CancellationToken);

            IReadOnlyList<Passenger.App.Linq.DiscoveredConnectionString> csList = Passenger.App.Linq.LinqWorkspaceDiscovery.FindConnectionStrings(dir);
            Assert.Equal(2, csList.Count);
            Assert.Contains(csList, c => c.Name == "Default" && c.GuessedKind == LinqDataSourceKind.SqlServer);
            Assert.Contains(csList, c => c.Name == "Postgres" && c.GuessedKind == LinqDataSourceKind.PostgreSql);

            string projDir = Path.Combine(dir, "Data");
            Directory.CreateDirectory(projDir);
            string csproj = Path.Combine(projDir, "Data.csproj");
            await File.WriteAllTextAsync(csproj, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net11.0</TargetFramework></PropertyGroup></Project>",
                Encoding.UTF8, TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(Path.Combine(projDir, "AppDbContext.cs"),
                """
                using Microsoft.EntityFrameworkCore;
                namespace MyApp.Data;
                public class AppDbContext : DbContext { }
                """, Encoding.UTF8, TestContext.Current.CancellationToken);

            Passenger.Workspace.ProjectModel projModel = new(
                csproj,
                "Data",
                "Library",
                ["net11.0"],
                [],
                [],
                Exists: true);
            IReadOnlyList<Passenger.App.Linq.DiscoveredDbContext> contexts = Passenger.App.Linq.LinqWorkspaceDiscovery.FindDbContexts(dir, [projModel]);
            Assert.Single(contexts);
            Assert.Equal("MyApp.Data.AppDbContext", contexts[0].FullTypeName);
            Assert.Equal("Data", contexts[0].ProjectName);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static void CreateMinimalXlsx(string path)
    {
        using FileStream fs = File.Create(path);
        using ZipArchive zip = new(fs, ZipArchiveMode.Create);

        WriteEntry(zip, "[Content_Types].xml",
            """<?xml version="1.0" encoding="UTF-8"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="xml" ContentType="application/xml"/><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/><Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/><Override PartName="/xl/sharedStrings.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sharedStrings+xml"/></Types>""");
        WriteEntry(zip, "_rels/.rels",
            """<?xml version="1.0" encoding="UTF-8"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/></Relationships>""");
        WriteEntry(zip, "xl/_rels/workbook.xml.rels",
            """<?xml version="1.0" encoding="UTF-8"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/><Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/sharedStrings" Target="sharedStrings.xml"/></Relationships>""");
        WriteEntry(zip, "xl/workbook.xml",
            """<?xml version="1.0" encoding="UTF-8"?><workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets><sheet name="Sheet1" sheetId="1" r:id="rId1"/></sheets></workbook>""");
        WriteEntry(zip, "xl/sharedStrings.xml",
            """<?xml version="1.0" encoding="UTF-8"?><sst xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><si><t>Product</t></si><si><t>Qty</t></si><si><t>Widget</t></si></sst>""");
        WriteEntry(zip, "xl/worksheets/sheet1.xml",
            """<?xml version="1.0" encoding="UTF-8"?><worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData><row r="1"><c r="A1" t="s"><v>0</v></c><c r="B1" t="s"><v>1</v></c></row><row r="2"><c r="A2" t="s"><v>2</v></c><c r="B2"><v>5</v></c></row></sheetData></worksheet>""");
    }

    private static void WriteEntry(ZipArchive zip, string name, string xml)
    {
        ZipArchiveEntry entry = zip.CreateEntry(name);
        using StreamWriter writer = new(entry.Open(), Encoding.UTF8);
        writer.Write(xml);
    }
}
