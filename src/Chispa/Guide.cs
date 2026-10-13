using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Chispa;

/// <summary>
/// La guía de uso. Se escribe en GUIA.md (raíz del proyecto), viaja dentro del ejecutable
/// y se abre en el navegador desde el menú o diciendo "Chispa, ayuda".
/// </summary>
internal static class Guide
{
    private const string Style = """
        body { font: 16px/1.55 "Segoe UI", system-ui, sans-serif; margin: 0; background: #f6f7fb; color: #1d2030; }
        main { max-width: 820px; margin: 0 auto; padding: 28px 22px 60px; }
        h1 { font-size: 30px; margin: 0 0 6px; }
        h2 { font-size: 21px; margin: 34px 0 8px; padding-top: 14px; border-top: 1px solid #d9dce8; }
        h3 { font-size: 17px; margin: 22px 0 6px; }
        table { border-collapse: collapse; width: 100%; margin: 10px 0 16px; background: #fff; }
        th, td { border: 1px solid #d9dce8; padding: 7px 10px; text-align: left; vertical-align: top; }
        th { background: #eceef6; }
        code { background: #e8eaf4; border-radius: 4px; padding: 1px 5px; font: 14px Consolas, monospace; }
        pre { background: #e8eaf4; border-radius: 6px; padding: 10px 12px; overflow-x: auto; }
        pre code { padding: 0; background: none; }
        li { margin: 3px 0; }
        @media (prefers-color-scheme: dark) {
          body { background: #171923; color: #e6e8f2; }
          table { background: #1f2230; }
          th { background: #2a2e40; }
          th, td, h2 { border-color: #363b52; }
          code, pre { background: #2a2e40; }
          a { color: #8fb4ff; }
        }
        """;

    public static void Open()
    {
        try
        {
            using var stream = typeof(Guide).Assembly.GetManifestResourceStream("GUIA.md");
            if (stream == null) return;
            using var reader = new StreamReader(stream, Encoding.UTF8);
            Directory.CreateDirectory(Config.DataDir);
            string path = Path.Combine(Config.DataDir, "guia.html");
            File.WriteAllText(path, ToHtml(reader.ReadToEnd()), Encoding.UTF8);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) { Log.Write($"No pude abrir la guía: {ex.Message}"); }
    }

    /// <summary>Convierte el Markdown sencillo de la guía (títulos, párrafos, listas, tablas y código) en una página.</summary>
    internal static string ToHtml(string markdown)
    {
        var html = new StringBuilder();
        var paragraph = new List<string>();
        string[] lines = markdown.Replace("\r", "").Split('\n');

        void FlushParagraph()
        {
            if (paragraph.Count == 0) return;
            html.Append("<p>").Append(Inline(string.Join(" ", paragraph))).AppendLine("</p>");
            paragraph.Clear();
        }

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            if (line.StartsWith("```"))
            {
                FlushParagraph();
                html.Append("<pre><code>");
                for (i++; i < lines.Length && !lines[i].StartsWith("```"); i++) html.AppendLine(WebUtility.HtmlEncode(lines[i]));
                html.AppendLine("</code></pre>");
            }
            else if (Regex.Match(line, @"^(#{1,3})\s+(.+)$") is { Success: true } heading)
            {
                FlushParagraph();
                int level = heading.Groups[1].Length;
                html.AppendLine($"<h{level}>{Inline(heading.Groups[2].Value)}</h{level}>");
            }
            else if (line.StartsWith('|'))
            {
                FlushParagraph();
                html.AppendLine("<table>");
                for (bool header = true; i < lines.Length && lines[i].StartsWith('|'); i++)
                {
                    if (Regex.IsMatch(lines[i], @"^\|[\s\-:|]+\|$")) continue;   // la raya que separa la cabecera
                    string tag = header ? "th" : "td";
                    var cells = lines[i].Trim().Trim('|').Split('|').Select(c => $"<{tag}>{Inline(c.Trim())}</{tag}>");
                    html.Append("<tr>").Append(string.Concat(cells)).AppendLine("</tr>");
                    header = false;
                }
                html.AppendLine("</table>");
                i--;
            }
            else if (line.StartsWith("- "))
            {
                FlushParagraph();
                html.AppendLine("<ul>");
                for (; i < lines.Length && (lines[i].StartsWith("- ") || lines[i].StartsWith("  ")); i++)
                {
                    if (lines[i].StartsWith("- ")) html.Append("<li>").Append(Inline(lines[i][2..]));
                    else html.Append(' ').Append(Inline(lines[i].Trim()));   // la línea siguiente del mismo punto
                    if (i + 1 >= lines.Length || !lines[i + 1].StartsWith("  ")) html.AppendLine("</li>");
                }
                html.AppendLine("</ul>");
                i--;
            }
            else if (line.Trim().Length == 0) FlushParagraph();
            else paragraph.Add(line.Trim());
        }
        FlushParagraph();

        return $"<!doctype html>\n<html lang=\"es\"><head><meta charset=\"utf-8\"><title>Guía de Chispa</title>\n<style>\n{Style}</style></head>\n<body><main>\n{html}</main></body></html>\n";
    }

    private static string Inline(string text)
    {
        string s = WebUtility.HtmlEncode(text);
        s = Regex.Replace(s, @"`([^`]+)`", "<code>$1</code>");
        s = Regex.Replace(s, @"\*\*([^*]+)\*\*", "<strong>$1</strong>");
        s = Regex.Replace(s, @"\*([^*]+)\*", "<em>$1</em>");
        return Regex.Replace(s, @"\[([^\]]+)\]\((https?://[^)\s]+)\)", "<a href=\"$2\">$1</a>");
    }
}
