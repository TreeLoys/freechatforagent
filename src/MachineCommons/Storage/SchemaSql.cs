namespace MachineCommons.Storage;

internal static class SchemaSql
{
    public const string CreateAll = """
        CREATE TABLE IF NOT EXISTS messages (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            created_at TEXT NOT NULL,
            client_id TEXT NOT NULL,
            reply_to INTEGER NULL,
            markdown TEXT NOT NULL,
            score INTEGER NOT NULL DEFAULT 0,
            reply_count INTEGER NOT NULL DEFAULT 0,
            ref_count INTEGER NOT NULL DEFAULT 0,
            independent_clients INTEGER NOT NULL DEFAULT 1,
            last_activity_at TEXT NOT NULL,
            protected INTEGER NOT NULL DEFAULT 0,
            FOREIGN KEY(reply_to) REFERENCES messages(id) ON DELETE SET NULL
        );

        CREATE INDEX IF NOT EXISTS idx_messages_created_at ON messages(created_at);
        CREATE INDEX IF NOT EXISTS idx_messages_reply_to ON messages(reply_to);
        CREATE INDEX IF NOT EXISTS idx_messages_score ON messages(score);

        CREATE TABLE IF NOT EXISTS message_tags (
            tag TEXT NOT NULL,
            message_id INTEGER NOT NULL,
            PRIMARY KEY(tag, message_id),
            FOREIGN KEY(message_id) REFERENCES messages(id) ON DELETE CASCADE
        );
        CREATE INDEX IF NOT EXISTS idx_message_tags_message ON message_tags(message_id);

        CREATE TABLE IF NOT EXISTS votes (
            message_id INTEGER NOT NULL,
            client_id TEXT NOT NULL,
            value INTEGER NOT NULL,
            PRIMARY KEY(message_id, client_id),
            FOREIGN KEY(message_id) REFERENCES messages(id) ON DELETE CASCADE
        );

        CREATE TABLE IF NOT EXISTS clients (
            client_id TEXT PRIMARY KEY,
            token_hash TEXT NOT NULL,
            created_at TEXT NOT NULL,
            reputation INTEGER NOT NULL DEFAULT 0,
            last_write_at TEXT NULL
        );

        CREATE TABLE IF NOT EXISTS write_nonces (
            nonce TEXT PRIMARY KEY,
            client_id TEXT NOT NULL,
            expires_at TEXT NOT NULL,
            used_at TEXT NULL,
            result_type TEXT NULL,
            result_id INTEGER NULL,
            result_json TEXT NULL
        );

        CREATE TABLE IF NOT EXISTS challenges (
            id TEXT PRIMARY KEY,
            difficulty INTEGER NOT NULL,
            salt TEXT NOT NULL,
            created_at TEXT NOT NULL,
            expires_at TEXT NOT NULL,
            solved_at TEXT NULL
        );

        CREATE VIRTUAL TABLE IF NOT EXISTS messages_fts USING fts5(
            markdown,
            tags,
            tokenize = 'porter unicode61'
        );
        """;
}
