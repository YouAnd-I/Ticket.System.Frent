using Frent;
using Ticket.Data;
using Xunit;

namespace Ticket.System.Frent.Tests;

public class TicketSystemTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ticket-tests-" + Guid.NewGuid().ToString("N"));
    private readonly TicketStore _store;
    private readonly TicketSystem _system;

    public TicketSystemTests()
    {
        Directory.CreateDirectory(_dir);
        _store = new TicketStore(_dir);
        _system = new TicketSystem(_store);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp cleanup is best effort */ }
    }

    private static TicketCreate Create(TicketPriority priority = TicketPriority.Urgent,
        string? title = "printer on fire", string? description = "it smells") => new()
    {
        Title = title,
        Description = description,
        RequestedPriority = priority,
        Requester = "<@1>",
        Assignee = "<@2>",
    };

    private static int Count<T>(World world) where T : struct
    {
        var count = 0;
        foreach (var _ in world.Query<T>().EnumerateWithEntities<T>()) count++;
        return count;
    }

    [Fact]
    public void Create_WithDirectPriority_IsPersistedAndAnswered()
    {
        using var world = new World();
        var request = world.Create(Create(TicketPriority.NoRush));

        _system.Execute(world);

        Assert.False(request.Has<TicketCreate>());
        var view = request.Get<TicketCreated>().View;
        Assert.True(view.Exists);
        Assert.Equal("no-rush", view.Priority);
        Assert.Equal("printer on fire", view.Title);
        Assert.Equal("<@2>", view.Assignee);
        Assert.Equal("open", view.Status);
        Assert.Equal(1, Count<TicketRecord>(world)); // the ticket's own entity outlives the reply
        Assert.True(File.Exists(Path.Combine(_dir, TicketStore.FileName)));
    }

    [Fact]
    public void Create_AutoWithText_WaitsForClassification_AndAsksLaya()
    {
        using var world = new World();
        var request = world.Create(Create(TicketPriority.Auto));

        _system.Execute(world);

        Assert.False(request.Has<TicketCreated>()); // not answered yet
        var waiting = request.Get<AwaitingClassification>();
        Assert.Equal("printer on fire it smells",
            Single<PriorityClassifyRequested>(world).Text);
        Assert.Equal(waiting.TicketId, Single<PriorityClassifyRequested>(world).TicketId);
    }

    [Fact]
    public void Create_AutoWithoutText_IsNoRush_LikeTheClassifier()
    {
        using var world = new World();
        var request = world.Create(Create(TicketPriority.Auto, title: null, description: "  "));

        _system.Execute(world);

        var view = request.Get<TicketCreated>().View;
        Assert.Equal("no-rush", view.Priority);
        Assert.True(view.AutoClassified);
        Assert.False(view.ClassifierOffline);
        Assert.Equal(0, Count<PriorityClassifyRequested>(world)); // laya never asked
    }

    [Fact]
    public void Classified_ResultFinishesTheWaitingTicket()
    {
        using var world = new World();
        var request = world.Create(Create(TicketPriority.Auto));
        _system.Execute(world);
        var ticketId = request.Get<AwaitingClassification>().TicketId;

        var classify = world.Create(new PriorityClassified
        {
            TicketId = ticketId,
            Priority = TicketPriority.NoRush,
        });
        _system.Execute(world);

        Assert.False(classify.Has<PriorityClassified>()); // acked and consumed
        Assert.False(request.Has<AwaitingClassification>());

        var view = request.Get<TicketCreated>().View;
        Assert.True(view.AutoClassified);
        Assert.False(view.ClassifierOffline);
        Assert.Equal("no-rush", view.Priority);
        Assert.Equal(ticketId, view.TicketId);
    }

    [Fact]
    public void Classified_Offline_DefaultsToUrgentWithFlag()
    {
        using var world = new World();
        var request = world.Create(Create(TicketPriority.Auto));
        _system.Execute(world);
        var ticketId = request.Get<AwaitingClassification>().TicketId;

        world.Create(new PriorityClassified { TicketId = ticketId, Offline = true });
        _system.Execute(world);

        var view = request.Get<TicketCreated>().View;
        Assert.Equal("urgent", view.Priority);
        Assert.True(view.ClassifierOffline);
        Assert.False(view.AutoClassified);
    }

    [Fact]
    public void StatusChange_IsPersistedAndAnswered()
    {
        using var world = new World();
        var request = world.Create(Create());
        _system.Execute(world);
        var id = request.Get<TicketCreated>().View.TicketId;

        var change = world.Create(new TicketStatusChange { TicketId = id, Status = "complete", ByUser = "<@9>" });
        _system.Execute(world);

        var view = change.Get<TicketChanged>().View;
        Assert.Equal("complete", view.Status);
        Assert.Contains("status=complete", File.ReadAllText(Path.Combine(_dir, TicketStore.FileName)));
    }

    [Fact]
    public void Note_IsAppended_CountedAndAnswered()
    {
        using var world = new World();
        var request = world.Create(Create());
        _system.Execute(world);
        var id = request.Get<TicketCreated>().View.TicketId;

        var note = world.Create(new TicketNote { TicketId = id, Note = "rebooted it", ByUser = "<@9>" });
        _system.Execute(world);

        var added = note.Get<TicketNoteAdded>();
        Assert.Equal(1, added.NoteCount);
        Assert.Equal("rebooted it", added.Note);
        Assert.Equal(1, added.View.NoteCount);
    }

    [Fact]
    public void Report_IsFiled_AndMarksTheTicketComplete()
    {
        using var world = new World();
        var request = world.Create(Create());
        _system.Execute(world);
        var id = request.Get<TicketCreated>().View.TicketId;

        var report = world.Create(new TicketReport
        {
            TicketId = id,
            Complaint = "was rude",
            Action = "an apology",
            Anonymous = true,
            ByUser = "<@9>",
        });
        _system.Execute(world);

        Assert.Equal("complete", report.Get<TicketReported>().View.Status);
        var log = File.ReadAllText(Path.Combine(_dir, TicketStore.FileName));
        Assert.Contains("user=anonymous", log); // the report line honors anonymity
        Assert.Contains("status=complete", log);
    }

    private static T Single<T>(World world) where T : struct
    {
        T? found = null;
        foreach (var row in world.Query<T>().EnumerateWithEntities<T>())
            found = row.Item1.Value;
        Assert.NotNull(found);
        return found!.Value;
    }
}
