using System.Net;
using System.Text;
using Passenger.App.Editors;

namespace Passenger.App.Linq;

/// <summary>Builds the self-contained HTML page shown in <see cref="ToolWindows.LinqResultView"/> with LINQPad-style
/// collapsible nested tables, SQL tab, and raw HTML/text log.</summary>
public static class LinqResultHtml
{
    public const string VirtualFileName = "linq-result.html";

    public static string BuildPage(IReadOnlyList<LinqDumpPacket> packets, string? status, bool isDark)
    {
        StringBuilder dumpBody = new();
        StringBuilder sqlBody = new();
        int dumpCount = 0;
        int sqlCount = 0;

        foreach (LinqDumpPacket packet in packets)
        {
            if (string.Equals(packet.Kind, "sql", StringComparison.OrdinalIgnoreCase))
            {
                sqlCount++;
                sqlBody.Append("<pre class=\"sql-entry\">").Append(WebUtility.HtmlEncode(packet.Content)).Append("</pre>\n");
            }
            else
            {
                dumpCount++;
                dumpBody.Append("<div class=\"dump-block\">\n");
                if (!string.IsNullOrWhiteSpace(packet.Title))
                {
                    dumpBody.Append("<div class=\"dump-title\">").Append(WebUtility.HtmlEncode(packet.Title)).Append("</div>\n");
                }
                dumpBody.Append(packet.Content).Append("\n</div>\n");
            }
        }

        if (dumpCount == 0 && sqlCount == 0)
        {
            dumpBody.Append("<div class=\"empty-hint\">")
                .Append(WebUtility.HtmlEncode(status ?? "Run a .linq query and call .Dump() to inspect results here."))
                .Append("</div>");
        }

        string bg = isDark ? "#131722" : "#ffffff";
        string fg = isDark ? "#dce3f0" : "#1e2430";
        string border = isDark ? "#383c44" : "#d4d4d4";
        string thBorder = isDark ? "#5a5f69" : "#bdbdbd";
        string thBg = isDark ? "#484b52" : "#eeeeee";
        string thFg = isDark ? "#e6eaf2" : "#1e2430";
        string typeFg = isDark ? "#7aa2f7" : "#2563eb";
        string muted = isDark ? "#8b95ab" : "#64748b";
        string hover = isDark ? "#1e2638" : "#f8fafc";
        string nullFg = isDark ? "#7c8599" : "#94a3b8";

        return $$"""
            <!DOCTYPE html>
            <html lang="en">
            <head>
            <meta charset="utf-8" />
            <style>
              :root { color-scheme: {{(isDark ? "dark" : "light")}}; }
              * { box-sizing: border-box; }
              html, body { margin: 0; padding: 0; background: {{bg}}; color: {{fg}}; font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, sans-serif; font-size: 12.5px; line-height: 1.4; }
              .toolbar { position: sticky; top: 0; z-index: 10; display: flex; align-items: center; gap: 10px; padding: 6px 12px; background: {{thBg}}; border-bottom: 1px solid {{thBorder}}; }
              .tab { cursor: pointer; padding: 3px 10px; border-radius: 5px; font-weight: 600; color: {{muted}}; user-select: none; }
              .tab.active { background: {{bg}}; color: {{fg}}; border: 1px solid {{border}}; }
              .status { margin-left: auto; color: {{muted}}; font-size: 11.5px; }
              .panel { padding: 10px 12px; display: none; }
              .panel.active { display: block; }
              .dump-block { margin-bottom: 12px; }
              .dump-title { font-weight: 600; color: {{typeFg}}; margin-bottom: 4px; }
              .empty-hint { color: {{muted}}; padding: 24px 8px; text-align: center; }
              table.linq-table, table.lp-table { border-collapse: collapse; border: 1px solid {{border}}; margin: 2px 0; font-size: 12px; }
              table.linq-table th, table.linq-table td,
              table.lp-table th, table.lp-table td { border-left: 1px solid {{border}}; border-right: 1px solid {{border}}; border-top: none; border-bottom: 1px dashed {{border}}; padding: 3px 8px; text-align: left; vertical-align: top; }
              table.linq-table th, table.lp-table th { background: {{thBg}}; color: {{thFg}}; font-weight: 600; border-bottom: 1px solid {{thBorder}}; }
              table.lp-table thead th { border-bottom: 1px solid {{thBorder}}; }
              table.linq-table tr.linq-head > td.linq-type { border-bottom: 1px solid {{thBorder}}; }
              table.linq-table tbody > tr:last-child > td,
              table.lp-table tbody > tr:last-child > td { border-bottom: 1px solid {{border}}; }
              table.linq-table tr:hover > td,
              table.lp-table tr:hover > td { background: {{hover}}; }
              .linq-type { cursor: pointer; background: {{thBg}}; color: {{typeFg}}; font-weight: 600; font-family: Consolas, "Cascadia Mono", monospace; }
              .linq-type::before { content: "▾ "; font-size: 10px; }
              table.linq-collapsed .linq-type::before { content: "▸ "; }
              table.linq-collapsed > tbody > tr:not(.linq-head) { display: none; }
              details.lp-box { border: 1px solid {{border}}; border-radius: 3px; margin: 2px 0; display: inline-block; min-width: 200px; }
              details.lp-box > summary { cursor: pointer; padding: 3px 8px; background: {{thBg}}; color: {{thFg}}; font-weight: 600; border-bottom: 1px solid {{thBorder}}; user-select: none; list-style: none; }
              details.lp-box > summary::-webkit-details-marker { display: none; }
              details.lp-box:not([open]) > summary { border-bottom: none; }
              details.lp-box > summary::before { content: "▾ "; font-size: 10px; color: {{typeFg}}; }
              details.lp-box:not([open]) > summary::before { content: "▸ "; }
              .lp-type { color: {{typeFg}}; font-family: Consolas, "Cascadia Mono", monospace; }
              .lp-count { color: {{muted}}; font-weight: 400; font-size: 11px; }
              .lp-coltype { color: {{muted}}; font-weight: 400; font-size: 10.5px; font-family: Consolas, "Cascadia Mono", monospace; }
              .lp-idx { color: {{muted}}; text-align: right; font-variant-numeric: tabular-nums; width: 1%; white-space: nowrap; }
              .lp-kv th { text-align: left; white-space: nowrap; }
              .lp-null, .linq-null { color: {{nullFg}}; font-style: italic; }
              .lp-num, .linq-num { font-variant-numeric: tabular-nums; text-align: right; }
              .lp-bool, .linq-bool { color: {{typeFg}}; font-weight: 600; }
              .lp-empty, .lp-more { color: {{muted}}; padding: 4px 8px; font-style: italic; font-size: 11.5px; }
              .linq-err { color: #ef4444; }
              pre.sql-entry { background: {{thBg}}; border: 1px solid {{border}}; border-radius: 6px; padding: 8px 10px; margin: 6px 0; white-space: pre-wrap; font-family: Consolas, "Cascadia Mono", monospace; font-size: 12px; }
            </style>
            </head>
            <body>
              <div class="toolbar">
                <div class="tab active" data-tab="results">Results ({{dumpCount}})</div>
                <div class="tab" data-tab="sql">SQL ({{sqlCount}})</div>
                <div class="status">{{WebUtility.HtmlEncode(status ?? string.Empty)}}</div>
              </div>
              <div id="panel-results" class="panel active">{{dumpBody}}</div>
              <div id="panel-sql" class="panel">{{(sqlCount > 0 ? sqlBody.ToString() : "<div class=\"empty-hint\">No SQL statements logged.</div>")}}</div>
              <script>
                document.querySelectorAll('.tab').forEach(tab => {
                  tab.addEventListener('click', () => {
                    document.querySelectorAll('.tab, .panel').forEach(el => el.classList.remove('active'));
                    tab.classList.add('active');
                    document.getElementById('panel-' + tab.dataset.tab).classList.add('active');
                  });
                });
                function toggleLinqTable(headerCell) {
                  const table = headerCell.closest('table.linq-table');
                  if (table) table.classList.toggle('linq-collapsed');
                }
                document.querySelectorAll('table.linq-table').forEach(t => {
                  const head = t.querySelector(':scope > tbody > tr:first-child');
                  if (head) head.classList.add('linq-head');
                });
              </script>
            </body>
            </html>
            """;
    }
}
