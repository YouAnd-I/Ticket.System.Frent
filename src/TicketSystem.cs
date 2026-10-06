using Frent;
using System.Text.Json.Nodes;
using Ticket.Data;

namespace Ticket.System.Frent;

// Internal world state: while the laya adapter classifies, the /it request entity
// waits behind this component. It carries the original create request.
public partial struct AwaitingClassification
{
    public string TicketId;
    public TicketCreate Create;
}

// The rules for IT tickets.
//
// A ticket is state that outlives its reply: it gets its own entity (the Ticket
// component) and its persisted form (TicketStore). Requests are messages: each
// is answered with a response component on its own entity, and the loop despawns it.
//
// Classification is a world-initiated conversation with the laya adapter: a ticket
// created with priority Auto gets a PriorityClassifyRequested component on its
// entity (the loop's delivery pass publishes it to subscribers); the /it request
// waits behind AwaitingClassification until the adapter answers with a
// PriorityClassified request, and only then is the reply finished.
public sealed class TicketSystem(TicketStore store)
{
    public void Execute(World world)
    {
        FinishClassifiedTickets(world);
        CreateTickets(world);
        ApplyFollowUps(world);
    }

    // 1. The laya adapter answered: ack it and finish the waiting /it requests.
    private void FinishClassifiedTickets(World world)
    {
        foreach (var row in world.Query<PriorityClassified>()
                     .EnumerateWithEntities<PriorityClassified>())
        {
            var classified = row.Item1.Value;

            // ack the adapter's request on its own entity
            var request = row.Entity;
            request.Add(new PriorityClassifiedAck());
            request.Remove<PriorityClassified>();

            var offline = classified.Offline || classified.Priority is null;
            var priority = offline ? TicketPriority.Urgent : classified.Priority!.Value;

            // collect first: structural changes must not hit the query being iterated
            List<(Entity Waiting, AwaitingClassification State)>? waiting = null;
            foreach (var w in world.Query<AwaitingClassification>()
                         .EnumerateWithEntities<AwaitingClassification>())
                if (w.Item1.Value.TicketId == classified.TicketId)
                    (waiting ??= []).Add((w.Entity, w.Item1.Value));

            if (waiting is null) continue;
            foreach (var (entity, state) in waiting)
            {
                entity.Remove<AwaitingClassification>();
                FinalizeTicket(entity, state.Create, state.TicketId, priority, auto: !offline, offline);
            }
        }
    }

    // 2. New tickets. Auto priority with text goes to the laya classifier first;
    //    everything else (and Auto with no text — NoRush, like the classifier would)
    //    is answered right away.
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
                ticket.Add(new PriorityClassifyRequested
                {
                    TicketId = ticketId,
                    Text = $"{create.Title} {create.Description}",
                });
                request.Add(new AwaitingClassification { TicketId = ticketId, Create = create });
            }
            else
            {
                var auto = create.RequestedPriority == TicketPriority.Auto; // empty text → NoRush, like the classifier
                var priority = auto ? TicketPriority.NoRush : create.RequestedPriority;
                FinalizeTicket(request, create, ticketId, priority, auto, offline: false);
            }
        }
    }

    // 3. Follow-ups from the buttons on the card.
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
            store.AppendStatus(report.ByUser, report.TicketId, "complete"); // card says complete — persist it
            entity.Add(new TicketReported { View = View(report.TicketId) });
            entity.Remove<TicketReport>();
        }
    }

    private void FinalizeTicket(Entity request, TicketCreate create, string ticketId,
        TicketPriority priority, bool auto, bool offline)
    {
        store.Append(create.Requester, ticketId, PriorityName(priority), auto, offline,
            create.Title, create.Description, create.AttachmentUrl, create.Assignee);
        request.Add(new TicketCreated { View = View(ticketId) });
    }

    // The store is the ticket's persisted form; this builds the plain snapshot a
    // screen needs to draw the card (the IT solution lookup included).
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
        TicketPriority.Report => "report",
        _ => "urgent",
    };
}
