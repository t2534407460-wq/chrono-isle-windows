using System.Globalization;
using Microsoft.Data.Sqlite;
using ChronoIsle.App.Services.Persistence;

namespace ChronoIsle.App.Services.Productivity;

public sealed record DraftProposal(string Title, DateTimeOffset? DueAtUtc = null, string Priority = "Normal",
    string? Category = null, int? EstimatedMinutes = null, string? Energy = null, string Status = "Pending");
public sealed record DraftItemView(string Id, string LifeItemId, int Ordinal, DraftProposal Proposal);
public sealed record CommandDraftView(string Id, string Status, DateTimeOffset ExpiresAtUtc, IReadOnlyList<DraftItemView> Items);

public sealed class DraftStore
{
    readonly IDbWriteQueue writeQueue;
    readonly SqliteConnectionFactory connections;
    readonly Func<DateTimeOffset> clock;

    public DraftStore(IDbWriteQueue writeQueue, SqliteConnectionFactory connections, Func<DateTimeOffset>? clock = null)
    {
        this.writeQueue = writeQueue ?? throw new ArgumentNullException(nameof(writeQueue));
        this.connections = connections ?? throw new ArgumentNullException(nameof(connections));
        this.clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public string Create(IReadOnlyList<DraftProposal> proposals, TimeSpan? lifetime = null)
    {
        Validate(proposals);
        var now = clock();
        var expires = now + (lifetime ?? TimeSpan.FromHours(24));
        if (expires <= now) throw new ArgumentOutOfRangeException(nameof(lifetime));
        var id = Guid.NewGuid().ToString("N");
        writeQueue.Execute(uow =>
        {
            Execute(uow, "INSERT INTO command_drafts(id,status,created_at_utc,expires_at_utc) VALUES($id,'Pending',$now,$expires)",
                ("$id", id), ("$now", Iso(now)), ("$expires", Iso(expires)));
            for (var i = 0; i < proposals.Count; i++) InsertDraftItem(uow, id, i, proposals[i]);
        });
        return id;
    }

    public CommandDraftView? Get(string id)
    {
        using var db = connections.OpenConnection();
        using var draft = db.CreateCommand();
        draft.CommandText = "SELECT status,expires_at_utc FROM command_drafts WHERE id=$id";
        draft.Parameters.AddWithValue("$id", id);
        using var reader = draft.ExecuteReader();
        if (!reader.Read()) return null;
        var status = reader.GetString(0);
        var expires = DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        reader.Close();
        using var items = db.CreateCommand();
        items.CommandText = "SELECT id,proposed_life_item_id,ordinal,title,due_at_utc,priority,category,estimated_minutes,energy,proposed_status FROM draft_items WHERE draft_id=$id ORDER BY ordinal";
        items.Parameters.AddWithValue("$id", id);
        using var itemReader = items.ExecuteReader();
        var result = new List<DraftItemView>();
        while (itemReader.Read()) result.Add(ReadItem(itemReader));
        return new(id, status, expires, result);
    }

    public void Cancel(string id) => SetTerminal(id, "Cancelled");
    public void Expire(string id) => SetTerminal(id, "Expired");

    public IReadOnlyList<string> Confirm(string id)
    {
        var draft = Get(id) ?? throw new KeyNotFoundException("Draft not found.");
        return ConfirmSelected(id, draft.Items.Select(item => item.Id).ToHashSet(), new Dictionary<string, DateTimeOffset?>());
    }

    /// <summary>Confirms only user-selected draft items; a supplied schedule overrides that item's proposed due time.</summary>
    public IReadOnlyList<string> ConfirmSelected(string id, IReadOnlySet<string> selectedItemIds,
        IReadOnlyDictionary<string, DateTimeOffset?> scheduleOverrides)
    {
        ArgumentNullException.ThrowIfNull(selectedItemIds);
        ArgumentNullException.ThrowIfNull(scheduleOverrides);
        var now = clock();
        return writeQueue.Execute(uow =>
        {
            var draft = ReadForConfirmation(uow, id);
            if (draft.ExpiresAtUtc <= now)
            {
                Execute(uow, "UPDATE command_drafts SET status='Expired' WHERE id=$id AND status='Pending'", ("$id", id));
                throw new InvalidOperationException("Draft has expired.");
            }
            var known = draft.Items.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
            if (selectedItemIds.Count == 0 || selectedItemIds.Any(itemId => !known.Contains(itemId)))
                throw new ArgumentException("Select one or more current draft items.", nameof(selectedItemIds));
            if (scheduleOverrides.Keys.Any(itemId => !selectedItemIds.Contains(itemId)))
                throw new ArgumentException("A schedule can only belong to a selected draft item.", nameof(scheduleOverrides));

            var selected = draft.Items.Where(item => selectedItemIds.Contains(item.Id)).Select(item =>
            {
                if (!scheduleOverrides.TryGetValue(item.Id, out var due) || due is null) return item;
                return item with { Proposal = item.Proposal with { DueAtUtc = due } };
            }).ToArray();
            Validate(selected.Select(item => item.Proposal).ToArray());
            foreach (var item in selected) InsertLifeItem(uow, item, now);
            var changed = Execute(uow, "UPDATE command_drafts SET status='Confirmed',confirmed_at_utc=$now WHERE id=$id AND status='Pending'",
                ("$id", id), ("$now", Iso(now)));
            if (changed != 1) throw new InvalidOperationException("Draft is no longer pending.");
            return (IReadOnlyList<string>)selected.Select(item => item.LifeItemId).ToArray();
        });
    }

    void SetTerminal(string id, string status) => writeQueue.Execute(uow =>
    {
        if (Execute(uow, "UPDATE command_drafts SET status=$status WHERE id=$id AND status='Pending'", ("$id", id), ("$status", status)) != 1)
            throw new InvalidOperationException("Draft is missing or no longer pending.");
    });

    CommandDraftView ReadForConfirmation(IUnitOfWork uow, string id)
    {
        using var command = uow.Connection.CreateCommand();
        command.Transaction = uow.Transaction;
        command.CommandText = "SELECT status,expires_at_utc FROM command_drafts WHERE id=$id";
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new KeyNotFoundException("Draft not found.");
        if (reader.GetString(0) != "Pending") throw new InvalidOperationException("Draft is no longer pending.");
        var expires = DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        reader.Close();
        using var items = uow.Connection.CreateCommand();
        items.Transaction = uow.Transaction;
        items.CommandText = "SELECT id,proposed_life_item_id,ordinal,title,due_at_utc,priority,category,estimated_minutes,energy,proposed_status FROM draft_items WHERE draft_id=$id ORDER BY ordinal";
        items.Parameters.AddWithValue("$id", id);
        using var rows = items.ExecuteReader();
        var result = new List<DraftItemView>();
        while (rows.Read()) result.Add(ReadItem(rows));
        return new(id, "Pending", expires, result);
    }

    static DraftItemView ReadItem(SqliteDataReader r) => new(r.GetString(0), r.GetString(1), r.GetInt32(2),
        new(r.GetString(3), r.IsDBNull(4) ? null : DateTimeOffset.Parse(r.GetString(4), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6), r.IsDBNull(7) ? null : r.GetInt32(7),
            r.IsDBNull(8) ? null : r.GetString(8), r.GetString(9)));

    static void InsertDraftItem(IUnitOfWork uow, string draftId, int ordinal, DraftProposal p) => Execute(uow, """
        INSERT INTO draft_items(id,draft_id,proposed_life_item_id,ordinal,title,due_at_utc,priority,category,estimated_minutes,energy,proposed_status)
        VALUES($id,$draft,$life,$ordinal,$title,$due,$priority,$category,$minutes,$energy,$status)
        """, ("$id", Guid.NewGuid().ToString("N")), ("$draft", draftId), ("$life", Guid.NewGuid().ToString("N")),
        ("$ordinal", ordinal), ("$title", p.Title.Trim()), ("$due", Db(p.DueAtUtc is null ? null : Iso(p.DueAtUtc.Value))),
        ("$priority", p.Priority), ("$category", Db(p.Category)), ("$minutes", Db(p.EstimatedMinutes)),
        ("$energy", Db(p.Energy)), ("$status", p.Status));

    static void InsertLifeItem(IUnitOfWork uow, DraftItemView item, DateTimeOffset now) => Execute(uow, """
        INSERT INTO life_items(id,kind,title,status,row_version,due_utc_instant,due_time_semantics,
          origin_type,is_readonly,created_at,updated_at,priority,category,estimated_minutes,energy,deferred_count)
        VALUES($id,'Todo',$title,'Pending',1,$due,$semantics,'Local',0,$now,$now,$priority,$category,$minutes,$energy,0)
        """, ("$id", item.LifeItemId), ("$title", item.Proposal.Title.Trim()),
        ("$due", Db(item.Proposal.DueAtUtc is null ? null : Iso(item.Proposal.DueAtUtc.Value))),
        ("$semantics", Db(item.Proposal.DueAtUtc is null ? null : "AbsoluteInstant")), ("$now", Iso(now)),
        ("$priority", item.Proposal.Priority), ("$category", Db(item.Proposal.Category)),
        ("$minutes", Db(item.Proposal.EstimatedMinutes)), ("$energy", Db(item.Proposal.Energy)));

    static void Validate(IReadOnlyList<DraftProposal> proposals)
    {
        if (proposals.Count is < 1 or > 10) throw new ArgumentOutOfRangeException(nameof(proposals), "A draft must contain 1-10 items.");
        foreach (var p in proposals)
        {
            if (string.IsNullOrWhiteSpace(p.Title)) throw new ArgumentException("Every draft item needs a title.", nameof(proposals));
            if (!string.Equals(p.Status, "Pending", StringComparison.Ordinal)) throw new ArgumentException("Draft items cannot be completed.", nameof(proposals));
            if (p.EstimatedMinutes is <= 0) throw new ArgumentOutOfRangeException(nameof(proposals), "Estimated minutes must be positive.");
        }
    }

    static int Execute(IUnitOfWork uow, string sql, params (string Name, object? Value)[] values)
    {
        using var command = uow.Connection.CreateCommand(); command.Transaction = uow.Transaction; command.CommandText = sql;
        foreach (var value in values) command.Parameters.AddWithValue(value.Name, value.Value ?? DBNull.Value);
        return command.ExecuteNonQuery();
    }
    static string Iso(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    static object Db(object? value) => value ?? DBNull.Value;
}
