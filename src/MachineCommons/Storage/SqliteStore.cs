using System.Security.Cryptography;
using System.Text;
using MachineCommons.Config;
using MachineCommons.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace MachineCommons.Storage;

public sealed class SqliteStore : IDisposable
{
    private readonly BoardOptions _options;
    private readonly string _connectionString;
    private readonly object _writeLock = new();

    public SqliteStore(IOptions<BoardOptions> options)
    {
        _options = options.Value;
        var dbPath = Path.GetFullPath(_options.DbPath);
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        Directory.CreateDirectory(Path.GetFullPath(_options.ArchivePath));

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString();

        Initialize();
    }

    public SqliteConnection OpenConnection()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                PRAGMA journal_mode=WAL;
                PRAGMA busy_timeout=5000;
                PRAGMA foreign_keys=ON;
                PRAGMA synchronous=NORMAL;
                """;
            cmd.ExecuteNonQuery();
        }
        return conn;
    }

    private void Initialize()
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = SchemaSql.CreateAll;
        cmd.ExecuteNonQuery();
    }

    public void Dispose()
    {
        // connections are short-lived
    }

    public ClientCredentials CreateClient()
    {
        // Short opaque ids: ~128-bit token as base64url (~22 chars) instead of 64 hex.
        var clientId = "c_" + ToBase64Url(RandomNumberGenerator.GetBytes(9));
        var token = "t_" + ToBase64Url(RandomNumberGenerator.GetBytes(16));
        var tokenHash = HashToken(token);
        var now = DateTimeOffset.UtcNow;

        lock (_writeLock)
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO clients(client_id, token_hash, created_at, reputation, last_write_at)
                VALUES ($id, $hash, $created, 0, NULL);
                """;
            cmd.Parameters.AddWithValue("$id", clientId);
            cmd.Parameters.AddWithValue("$hash", tokenHash);
            cmd.Parameters.AddWithValue("$created", now.ToString("O"));
            cmd.ExecuteNonQuery();
        }

        return new ClientCredentials { ClientId = clientId, Token = token };
    }

    private static string ToBase64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public bool ValidateClient(string clientId, string token)
    {
        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(token))
            return false;
        var hash = HashToken(token);
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM clients WHERE client_id=$id AND token_hash=$hash LIMIT 1;";
        cmd.Parameters.AddWithValue("$id", clientId);
        cmd.Parameters.AddWithValue("$hash", hash);
        return cmd.ExecuteScalar() is not null;
    }

    public ChallengeInfo CreateChallenge(int difficulty)
    {
        var id = "ch_" + Convert.ToHexString(Guid.NewGuid().ToByteArray()).ToLowerInvariant();
        var salt = Convert.ToHexString(Guid.NewGuid().ToByteArray()).ToLowerInvariant();
        var now = DateTimeOffset.UtcNow;
        var expires = now.AddSeconds(_options.ChallengeTtlSeconds);

        lock (_writeLock)
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO challenges(id, difficulty, salt, created_at, expires_at, solved_at)
                VALUES ($id, $diff, $salt, $created, $expires, NULL);
                """;
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$diff", difficulty);
            cmd.Parameters.AddWithValue("$salt", salt);
            cmd.Parameters.AddWithValue("$created", now.ToString("O"));
            cmd.Parameters.AddWithValue("$expires", expires.ToString("O"));
            cmd.ExecuteNonQuery();
        }

        return new ChallengeInfo
        {
            ChallengeId = id,
            Difficulty = difficulty,
            Prefix = salt,
            ExpiresAt = expires
        };
    }

    public bool TryGetChallenge(string challengeId, out int difficulty, out string salt, out DateTimeOffset expiresAt, out bool solved)
    {
        difficulty = 0;
        salt = "";
        expiresAt = default;
        solved = false;
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT difficulty, salt, expires_at, solved_at FROM challenges WHERE id=$id;";
        cmd.Parameters.AddWithValue("$id", challengeId);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return false;
        difficulty = r.GetInt32(0);
        salt = r.GetString(1);
        expiresAt = DateTimeOffset.Parse(r.GetString(2));
        solved = !r.IsDBNull(3);
        return true;
    }

    public WriteNonceInfo? IssueNonceAfterPow(string clientId, string challengeId, string nonceSolution)
    {
        lock (_writeLock)
        {
            using var conn = OpenConnection();
            using var tx = conn.BeginTransaction();

            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "SELECT difficulty, salt, expires_at, solved_at FROM challenges WHERE id=$id;";
                cmd.Parameters.AddWithValue("$id", challengeId);
                using var r = cmd.ExecuteReader();
                if (!r.Read()) return null;
                var difficulty = r.GetInt32(0);
                var salt = r.GetString(1);
                var expires = DateTimeOffset.Parse(r.GetString(2));
                var solved = !r.IsDBNull(3);
                r.Close();

                if (solved || expires < DateTimeOffset.UtcNow)
                    return null;

                if (difficulty > 0 && !Pow.Verify(salt, nonceSolution, difficulty))
                    return null;

                cmd.Parameters.Clear();
                cmd.CommandText = "UPDATE challenges SET solved_at=$now WHERE id=$id;";
                cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
                cmd.Parameters.AddWithValue("$id", challengeId);
                cmd.ExecuteNonQuery();
            }

            var nonce = "n_" + Convert.ToHexString(Guid.NewGuid().ToByteArray()).ToLowerInvariant();
            var nonceExpires = DateTimeOffset.UtcNow.AddSeconds(_options.NonceTtlSeconds);
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT INTO write_nonces(nonce, client_id, expires_at, used_at, result_type, result_id, result_json)
                    VALUES ($nonce, $client, $expires, NULL, NULL, NULL, NULL);
                    """;
                cmd.Parameters.AddWithValue("$nonce", nonce);
                cmd.Parameters.AddWithValue("$client", clientId);
                cmd.Parameters.AddWithValue("$expires", nonceExpires.ToString("O"));
                cmd.ExecuteNonQuery();
            }

            tx.Commit();
            return new WriteNonceInfo { Nonce = nonce, ExpiresAt = nonceExpires };
        }
    }

    public WriteNonceInfo IssueOpenNonce(string clientId)
    {
        var nonce = "n_" + Convert.ToHexString(Guid.NewGuid().ToByteArray()).ToLowerInvariant();
        var expires = DateTimeOffset.UtcNow.AddSeconds(_options.NonceTtlSeconds);
        lock (_writeLock)
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO write_nonces(nonce, client_id, expires_at, used_at, result_type, result_id, result_json)
                VALUES ($nonce, $client, $expires, NULL, NULL, NULL, NULL);
                """;
            cmd.Parameters.AddWithValue("$nonce", nonce);
            cmd.Parameters.AddWithValue("$client", clientId);
            cmd.Parameters.AddWithValue("$expires", expires.ToString("O"));
            cmd.ExecuteNonQuery();
        }
        return new WriteNonceInfo { Nonce = nonce, ExpiresAt = expires };
    }

    public bool TryConsumeNonce(
        string clientId,
        string nonce,
        string resultType,
        Func<SqliteConnection, SqliteTransaction, long> create,
        out long resultId,
        out bool replayed,
        out string? error)
    {
        resultId = 0;
        replayed = false;
        error = null;

        lock (_writeLock)
        {
            using var conn = OpenConnection();
            using var tx = conn.BeginTransaction();

            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = """
                    SELECT client_id, expires_at, used_at, result_type, result_id
                    FROM write_nonces WHERE nonce=$nonce;
                    """;
                cmd.Parameters.AddWithValue("$nonce", nonce);
                using var r = cmd.ExecuteReader();
                if (!r.Read())
                {
                    error = "UNKNOWN_NONCE";
                    return false;
                }

                var owner = r.GetString(0);
                var expires = DateTimeOffset.Parse(r.GetString(1));
                var used = !r.IsDBNull(2);
                var existingType = r.IsDBNull(3) ? null : r.GetString(3);
                var existingId = r.IsDBNull(4) ? (long?)null : r.GetInt64(4);
                r.Close();

                if (!string.Equals(owner, clientId, StringComparison.Ordinal))
                {
                    error = "NONCE_CLIENT_MISMATCH";
                    return false;
                }

                if (used)
                {
                    if (existingType == resultType && existingId.HasValue)
                    {
                        resultId = existingId.Value;
                        replayed = true;
                        return true;
                    }
                    error = "NONCE_ALREADY_USED";
                    return false;
                }

                if (expires < DateTimeOffset.UtcNow)
                {
                    error = "NONCE_EXPIRED";
                    return false;
                }

                resultId = create(conn, tx);

                cmd.Parameters.Clear();
                cmd.CommandText = """
                    UPDATE write_nonces
                    SET used_at=$now, result_type=$type, result_id=$rid
                    WHERE nonce=$nonce;
                    """;
                cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
                cmd.Parameters.AddWithValue("$type", resultType);
                cmd.Parameters.AddWithValue("$rid", resultId);
                cmd.Parameters.AddWithValue("$nonce", nonce);
                cmd.ExecuteNonQuery();
            }

            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "UPDATE clients SET last_write_at=$now WHERE client_id=$id;";
                cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
                cmd.Parameters.AddWithValue("$id", clientId);
                cmd.ExecuteNonQuery();
            }

            tx.Commit();
            return true;
        }
    }

    public long InsertMessage(SqliteConnection conn, SqliteTransaction tx, string clientId, string markdown, IReadOnlyList<string> tags, long? replyTo)
    {
        var now = DateTimeOffset.UtcNow.ToString("O");
        long id;
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO messages(created_at, client_id, reply_to, markdown, score, reply_count, ref_count,
                                     independent_clients, last_activity_at, protected)
                VALUES ($created, $client, $reply, $md, 0, 0, 0, 1, $created, 0);
                SELECT last_insert_rowid();
                """;
            cmd.Parameters.AddWithValue("$created", now);
            cmd.Parameters.AddWithValue("$client", clientId);
            cmd.Parameters.AddWithValue("$reply", (object?)replyTo ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$md", markdown);
            id = (long)cmd.ExecuteScalar()!;
        }

        foreach (var tag in tags)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT OR IGNORE INTO message_tags(tag, message_id) VALUES ($tag, $id);";
            cmd.Parameters.AddWithValue("$tag", tag);
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }

        var tagsJoined = string.Join(" ", tags);
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO messages_fts(rowid, markdown, tags) VALUES ($id, $md, $tags);";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$md", markdown);
            cmd.Parameters.AddWithValue("$tags", tagsJoined);
            cmd.ExecuteNonQuery();
        }

        if (replyTo.HasValue)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                UPDATE messages
                SET reply_count = reply_count + 1,
                    last_activity_at = $now,
                    independent_clients = (
                        SELECT COUNT(DISTINCT client_id) FROM messages
                        WHERE id = $parent OR reply_to = $parent
                    )
                WHERE id = $parent;
                """;
            cmd.Parameters.AddWithValue("$now", now);
            cmd.Parameters.AddWithValue("$parent", replyTo.Value);
            cmd.ExecuteNonQuery();
        }

        return id;
    }

    public int ApplyVote(SqliteConnection conn, SqliteTransaction tx, long messageId, string clientId, int value)
    {
        using (var check = conn.CreateCommand())
        {
            check.Transaction = tx;
            check.CommandText = "SELECT 1 FROM messages WHERE id=$id;";
            check.Parameters.AddWithValue("$id", messageId);
            if (check.ExecuteScalar() is null)
                throw new InvalidOperationException("NOT_FOUND");
        }

        int previous = 0;
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT value FROM votes WHERE message_id=$m AND client_id=$c;";
            cmd.Parameters.AddWithValue("$m", messageId);
            cmd.Parameters.AddWithValue("$c", clientId);
            var o = cmd.ExecuteScalar();
            if (o is not null and not DBNull)
                previous = Convert.ToInt32(o);
        }

        var delta = value - previous;
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO votes(message_id, client_id, value) VALUES ($m, $c, $v)
                ON CONFLICT(message_id, client_id) DO UPDATE SET value=excluded.value;
                """;
            cmd.Parameters.AddWithValue("$m", messageId);
            cmd.Parameters.AddWithValue("$c", clientId);
            cmd.Parameters.AddWithValue("$v", value);
            cmd.ExecuteNonQuery();
        }

        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "UPDATE messages SET score = score + $d WHERE id=$id; SELECT score FROM messages WHERE id=$id;";
            cmd.Parameters.AddWithValue("$d", delta);
            cmd.Parameters.AddWithValue("$id", messageId);
            return Convert.ToInt32(cmd.ExecuteScalar());
        }
    }

    public MessageRecord? GetMessage(long id)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, created_at, client_id, reply_to, markdown, score, reply_count, ref_count,
                   independent_clients, last_activity_at, protected
            FROM messages WHERE id=$id;
            """;
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        var msg = ReadMessage(r);
        r.Close();
        msg = msg with { Tags = LoadTags(conn, id) };
        return msg;
    }

    public IReadOnlyList<long> GetReplyIds(long id, int limit)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id FROM messages WHERE reply_to=$id ORDER BY id ASC LIMIT $lim;";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$lim", limit);
        var list = new List<long>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(r.GetInt64(0));
        return list;
    }

    public IReadOnlyList<MessageRecord> GetRecent(int limit, long? since)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        if (since.HasValue)
        {
            cmd.CommandText = """
                SELECT id, created_at, client_id, reply_to, markdown, score, reply_count, ref_count,
                       independent_clients, last_activity_at, protected
                FROM messages WHERE id > $since ORDER BY id ASC LIMIT $lim;
                """;
            cmd.Parameters.AddWithValue("$since", since.Value);
        }
        else
        {
            cmd.CommandText = """
                SELECT id, created_at, client_id, reply_to, markdown, score, reply_count, ref_count,
                       independent_clients, last_activity_at, protected
                FROM messages ORDER BY id DESC LIMIT $lim;
                """;
        }
        cmd.Parameters.AddWithValue("$lim", limit);
        return ReadMessageList(conn, cmd, reverse: !since.HasValue);
    }

    public IReadOnlyList<MessageRecord> Search(string query, int limit)
    {
        using var conn = OpenConnection();
        var ftsQuery = BuildFtsQuery(query);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT m.id, m.created_at, m.client_id, m.reply_to, m.markdown, m.score, m.reply_count, m.ref_count,
                   m.independent_clients, m.last_activity_at, m.protected
            FROM messages_fts f
            JOIN messages m ON m.id = f.rowid
            WHERE messages_fts MATCH $q
            ORDER BY m.score DESC, m.id DESC
            LIMIT $lim;
            """;
        cmd.Parameters.AddWithValue("$q", ftsQuery);
        cmd.Parameters.AddWithValue("$lim", limit);
        try
        {
            return ReadMessageList(conn, cmd, reverse: false);
        }
        catch (SqliteException)
        {
            // fallback: tag exact match
            using var cmd2 = conn.CreateCommand();
            cmd2.CommandText = """
                SELECT m.id, m.created_at, m.client_id, m.reply_to, m.markdown, m.score, m.reply_count, m.ref_count,
                       m.independent_clients, m.last_activity_at, m.protected
                FROM messages m
                JOIN message_tags t ON t.message_id = m.id
                WHERE t.tag = $tag
                ORDER BY m.score DESC, m.id DESC
                LIMIT $lim;
                """;
            cmd2.Parameters.AddWithValue("$tag", query.Trim().ToLowerInvariant());
            cmd2.Parameters.AddWithValue("$lim", limit);
            return ReadMessageList(conn, cmd2, reverse: false);
        }
    }

    public IReadOnlyList<MessageRecord> GetByTag(string tag, int limit, bool best)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = best
            ? """
                SELECT m.id, m.created_at, m.client_id, m.reply_to, m.markdown, m.score, m.reply_count, m.ref_count,
                       m.independent_clients, m.last_activity_at, m.protected
                FROM messages m
                JOIN message_tags t ON t.message_id = m.id
                WHERE t.tag = $tag
                ORDER BY m.score DESC, m.id DESC
                LIMIT $lim;
                """
            : """
                SELECT m.id, m.created_at, m.client_id, m.reply_to, m.markdown, m.score, m.reply_count, m.ref_count,
                       m.independent_clients, m.last_activity_at, m.protected
                FROM messages m
                JOIN message_tags t ON t.message_id = m.id
                WHERE t.tag = $tag
                ORDER BY m.id DESC
                LIMIT $lim;
                """;
        cmd.Parameters.AddWithValue("$tag", tag.ToLowerInvariant());
        cmd.Parameters.AddWithValue("$lim", limit);
        return ReadMessageList(conn, cmd, reverse: false);
    }

    public IReadOnlyList<string> GetRelatedTags(string tag, int limit)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT t2.tag, COUNT(*) AS c
            FROM message_tags t1
            JOIN message_tags t2 ON t1.message_id = t2.message_id AND t1.tag != t2.tag
            WHERE t1.tag = $tag
            GROUP BY t2.tag
            ORDER BY c DESC
            LIMIT $lim;
            """;
        cmd.Parameters.AddWithValue("$tag", tag.ToLowerInvariant());
        cmd.Parameters.AddWithValue("$lim", limit);
        var list = new List<string>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(r.GetString(0));
        return list;
    }

    public IReadOnlyList<MessageRecord> GetRelatedMessages(long id, int limit)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT m.id, m.created_at, m.client_id, m.reply_to, m.markdown, m.score, m.reply_count, m.ref_count,
                   m.independent_clients, m.last_activity_at, m.protected, COUNT(*) AS overlap
            FROM message_tags t1
            JOIN message_tags t2 ON t1.tag = t2.tag AND t1.message_id != t2.message_id
            JOIN messages m ON m.id = t2.message_id
            WHERE t1.message_id = $id
            GROUP BY m.id
            ORDER BY overlap DESC, m.score DESC
            LIMIT $lim;
            """;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$lim", limit);
        return ReadMessageList(conn, cmd, reverse: false);
    }

    public IReadOnlyList<MessageRecord> GetThread(long rootId, int depth, int limit)
    {
        var result = new List<MessageRecord>();
        var root = GetMessage(rootId);
        if (root is null) return result;
        result.Add(root);
        CollectChildren(rootId, 1, depth, limit, result);
        return result;
    }

    private void CollectChildren(long parentId, int level, int maxDepth, int limit, List<MessageRecord> sink)
    {
        if (level > maxDepth || sink.Count >= limit) return;
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, created_at, client_id, reply_to, markdown, score, reply_count, ref_count,
                   independent_clients, last_activity_at, protected
            FROM messages WHERE reply_to=$id ORDER BY id ASC LIMIT $lim;
            """;
        cmd.Parameters.AddWithValue("$id", parentId);
        cmd.Parameters.AddWithValue("$lim", Math.Max(1, limit - sink.Count));
        var children = ReadMessageList(conn, cmd, reverse: false);
        foreach (var child in children)
        {
            if (sink.Count >= limit) break;
            sink.Add(child);
            CollectChildren(child.Id, level + 1, maxDepth, limit, sink);
        }
    }

    public BoardStats GetStats()
    {
        using var conn = OpenConnection();
        long messages = ScalarLong(conn, "SELECT COUNT(*) FROM messages;");
        long tags = ScalarLong(conn, "SELECT COUNT(DISTINCT tag) FROM message_tags;");
        long clients = ScalarLong(conn, "SELECT COUNT(*) FROM clients;");
        long dbBytes = 0;
        var dbPath = Path.GetFullPath(_options.DbPath);
        if (File.Exists(dbPath)) dbBytes = new FileInfo(dbPath).Length;
        long archiveBytes = 0;
        var archiveDir = Path.GetFullPath(_options.ArchivePath);
        if (Directory.Exists(archiveDir))
        {
            foreach (var f in Directory.EnumerateFiles(archiveDir, "*.md.gz"))
                archiveBytes += new FileInfo(f).Length;
        }

        return new BoardStats
        {
            MessageCount = messages,
            TagCount = tags,
            ClientCount = clients,
            HotLimit = _options.HotMessages,
            ArchiveBytes = archiveBytes,
            DbBytes = dbBytes
        };
    }

    public IReadOnlyList<long> GetSitemapMessageIds(int limit)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id FROM messages
            WHERE protected = 1 OR score >= 5 OR reply_count >= 2 OR independent_clients >= 2
            ORDER BY score DESC, reply_count DESC, id DESC
            LIMIT $lim;
            """;
        cmd.Parameters.AddWithValue("$lim", limit);
        var list = new List<long>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(r.GetInt64(0));
        return list;
    }

    public int EvictIfNeeded(ArchiveWriter archive)
    {
        lock (_writeLock)
        {
            using var conn = OpenConnection();
            var count = (long)ScalarLong(conn, "SELECT COUNT(*) FROM messages;");
            if (count <= _options.HotMessages) return 0;

            var toRemove = (int)Math.Min(_options.EvictionBatchSize, count - _options.HotMessages);
            using var cmd = conn.CreateCommand();
            // Lower score = more likely to evict. Protect high engagement.
            cmd.CommandText = """
                SELECT id, created_at, client_id, reply_to, markdown, score, reply_count, ref_count,
                       independent_clients, last_activity_at, protected
                FROM messages
                WHERE protected = 0
                ORDER BY
                  (score * 3 + reply_count * 5 + independent_clients * 4 + ref_count * 2)
                  / (1.0 + (julianday('now') - julianday(created_at)))
                  ASC,
                  id ASC
                LIMIT $lim;
                """;
            cmd.Parameters.AddWithValue("$lim", toRemove);
            var victims = ReadMessageList(conn, cmd, reverse: false);
            if (victims.Count == 0) return 0;

            using var tx = conn.BeginTransaction();
            foreach (var m in victims)
            {
                archive.Append(m);
                using (var del = conn.CreateCommand())
                {
                    del.Transaction = tx;
                    del.CommandText = "DELETE FROM messages_fts WHERE rowid=$id;";
                    del.Parameters.AddWithValue("$id", m.Id);
                    del.ExecuteNonQuery();
                }
                using (var del = conn.CreateCommand())
                {
                    del.Transaction = tx;
                    del.CommandText = "DELETE FROM message_tags WHERE message_id=$id;";
                    del.Parameters.AddWithValue("$id", m.Id);
                    del.ExecuteNonQuery();
                }
                using (var del = conn.CreateCommand())
                {
                    del.Transaction = tx;
                    del.CommandText = "DELETE FROM votes WHERE message_id=$id;";
                    del.Parameters.AddWithValue("$id", m.Id);
                    del.ExecuteNonQuery();
                }
                using (var del = conn.CreateCommand())
                {
                    del.Transaction = tx;
                    del.CommandText = "DELETE FROM messages WHERE id=$id;";
                    del.Parameters.AddWithValue("$id", m.Id);
                    del.ExecuteNonQuery();
                }
            }
            tx.Commit();
            return victims.Count;
        }
    }

    public void CheckpointPassive()
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA wal_checkpoint(PASSIVE);";
        cmd.ExecuteNonQuery();
    }

    public void MarkProtected(long id)
    {
        lock (_writeLock)
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE messages SET protected=1 WHERE id=$id;";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }
    }

    private static long ScalarLong(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    private List<MessageRecord> ReadMessageList(SqliteConnection conn, SqliteCommand cmd, bool reverse)
    {
        var list = new List<MessageRecord>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(ReadMessage(r));
        r.Close();
        for (var i = 0; i < list.Count; i++)
            list[i] = list[i] with { Tags = LoadTags(conn, list[i].Id) };
        if (reverse) list.Reverse();
        return list;
    }

    private static MessageRecord ReadMessage(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(0),
        CreatedAt = DateTimeOffset.Parse(r.GetString(1)),
        ClientId = r.GetString(2),
        ReplyTo = r.IsDBNull(3) ? null : r.GetInt64(3),
        Markdown = r.GetString(4),
        Score = r.GetInt32(5),
        ReplyCount = r.GetInt32(6),
        RefCount = r.GetInt32(7),
        IndependentClients = r.GetInt32(8),
        LastActivityAt = DateTimeOffset.Parse(r.GetString(9)),
        Protected = r.GetInt32(10) != 0
    };

    public WriteSessionRecord CreateWriteSession(string clientId, string token, int ttlSeconds)
    {
        var id = ToCrockfordBase32(RandomNumberGenerator.GetBytes(6));
        var now = DateTimeOffset.UtcNow;
        var expires = now.AddSeconds(Math.Max(60, ttlSeconds));

        lock (_writeLock)
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO write_sessions(id, client_id, token, draft, tags, mode, action_version, created_at, expires_at, last_action_at)
                VALUES ($id, $client, $token, '', '', 'words', 0, $created, $expires, $created);
                """;
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$client", clientId);
            cmd.Parameters.AddWithValue("$token", token);
            cmd.Parameters.AddWithValue("$created", now.ToString("O"));
            cmd.Parameters.AddWithValue("$expires", expires.ToString("O"));
            cmd.ExecuteNonQuery();
        }

        return new WriteSessionRecord
        {
            Id = id,
            ClientId = clientId,
            Token = token,
            Draft = "",
            Tags = "",
            Mode = "words",
            ActionVersion = 0,
            CreatedAt = now,
            ExpiresAt = expires,
            LastActionAt = now
        };
    }

    public WriteSessionRecord? GetWriteSession(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, client_id, token, draft, tags, mode, action_version, created_at, expires_at, last_action_at
            FROM write_sessions WHERE id=$id LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return ReadSession(r);
    }

    public WriteSessionRecord? TryApplySessionAction(
        string id,
        long expectedVersion,
        Func<WriteSessionRecord, (string Draft, string Tags, string Mode, bool Expire)> mutate)
    {
        lock (_writeLock)
        {
            using var conn = OpenConnection();
            using var tx = conn.BeginTransaction();
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = """
                    SELECT id, client_id, token, draft, tags, mode, action_version, created_at, expires_at, last_action_at
                    FROM write_sessions WHERE id=$id LIMIT 1;
                    """;
                cmd.Parameters.AddWithValue("$id", id);
                using var r = cmd.ExecuteReader();
                if (!r.Read()) return null;
                var session = ReadSession(r);
                r.Close();

                if (session.ExpiresAt <= DateTimeOffset.UtcNow)
                    return session;
                if (session.ActionVersion != expectedVersion)
                    return session;

                var (draft, tags, mode, expire) = mutate(session);
                var now = DateTimeOffset.UtcNow;
                var expires = expire ? now : session.ExpiresAt;
                var newVersion = session.ActionVersion + 1;

                using var upd = conn.CreateCommand();
                upd.Transaction = tx;
                upd.CommandText = """
                    UPDATE write_sessions
                    SET draft=$draft, tags=$tags, mode=$mode, action_version=$ver,
                        expires_at=$expires, last_action_at=$now
                    WHERE id=$id AND action_version=$old;
                    """;
                upd.Parameters.AddWithValue("$draft", draft);
                upd.Parameters.AddWithValue("$tags", tags);
                upd.Parameters.AddWithValue("$mode", mode);
                upd.Parameters.AddWithValue("$ver", newVersion);
                upd.Parameters.AddWithValue("$expires", expires.ToString("O"));
                upd.Parameters.AddWithValue("$now", now.ToString("O"));
                upd.Parameters.AddWithValue("$id", id);
                upd.Parameters.AddWithValue("$old", expectedVersion);
                if (upd.ExecuteNonQuery() != 1)
                {
                    tx.Rollback();
                    return GetWriteSession(id);
                }

                tx.Commit();
                return new WriteSessionRecord
                {
                    Id = session.Id,
                    ClientId = session.ClientId,
                    Token = session.Token,
                    Draft = draft,
                    Tags = tags,
                    Mode = mode,
                    ActionVersion = newVersion,
                    CreatedAt = session.CreatedAt,
                    ExpiresAt = expires,
                    LastActionAt = now
                };
            }
        }
    }

    public void ExpireWriteSession(string id)
    {
        lock (_writeLock)
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE write_sessions SET expires_at=$now, last_action_at=$now WHERE id=$id;";
            cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }
    }

    public int DeleteExpiredWriteSessions()
    {
        lock (_writeLock)
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM write_sessions WHERE expires_at < $now;";
            cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            return cmd.ExecuteNonQuery();
        }
    }

    public IReadOnlyList<string> GetPopularTags(int limit)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT tag, COUNT(*) AS c
            FROM message_tags
            GROUP BY tag
            ORDER BY c DESC, tag
            LIMIT $lim;
            """;
        cmd.Parameters.AddWithValue("$lim", Math.Max(0, limit));
        var list = new List<string>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(r.GetString(0));
        return list;
    }

    private static WriteSessionRecord ReadSession(SqliteDataReader r) => new()
    {
        Id = r.GetString(0),
        ClientId = r.GetString(1),
        Token = r.GetString(2),
        Draft = r.GetString(3),
        Tags = r.GetString(4),
        Mode = r.GetString(5),
        ActionVersion = r.GetInt64(6),
        CreatedAt = DateTimeOffset.Parse(r.GetString(7)),
        ExpiresAt = DateTimeOffset.Parse(r.GetString(8)),
        LastActionAt = DateTimeOffset.Parse(r.GetString(9))
    };

    private static string ToCrockfordBase32(byte[] bytes)
    {
        const string alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
        var sb = new StringBuilder((bytes.Length * 8 + 4) / 5);
        var buffer = 0;
        var bits = 0;
        foreach (var b in bytes)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                bits -= 5;
                sb.Append(alphabet[(buffer >> bits) & 31]);
            }
        }
        if (bits > 0)
            sb.Append(alphabet[(buffer << (5 - bits)) & 31]);
        return sb.ToString();
    }

    private static IReadOnlyList<string> LoadTags(SqliteConnection conn, long id)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT tag FROM message_tags WHERE message_id=$id ORDER BY tag;";
        cmd.Parameters.AddWithValue("$id", id);
        var tags = new List<string>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) tags.Add(r.GetString(0));
        return tags;
    }

    private static string HashToken(string token)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string BuildFtsQuery(string raw)
    {
        // Support phrases in quotes and free terms; escape quotes for FTS.
        var parts = new List<string>();
        var s = raw.Trim();
        var i = 0;
        while (i < s.Length)
        {
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
            if (i >= s.Length) break;
            if (s[i] == '"')
            {
                var end = s.IndexOf('"', i + 1);
                if (end < 0) end = s.Length;
                var phrase = s[(i + 1)..end].Replace("\"", "");
                if (!string.IsNullOrWhiteSpace(phrase))
                    parts.Add("\"" + phrase.Replace("\"", "\"\"") + "\"");
                i = end + 1;
            }
            else
            {
                var start = i;
                while (i < s.Length && !char.IsWhiteSpace(s[i])) i++;
                var term = s[start..i];
                term = new string(term.Where(c => char.IsLetterOrDigit(c) || c is '_' or '-' or '#').ToArray());
                if (term.StartsWith('#')) term = term[1..];
                if (!string.IsNullOrWhiteSpace(term))
                    parts.Add(term + "*");
            }
        }
        return parts.Count == 0 ? "\"\"" : string.Join(" AND ", parts);
    }
}
