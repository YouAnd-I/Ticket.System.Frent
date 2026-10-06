using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ticket.System.Frent;

// Ticket storage — append-only txt, parseable "key=value" fields so we can search back,
// plus one JSON file per ticket and IT-written solution files ({slug}.s.json).
// Instance-based so tests can point it at a temp directory; the bot uses Default.
public sealed class TicketStore(string baseDir)
{
    public const string FileName = "it-tickets.txt";
    public const string DirName = "it-tickets";

    public static TicketStore Default { get; } = new(".");

    private string LogPath => global::System.IO.Path.Combine(baseDir, FileName);
    private string DirPath => global::System.IO.Path.Combine(baseDir, DirName);

    public record Ticket(string Id, string Title, string Desc);
    public record Solution(string Title, string? Text, string? Image);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    public void Append(string user, string id, string priority, bool auto, bool offline,
        string? title, string? desc, string? file, string? assigned)
    {
        var line = $"[{DateTimeOffset.UtcNow:u}] user={user} ticket={id} priority={priority}{(auto ? " auto" : "")}{(offline ? " classifier-offline" : "")} | title={Clean(title) ?? "(no title)"} | desc={Clean(desc)}";
        if (file is not null) line += $" | file={file}";
        if (assigned is not null) line += $" | assigned={assigned}";
        File.AppendAllText(LogPath, line + '\n');

        Directory.CreateDirectory(DirPath);
        File.WriteAllText(global::System.IO.Path.Combine(DirPath, $"{Slug(title)}-{id}.json"),
            JsonSerializer.Serialize(new
            {
                id, user,
                created = DateTimeOffset.UtcNow.ToString("u"),
                priority,
                auto,
                offline,
                assigned,
                title,
                description = desc,
                file,
                status = "open",
            }, JsonOpts));
    }

    public JsonNode? LoadJson(string id)
    {
        if (!Directory.Exists(DirPath)) return null;
        var path = Directory.EnumerateFiles(DirPath, $"*-{id}.json").FirstOrDefault();
        if (path is null) return null;
        try { return JsonNode.Parse(File.ReadAllText(path)); }
        catch { return null; }
    }

    // Solutions written by IT as {slug}.s.json — { "title": "...", "text": "...", "image": "url" }
    public Solution? BestSolution(string? query)
    {
        var words = (query ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(w => w.Length > 1).ToArray();
        if (words.Length == 0 || !Directory.Exists(DirPath)) return null;

        return Directory.EnumerateFiles(DirPath, "*.s.json")
            .Select(f =>
            {
                try { return JsonSerializer.Deserialize<Solution>(File.ReadAllText(f), JsonOpts); }
                catch { return null; }
            })
            .Where(s => s is not null)
            .Select(s => (s: s!, score: words.Count(w =>
                s!.Title.Contains(w, StringComparison.OrdinalIgnoreCase) ||
                (s.Text ?? "").Contains(w, StringComparison.OrdinalIgnoreCase))))
            .Where(x => x.score > 0)
            .OrderByDescending(x => x.score)
            .Select(x => x.s)
            .FirstOrDefault();
    }

    private static string Slug(string? title)
    {
        var s = string.Concat((title ?? "untitled").ToLowerInvariant().Trim()
            .Select(c => char.IsLetterOrDigit(c) ? c : '-'));
        while (s.Contains("--")) s = s.Replace("--", "-");
        return s.Trim('-') is { Length: > 0 } x ? x : "untitled";
    }

    public void AppendStatus(string user, string id, string status)
    {
        File.AppendAllText(LogPath, $"[{DateTimeOffset.UtcNow:u}] user={user} ticket={id} status={status}\n");
        UpdateJson(id, n => n["status"] = status);
    }

    // Returns this ticket's note count after appending
    public int AppendNote(string user, string id, string note)
    {
        File.AppendAllText(LogPath,
            $"[{DateTimeOffset.UtcNow:u}] user={Clean(user)} ticket={id} | note={Clean(note)}\n");
        UpdateJson(id, n =>
        {
            if (n["notes"] is not JsonArray notes) n["notes"] = notes = new JsonArray();
            notes.Add(note);
        });
        return File.ReadLines(LogPath)
            .Count(l => l.Contains($"ticket={id} ") && l.Contains("| note="));
    }

    private void UpdateJson(string id, Action<JsonNode> update)
    {
        if (!Directory.Exists(DirPath)) return;
        var path = Directory.EnumerateFiles(DirPath, $"*-{id}.json").FirstOrDefault();
        if (path is null) return;
        try
        {
            var node = JsonNode.Parse(File.ReadAllText(path))!;
            update(node);
            File.WriteAllText(path, node.ToJsonString(JsonOpts));
        }
        catch { }
    }

    public void AppendReport(string user, string id, string complaint, string action,
        bool anonymous, string? file) =>
        File.AppendAllText(LogPath,
            $"[{DateTimeOffset.UtcNow:u}] user={(anonymous ? "anonymous" : user)} ticket={id} report | complaint={Clean(complaint)} | action={Clean(action)}" +
            (file is null ? "" : $" | file={file}") + "\n");

    // Time since the ticket's creation line
    public TimeSpan? Age(string id)
    {
        if (!File.Exists(LogPath)) return null;
        foreach (var line in File.ReadLines(LogPath))
        {
            if (!line.Contains($"ticket={id}") || !line.Contains(" | title=")) continue;
            var close = line.IndexOf(']');
            if (line.StartsWith('[') && close > 1 &&
                DateTimeOffset.TryParse(line[1..close], out var created))
                return DateTimeOffset.UtcNow - created;
        }
        return null;
    }

    public List<Ticket> Load()
    {
        var list = new List<Ticket>();
        if (!File.Exists(LogPath)) return list;
        foreach (var line in File.ReadLines(LogPath))
        {
            var parts = line.Split(" | ");
            var id = parts[0].Split(' ').FirstOrDefault(p => p.StartsWith("ticket="))?[7..];
            var title = parts.ElementAtOrDefault(1);
            if (id is null || title is null || !title.StartsWith("title=")) continue;
            var desc = parts.ElementAtOrDefault(2);
            list.Add(new(id, title[6..], desc is not null && desc.StartsWith("desc=") ? desc[5..] : ""));
        }
        return list;
    }

    // Silly search: score = words found in title or description
    public IEnumerable<Ticket> Similar(string? query, int take = 25, string? excludeId = null)
    {
        var words = (query ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(w => w.Length > 1).ToArray();
        if (words.Length == 0) return [];
        return Load()
            .Where(t => t.Id != excludeId)
            .Select(t => (t, score: words.Count(w =>
                t.Title.Contains(w, StringComparison.OrdinalIgnoreCase) ||
                t.Desc.Contains(w, StringComparison.OrdinalIgnoreCase))))
            .Where(x => x.score > 0)
            .OrderByDescending(x => x.score)
            .Take(take)
            .Select(x => x.t);
    }

    private static string? Clean(string? s) =>
        s?.Replace("\r", " ").Replace("\n", " ").Replace("|", "/");
}
