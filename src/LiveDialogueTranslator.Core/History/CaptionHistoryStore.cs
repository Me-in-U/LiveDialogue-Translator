using Microsoft.Data.Sqlite;
using LiveDialogueTranslator.Core.Transcripts;

namespace LiveDialogueTranslator.Core.History;

public sealed class CaptionHistoryStore
{
    private readonly string dbPath;

    public CaptionHistoryStore(string dbPath)
    {
        this.dbPath = dbPath;
        var directory = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        Initialize();
    }

    public void Append(CaptionEntry entry)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO captions
                (id, speaker_id, speaker_name, text, start_ms, end_ms, latency_ms, captured_at, is_final)
            VALUES
                ($id, $speaker_id, $speaker_name, $text, $start_ms, $end_ms, $latency_ms, $captured_at, $is_final)
            ON CONFLICT(id) DO UPDATE SET
                speaker_id = excluded.speaker_id,
                speaker_name = excluded.speaker_name,
                text = excluded.text,
                start_ms = excluded.start_ms,
                end_ms = excluded.end_ms,
                latency_ms = excluded.latency_ms,
                captured_at = excluded.captured_at,
                is_final = excluded.is_final
            """;
        command.Parameters.AddWithValue("$id", entry.Id.ToString("D"));
        command.Parameters.AddWithValue("$speaker_id", entry.SpeakerId);
        command.Parameters.AddWithValue("$speaker_name", entry.SpeakerName);
        command.Parameters.AddWithValue("$text", entry.Text);
        command.Parameters.AddWithValue("$start_ms", entry.StartMs);
        command.Parameters.AddWithValue("$end_ms", entry.EndMs);
        command.Parameters.AddWithValue("$latency_ms", entry.LatencyMs.HasValue ? entry.LatencyMs.Value : DBNull.Value);
        command.Parameters.AddWithValue("$captured_at", entry.CapturedAt.ToString("O"));
        command.Parameters.AddWithValue("$is_final", entry.IsFinal ? 1 : 0);
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<CaptionEntry> LoadRecent(int limit)
    {
        if (limit < 1)
        {
            return [];
        }

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, speaker_id, speaker_name, text, start_ms, end_ms, latency_ms, captured_at, is_final
            FROM captions
            ORDER BY rowid DESC
            LIMIT $limit
            """;
        command.Parameters.AddWithValue("$limit", limit);

        var entries = new List<CaptionEntry>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            entries.Add(new CaptionEntry(
                Guid.Parse(reader.GetString(0)),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetInt64(4),
                reader.GetInt64(5),
                reader.IsDBNull(6) ? null : reader.GetInt32(6),
                DateTimeOffset.Parse(reader.GetString(7)),
                reader.GetInt32(8) == 1));
        }

        entries.Reverse();
        return entries;
    }

    public void Clear()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM captions;";
        command.ExecuteNonQuery();
    }

    private void Initialize()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS captions (
                id TEXT PRIMARY KEY,
                speaker_id TEXT NOT NULL,
                speaker_name TEXT NOT NULL,
                text TEXT NOT NULL,
                start_ms INTEGER NOT NULL,
                end_ms INTEGER NOT NULL,
                latency_ms INTEGER NULL,
                captured_at TEXT NOT NULL,
                is_final INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_captions_captured_at ON captions(captured_at);
            """;
        command.ExecuteNonQuery();
    }

    private SqliteConnection OpenConnection()
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Pooling = false
        };
        var connection = new SqliteConnection(builder.ConnectionString);
        connection.Open();
        return connection;
    }
}
