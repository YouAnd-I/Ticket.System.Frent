using Frent;
using System.Text.Json.Nodes;
using Ticket.Data;

namespace Ticket.System.Frent;

public partial struct AwaitingClassification
{
    public string TicketId;
    public TicketCreate Create;
}

public sealed class TicketSystem(ITicketStore store)
{
    public void Execute(World world)
    {
        FinishClassifiedTickets(world);
        CreateTickets(world);
        ApplyFollowUps(world);
    }

    private void FinishClassifiedTickets(World world)
    {
        foreach (var row in world.Query<PriorityClassified>()
                     .EnumerateWithEntities<PriorityClassified>())
        {
            var classified = row.Item1.Value;

            var request = row.Entity;
            request.Add(new PriorityClassifiedAck());
            request.Remove<PriorityClassified>();

            var offline = classified.Offline || classified.Priority is null;
            var priority = offline ? TicketPriority.Urgent : classified.Priority!.Value;

            List<(Entity Waiting, AwaitingClassification State)>? waiting = null;
            foreach (var w in world.Query<AwaitingClassification>()
                         .EnumerateWithEntities<AwaitingClassification>())
                if (w.Item1.Value.TicketId == classified.TicketId)
                    (waiting ??= []).Add((w.Entity, w.Item1.Value));

            if (waiting is null) continue;
            foreach (var (entity, state) in waiting)
            {
                entity.Remove<AwaitingClassification>();
                FinalizeTicket(entity, state.Create, state.TicketId, priority, auto: !offline, offline,
                    classified.AssigneeStaffId);
            }
        }
    }

    private void CreateTickets(World world)
    {
        List<(Entity Request, TicketCreate Create)>? toCreate = null;
        foreach (var row in world.Query<TicketCreate>().EnumerateWithEntities<TicketCreate>())
            (toCreate ??= []).Add((row.Entity, row.Item1.Value));
        if (toCreate is null) return;

        foreach (var (request, create) in toCreate)
        {
            request.Remove<TicketCreate>();
            var ticketId = NewTicketId();
            var ticket = world.Create(new TicketRecord { Id = ticketId, Requester = create.Requester });

            if (create.RequestedPriority == TicketPriority.Auto
                && !string.IsNullOrWhiteSpace($"{create.Title} {create.Description}"))
            {
                var now = DateTimeOffset.UtcNow;
                ticket.Add(new PriorityClassifyRequested
                {
                    TicketId = ticketId,
                    Text = $"{create.Title} {create.Description}",
                    Priorities = [.. store.Priorities()],
                    AvailableStaff = [.. store.AvailableStaff(now)],
                    NowUtc = now.ToString("u"),
                });
                request.Add(new AwaitingClassification { TicketId = ticketId, Create = create });
            }
            else
            {
                var auto = create.RequestedPriority == TicketPriority.Auto;
                var priority = auto ? TicketPriority.NoRush : create.RequestedPriority;
                FinalizeTicket(request, create, ticketId, priority, auto, offline: false, assigneeStaffId: null);
            }
        }
    }

    private void ApplyFollowUps(World world)
    {
        foreach (var row in world.Query<TicketStatusChange>()
                     .EnumerateWithEntities<TicketStatusChange>())
        {
            var entity = row.Entity;
            var change = row.Item1.Value;
            store.AppendStatus(change.ByUser, change.TicketId, change.Status);
            entity.Add(new TicketChanged { View = View(change.TicketId) });
            entity.Remove<TicketStatusChange>();
        }

        foreach (var row in world.Query<TicketNote>().EnumerateWithEntities<TicketNote>())
        {
            var entity = row.Entity;
            var note = row.Item1.Value;
            var count = store.AppendNote(note.ByUser, note.TicketId, note.Note);
            entity.Add(new TicketNoteAdded
            {
                View = View(note.TicketId),
                Note = note.Note,
                NoteCount = count,
            });
            entity.Remove<TicketNote>();
        }

        foreach (var row in world.Query<TicketReport>().EnumerateWithEntities<TicketReport>())
        {
            var entity = row.Entity;
            var report = row.Item1.Value;
            store.AppendReport(report.ByUser, report.TicketId, report.Complaint,
                report.Action, report.Anonymous, report.FileUrl);
            store.AppendStatus(report.ByUser, report.TicketId, "complete");
            entity.Add(new TicketReported { View = View(report.TicketId) });
            entity.Remove<TicketReport>();
        }
    }

    private void FinalizeTicket(Entity request, TicketCreate create, string ticketId,
        TicketPriority priority, bool auto, bool offline, string? assigneeStaffId)
    {
        var assignee = create.Assignee;
        if (assignee is null && assigneeStaffId is { } staffId)
            assignee = $"<@{staffId}>";
        if (assignee is null && store.Route(DateTimeOffset.UtcNow) is { StaffIds.Count: > 0 } route)
            assignee = $"<@{route.StaffIds[0]}>";
        store.Append(create.Requester, ticketId, PriorityName(priority), auto, offline,
            create.Title, create.Description, create.AttachmentUrl, assignee);
        request.Add(new TicketCreated { View = View(ticketId) });
    }

    public TicketView View(string id)
    {
        var t = store.LoadJson(id);
        if (t is null)
            return new TicketView { TicketId = id, Priority = "urgent", Status = "open" };

        var title = t["title"]?.GetValue<string>();
        var description = t["description"]?.GetValue<string>();
        var solution = store.BestSolution($"{title} {description}");

        return new TicketView
        {
            TicketId = id,
            Exists = true,
            Title = title,
            Description = description,
            Priority = t["priority"]?.GetValue<string>() ?? "urgent",
            AutoClassified = t["auto"]?.GetValue<bool>() ?? false,
            ClassifierOffline = t["offline"]?.GetValue<bool>() ?? false,
            AttachmentUrl = t["file"]?.GetValue<string>(),
            Assignee = t["assigned"]?.GetValue<string>(),
            NoteCount = t["notes"] as JsonArray is { Count: > 0 } notes ? notes.Count : 0,
            SolutionTitle = solution?.Title,
            SolutionText = solution?.Text,
            SolutionImage = solution?.Image,
            CreatedAtUtc = DateTimeOffset.TryParse(t["created"]?.GetValue<string>(), out var created)
                ? created
                : default,
            Status = t["status"]?.GetValue<string>() ?? "open",
        };
    }

    private static string NewTicketId() => Guid.NewGuid().ToString("N")[..8];

    private static string PriorityName(TicketPriority p) => p switch
    {
        TicketPriority.Auto => "auto",
        TicketPriority.NoRush => "no-rush",
        _ => "urgent",
    };
}
