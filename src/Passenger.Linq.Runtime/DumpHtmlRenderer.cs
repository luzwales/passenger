using System.Collections;
using System.Data;
using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace Passenger.Linq.Runtime;

/// <summary>Renders any .NET object graph into LINQPad-style nested collapsible HTML tables.</summary>
public static class DumpHtmlRenderer
{
    public const int DefaultMaxDepth = 5;
    public const int DefaultMaxItems = 200;

    public static string Render<T>(T value, string? description = null, int maxDepth = DefaultMaxDepth, int maxItems = DefaultMaxItems)
    {
        StringBuilder sb = new();
        HashSet<object> visited = new(ReferenceEqualityComparer.Instance);
        sb.Append("<div class=\"lp-dump\">");
        if (!string.IsNullOrWhiteSpace(description))
        {
            sb.Append("<div class=\"lp-heading\">").Append(WebUtility.HtmlEncode(description)).Append("</div>");
        }
        RenderValue(sb, value, 0, Math.Clamp(maxDepth, 1, 16), Math.Clamp(maxItems, 1, 5000), visited);
        sb.Append("</div>");
        return sb.ToString();
    }

    private static void RenderValue(StringBuilder sb, object? value, int depth, int maxDepth, int maxItems, HashSet<object> visited)
    {
        if (value is null)
        {
            sb.Append("<span class=\"lp-null\">null</span>");
            return;
        }

        if (value is string s)
        {
            sb.Append("<span class=\"lp-string\">").Append(WebUtility.HtmlEncode(s)).Append("</span>");
            return;
        }

        Type type = value.GetType();
        if (IsScalar(type))
        {
            sb.Append("<span class=\"lp-scalar\">").Append(WebUtility.HtmlEncode(FormatScalar(value))).Append("</span>");
            return;
        }

        if (value is JsonElement json)
        {
            sb.Append("<pre class=\"lp-json\">").Append(WebUtility.HtmlEncode(json.ToString())).Append("</pre>");
            return;
        }

        if (value is Exception ex)
        {
            RenderException(sb, ex);
            return;
        }

        if (depth >= maxDepth)
        {
            sb.Append("<span class=\"lp-truncated\" title=\"Max depth reached\">").Append(WebUtility.HtmlEncode(FormatTypeName(type))).Append(" (…)</span>");
            return;
        }

        if (!type.IsValueType && !visited.Add(value))
        {
            sb.Append("<span class=\"lp-cycle\">↺ ").Append(WebUtility.HtmlEncode(FormatTypeName(type))).Append("</span>");
            return;
        }

        try
        {
            if (value is DataTable table)
            {
                RenderDataTable(sb, table, depth, maxDepth, maxItems, visited);
                return;
            }

            if (value is DataSet dataSet)
            {
                RenderDataSet(sb, dataSet, depth, maxDepth, maxItems, visited);
                return;
            }

            if (value is IDictionary dict)
            {
                RenderDictionary(sb, dict, type, depth, maxDepth, maxItems, visited);
                return;
            }

            if (value is IEnumerable enumerable)
            {
                RenderEnumerable(sb, enumerable, type, depth, maxDepth, maxItems, visited);
                return;
            }

            RenderObject(sb, value, type, depth, maxDepth, maxItems, visited);
        }
        finally
        {
            if (!type.IsValueType)
            {
                visited.Remove(value);
            }
        }
    }

    private static void RenderException(StringBuilder sb, Exception ex)
    {
        sb.Append("<div class=\"lp-error\">");
        sb.Append("<div class=\"lp-error-title\">").Append(WebUtility.HtmlEncode(ex.GetType().FullName ?? ex.GetType().Name))
          .Append(": ").Append(WebUtility.HtmlEncode(ex.Message)).Append("</div>");
        if (!string.IsNullOrEmpty(ex.StackTrace))
        {
            sb.Append("<pre class=\"lp-stack\">").Append(WebUtility.HtmlEncode(ex.StackTrace)).Append("</pre>");
        }
        if (ex.InnerException is not null)
        {
            sb.Append("<div class=\"lp-inner\">InnerException:</div>");
            RenderException(sb, ex.InnerException);
        }
        sb.Append("</div>");
    }

    private static void RenderDataSet(StringBuilder sb, DataSet dataSet, int depth, int maxDepth, int maxItems, HashSet<object> visited)
    {
        sb.Append("<details open class=\"lp-box\"><summary><span class=\"lp-type\">DataSet</span>")
          .Append(string.IsNullOrEmpty(dataSet.DataSetName) ? "" : " " + WebUtility.HtmlEncode(dataSet.DataSetName))
          .Append($" <span class=\"lp-count\">({dataSet.Tables.Count} tables)</span></summary>");
        foreach (DataTable table in dataSet.Tables)
        {
            sb.Append("<div class=\"lp-subtable\"><div class=\"lp-heading\">").Append(WebUtility.HtmlEncode(table.TableName)).Append("</div>");
            RenderDataTable(sb, table, depth + 1, maxDepth, maxItems, visited);
            sb.Append("</div>");
        }
        sb.Append("</details>");
    }

    private static void RenderDataTable(StringBuilder sb, DataTable table, int depth, int maxDepth, int maxItems, HashSet<object> visited)
    {
        int total = table.Rows.Count;
        sb.Append("<details open class=\"lp-box\"><summary><span class=\"lp-type\">DataTable</span>")
          .Append(string.IsNullOrEmpty(table.TableName) ? "" : " " + WebUtility.HtmlEncode(table.TableName))
          .Append($" <span class=\"lp-count\">({total} rows)</span></summary>");
        sb.Append("<table class=\"lp-table\"><thead><tr>");
        foreach (DataColumn col in table.Columns)
        {
            sb.Append("<th>").Append(WebUtility.HtmlEncode(col.ColumnName))
              .Append("<div class=\"lp-coltype\">").Append(WebUtility.HtmlEncode(FormatTypeName(col.DataType))).Append("</div></th>");
        }
        sb.Append("</tr></thead><tbody>");
        int shown = 0;
        foreach (DataRow row in table.Rows)
        {
            if (shown >= maxItems)
            {
                break;
            }
            sb.Append("<tr>");
            for (int i = 0; i < table.Columns.Count; i++)
            {
                object? cell = row[i];
                if (cell == DBNull.Value)
                {
                    cell = null;
                }
                sb.Append("<td>");
                RenderValue(sb, cell, depth + 1, maxDepth, maxItems, visited);
                sb.Append("</td>");
            }
            sb.Append("</tr>");
            shown++;
        }
        sb.Append("</tbody></table>");
        if (total > shown)
        {
            sb.Append($"<div class=\"lp-more\">… {total - shown} more rows omitted (showing first {shown})</div>");
        }
        sb.Append("</details>");
    }

    private static void RenderDictionary(StringBuilder sb, IDictionary dict, Type type, int depth, int maxDepth, int maxItems, HashSet<object> visited)
    {
        int count = dict.Count;
        sb.Append("<details open class=\"lp-box\"><summary><span class=\"lp-type\">")
          .Append(WebUtility.HtmlEncode(FormatTypeName(type)))
          .Append($"</span> <span class=\"lp-count\">({count} items)</span></summary>");
        if (count == 0)
        {
            sb.Append("<div class=\"lp-empty\">(empty)</div></details>");
            return;
        }
        sb.Append("<table class=\"lp-table\"><thead><tr><th>Key</th><th>Value</th></tr></thead><tbody>");
        int shown = 0;
        foreach (DictionaryEntry entry in dict)
        {
            if (shown >= maxItems)
            {
                break;
            }
            sb.Append("<tr><td>");
            RenderValue(sb, entry.Key, depth + 1, maxDepth, maxItems, visited);
            sb.Append("</td><td>");
            RenderValue(sb, entry.Value, depth + 1, maxDepth, maxItems, visited);
            sb.Append("</td></tr>");
            shown++;
        }
        sb.Append("</tbody></table>");
        if (count > shown)
        {
            sb.Append($"<div class=\"lp-more\">… {count - shown} more items omitted</div>");
        }
        sb.Append("</details>");
    }

    private static void RenderEnumerable(StringBuilder sb, IEnumerable enumerable, Type type, int depth, int maxDepth, int maxItems, HashSet<object> visited)
    {
        List<object?> items = [];
        bool truncated = false;
        int count = 0;
        foreach (object? item in enumerable)
        {
            if (count >= maxItems)
            {
                truncated = true;
                break;
            }
            items.Add(item);
            count++;
        }

        string countLabel = truncated ? $"({items.Count}+ items)" : $"({items.Count} items)";
        sb.Append("<details open class=\"lp-box\"><summary><span class=\"lp-type\">")
          .Append(WebUtility.HtmlEncode(FormatTypeName(type)))
          .Append($"</span> <span class=\"lp-count\">{countLabel}</span></summary>");

        if (items.Count == 0)
        {
            sb.Append("<div class=\"lp-empty\">(empty)</div></details>");
            return;
        }

        Type? elemType = GetElementType(type, items);
        if (elemType is not null && !IsScalar(elemType) && elemType != typeof(string) && !typeof(IEnumerable).IsAssignableFrom(elemType))
        {
            MemberInfo[] members = GetDumpMembers(elemType);
            if (members.Length > 0)
            {
                sb.Append("<table class=\"lp-table\"><thead><tr><th class=\"lp-idx\">#</th>");
                foreach (MemberInfo m in members)
                {
                    sb.Append("<th>").Append(WebUtility.HtmlEncode(m.Name))
                      .Append("<div class=\"lp-coltype\">").Append(WebUtility.HtmlEncode(FormatTypeName(GetMemberType(m)))).Append("</div></th>");
                }
                sb.Append("</tr></thead><tbody>");
                for (int i = 0; i < items.Count; i++)
                {
                    object? item = items[i];
                    sb.Append($"<tr><td class=\"lp-idx\">{i}</td>");
                    if (item is null)
                    {
                        sb.Append($"<td colspan=\"{members.Length}\"><span class=\"lp-null\">null</span></td>");
                    }
                    else
                    {
                        foreach (MemberInfo m in members)
                        {
                            sb.Append("<td>");
                            RenderValue(sb, ReadMember(m, item), depth + 1, maxDepth, maxItems, visited);
                            sb.Append("</td>");
                        }
                    }
                    sb.Append("</tr>");
                }
                sb.Append("</tbody></table>");
                if (truncated)
                {
                    sb.Append($"<div class=\"lp-more\">… truncated after {maxItems} items</div>");
                }
                sb.Append("</details>");
                return;
            }
        }

        sb.Append("<table class=\"lp-table\"><tbody>");
        for (int i = 0; i < items.Count; i++)
        {
            sb.Append($"<tr><td class=\"lp-idx\">{i}</td><td>");
            RenderValue(sb, items[i], depth + 1, maxDepth, maxItems, visited);
            sb.Append("</td></tr>");
        }
        sb.Append("</tbody></table>");
        if (truncated)
        {
            sb.Append($"<div class=\"lp-more\">… truncated after {maxItems} items</div>");
        }
        sb.Append("</details>");
    }

    private static void RenderObject(StringBuilder sb, object value, Type type, int depth, int maxDepth, int maxItems, HashSet<object> visited)
    {
        MemberInfo[] members = GetDumpMembers(type);
        if (members.Length == 0)
        {
            sb.Append("<span class=\"lp-scalar\">").Append(WebUtility.HtmlEncode(value.ToString() ?? FormatTypeName(type))).Append("</span>");
            return;
        }

        sb.Append("<details open class=\"lp-box\"><summary><span class=\"lp-type\">")
          .Append(WebUtility.HtmlEncode(FormatTypeName(type))).Append("</span></summary>");
        sb.Append("<table class=\"lp-table lp-kv\"><tbody>");
        foreach (MemberInfo m in members)
        {
            sb.Append("<tr><th>").Append(WebUtility.HtmlEncode(m.Name)).Append("</th><td>");
            RenderValue(sb, ReadMember(m, value), depth + 1, maxDepth, maxItems, visited);
            sb.Append("</td></tr>");
        }
        sb.Append("</tbody></table></details>");
    }

    private static MemberInfo[] GetDumpMembers(Type type)
    {
        PropertyInfo[] props = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);
        FieldInfo[] fields = type.GetFields(BindingFlags.Public | BindingFlags.Instance);
        List<MemberInfo> members = new(props.Length + fields.Length);
        foreach (PropertyInfo p in props)
        {
            if (p.GetIndexParameters().Length == 0 && p.CanRead)
            {
                members.Add(p);
            }
        }
        foreach (FieldInfo f in fields)
        {
            members.Add(f);
        }
        return [.. members];
    }

    private static Type GetMemberType(MemberInfo m) => m switch
    {
        PropertyInfo p => p.PropertyType,
        FieldInfo f => f.FieldType,
        _ => typeof(object),
    };

    private static object? ReadMember(MemberInfo m, object instance)
    {
        try
        {
            return m switch
            {
                PropertyInfo p => p.GetValue(instance),
                FieldInfo f => f.GetValue(instance),
                _ => null,
            };
        }
        catch (TargetInvocationException tie) when (tie.InnerException is not null)
        {
            return $"! {tie.InnerException.GetType().Name}: {tie.InnerException.Message}";
        }
        catch (Exception ex) when (ex is MemberAccessException or ArgumentException or InvalidOperationException)
        {
            return $"! {ex.GetType().Name}: {ex.Message}";
        }
    }

    private static Type? GetElementType(Type seqType, List<object?> items)
    {
        if (seqType.IsArray)
        {
            return seqType.GetElementType();
        }
        foreach (Type iface in seqType.GetInterfaces())
        {
            if (iface.IsGenericType && iface.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            {
                Type arg = iface.GetGenericArguments()[0];
                if (arg != typeof(object))
                {
                    return arg;
                }
            }
        }
        return items.FirstOrDefault(x => x is not null)?.GetType();
    }

    private static bool IsScalar(Type type)
    {
        Type? underlying = Nullable.GetUnderlyingType(type);
        if (underlying is not null)
        {
            type = underlying;
        }
        return type.IsPrimitive || type.IsEnum || type == typeof(decimal) || type == typeof(DateTime) ||
               type == typeof(DateTimeOffset) || type == typeof(TimeSpan) || type == typeof(DateOnly) ||
               type == typeof(TimeOnly) || type == typeof(Guid) || type == typeof(Uri);
    }

    private static string FormatScalar(object value) => value switch
    {
        DateTime dt => dt.ToString("yyyy-MM-dd HH:mm:ss.fff"),
        DateTimeOffset dto => dto.ToString("yyyy-MM-dd HH:mm:ss.fff zzz"),
        bool b => b ? "true" : "false",
        _ => value.ToString() ?? string.Empty,
    };

    private static string FormatTypeName(Type type)
    {
        if (Nullable.GetUnderlyingType(type) is { } inner)
        {
            return FormatTypeName(inner) + "?";
        }
        if (type == typeof(string)) { return "string"; }
        if (type == typeof(int)) { return "int"; }
        if (type == typeof(long)) { return "long"; }
        if (type == typeof(double)) { return "double"; }
        if (type == typeof(decimal)) { return "decimal"; }
        if (type == typeof(bool)) { return "bool"; }
        if (type == typeof(object)) { return "object"; }
        if (type.IsArray) { return FormatTypeName(type.GetElementType()!) + "[]"; }
        if (type.IsGenericType)
        {
            string name = type.Name;
            int tick = name.IndexOf('`');
            if (tick > 0)
            {
                name = name[..tick];
            }
            if (type.Name.StartsWith("<>f__AnonymousType", StringComparison.Ordinal))
            {
                name = "anonymous";
            }
            return $"{name}<{string.Join(", ", type.GetGenericArguments().Select(FormatTypeName))}>";
        }
        return type.Name;
    }
}
