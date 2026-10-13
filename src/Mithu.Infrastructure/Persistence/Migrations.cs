namespace Mithu.Infrastructure.Persistence;

/// <summary>
/// Database schema, one script per version (index 0 = v1). Never edit a released script: add a new one.
/// Times are stored as sortable ISO-8601 UTC text.
/// </summary>
internal static class Migrations
{
    public static IReadOnlyList<string> Scripts { get; } =
    [
        // v1: notes, tags and full-text search.
        """
        CREATE TABLE notes (
            id             TEXT PRIMARY KEY NOT NULL,
            title          TEXT NOT NULL DEFAULT '',
            content        TEXT NOT NULL DEFAULT '',
            is_pinned      INTEGER NOT NULL DEFAULT 0,
            created_at_utc TEXT NOT NULL,
            updated_at_utc TEXT NOT NULL
        );

        CREATE TABLE note_tags (
            note_id TEXT NOT NULL REFERENCES notes(id) ON DELETE CASCADE,
            tag     TEXT NOT NULL COLLATE NOCASE,
            PRIMARY KEY (note_id, tag)
        );

        CREATE INDEX ix_note_tags_tag ON note_tags(tag);
        CREATE INDEX ix_notes_updated ON notes(updated_at_utc);
        CREATE INDEX ix_notes_created ON notes(created_at_utc);

        -- Word search over titles and content, kept in step with notes by triggers.
        CREATE VIRTUAL TABLE notes_fts USING fts5(
            note_id UNINDEXED, title, content,
            tokenize = 'unicode61 remove_diacritics 2'
        );

        CREATE TRIGGER notes_ai AFTER INSERT ON notes BEGIN
            INSERT INTO notes_fts (note_id, title, content) VALUES (new.id, new.title, new.content);
        END;

        CREATE TRIGGER notes_au AFTER UPDATE OF title, content ON notes BEGIN
            DELETE FROM notes_fts WHERE note_id = old.id;
            INSERT INTO notes_fts (note_id, title, content) VALUES (new.id, new.title, new.content);
        END;

        CREATE TRIGGER notes_ad AFTER DELETE ON notes BEGIN
            DELETE FROM notes_fts WHERE note_id = old.id;
        END;
        """,

        // v2: archived notes; settings and focus timer move from JSON files into the database.
        """
        ALTER TABLE notes ADD COLUMN is_archived INTEGER NOT NULL DEFAULT 0;
        CREATE INDEX ix_notes_archived ON notes(is_archived);

        -- Key/value preferences ("app.dockToTop" = "true"). Values are JSON text.
        CREATE TABLE settings (
            key   TEXT PRIMARY KEY NOT NULL,
            value TEXT NOT NULL
        );

        -- The focus countdown that is running or paused (at most one).
        CREATE TABLE timer_sessions (
            id           INTEGER PRIMARY KEY CHECK (id = 1),
            kind         TEXT NOT NULL,
            duration_ms  INTEGER NOT NULL,
            remaining_ms INTEGER NOT NULL,
            deadline_utc TEXT NULL,
            intention    TEXT NULL
        );
        """,

        // v3: reminders. start_local (wall clock in time_zone_id) anchors the repeat; due_at_utc is the current
        // occurrence; last_notified_at_utc stops the same alert from showing twice.
        """
        CREATE TABLE reminders (
            id                   TEXT PRIMARY KEY NOT NULL,
            title                TEXT NOT NULL,
            notes                TEXT NOT NULL DEFAULT '',
            start_local          TEXT NOT NULL,
            time_zone_id         TEXT NOT NULL,
            repeat_kind          TEXT NOT NULL DEFAULT 'None',
            repeat_every         INTEGER NOT NULL DEFAULT 1,
            repeat_unit          TEXT NOT NULL DEFAULT 'Days',
            due_at_utc           TEXT NOT NULL,
            snoozed_until_utc    TEXT NULL,
            priority             TEXT NOT NULL DEFAULT 'Normal',
            status               TEXT NOT NULL DEFAULT 'Pending',
            last_notified_at_utc TEXT NULL,
            done_at_utc          TEXT NULL,
            created_at_utc       TEXT NOT NULL,
            updated_at_utc       TEXT NOT NULL
        );

        CREATE INDEX ix_reminders_status_due ON reminders(status, due_at_utc);
        """,

        // v4: AI chats. parts_json keeps every part of a message (text, tool calls, Claude thinking) so a chat can be
        // continued exactly; text is the visible text, for the list and search.
        """
        CREATE TABLE chats (
            id             TEXT PRIMARY KEY NOT NULL,
            title          TEXT NOT NULL,
            created_at_utc TEXT NOT NULL,
            updated_at_utc TEXT NOT NULL
        );

        CREATE TABLE chat_messages (
            id             INTEGER PRIMARY KEY AUTOINCREMENT,
            chat_id        TEXT NOT NULL REFERENCES chats(id) ON DELETE CASCADE,
            role           TEXT NOT NULL,
            parts_json     TEXT NOT NULL,
            text           TEXT NOT NULL DEFAULT '',
            model          TEXT NULL,
            created_at_utc TEXT NOT NULL
        );

        CREATE INDEX ix_chat_messages_chat ON chat_messages(chat_id, id);
        CREATE INDEX ix_chats_updated ON chats(updated_at_utc);
        """,

        // v5: Desktop control action log (Phase 5.3): what a tool call changed (file moves, copies, renames, new
        // folders) so it can be undone. Paths only, never file contents. Kept when a chat is deleted.
        """
        CREATE TABLE actions (
            id             INTEGER PRIMARY KEY AUTOINCREMENT,
            chat_id        TEXT NOT NULL,
            call_id        TEXT NOT NULL,
            summary        TEXT NOT NULL,
            ops_json       TEXT NOT NULL,
            undone         INTEGER NOT NULL DEFAULT 0,
            created_at_utc TEXT NOT NULL
        );

        CREATE INDEX ix_actions_chat ON actions(chat_id, id);
        """,

        // v6: To-do lists and tasks (no dates: anything with a time is a reminder). "My tasks" always exists.
        """
        CREATE TABLE todo_lists (
            id             TEXT PRIMARY KEY NOT NULL,
            name           TEXT NOT NULL,
            sort_order     INTEGER NOT NULL DEFAULT 0,
            created_at_utc TEXT NOT NULL
        );

        CREATE TABLE todos (
            id             TEXT PRIMARY KEY NOT NULL,
            list_id        TEXT NOT NULL,
            title          TEXT NOT NULL,
            important      INTEGER NOT NULL DEFAULT 0,
            done           INTEGER NOT NULL DEFAULT 0,
            sort_order     INTEGER NOT NULL DEFAULT 0,
            created_at_utc TEXT NOT NULL,
            updated_at_utc TEXT NOT NULL,
            done_at_utc    TEXT NULL
        );

        CREATE INDEX ix_todos_list ON todos(list_id, done, sort_order);

        INSERT INTO todo_lists (id, name, sort_order, created_at_utc) VALUES ('inbox', 'My tasks', 0, '2026-01-01T00:00:00.0000000Z');
        """,
    ];
}
