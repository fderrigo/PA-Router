PRAGMA foreign_keys = ON;
PRAGMA journal_mode = WAL;
PRAGMA synchronous = FULL;
PRAGMA busy_timeout = 5000;
CREATE TABLE schema_migrations (
  version INTEGER PRIMARY KEY, checksum TEXT NOT NULL, applied_at_ms INTEGER NOT NULL
);
CREATE TABLE sessions (
  id TEXT PRIMARY KEY,
  profile_id TEXT NOT NULL,
  channel TEXT NOT NULL CHECK(channel IN ('personal_cli','chatbot')),
  state TEXT NOT NULL CHECK(state IN ('active','closed')),
  recovery_required INTEGER NOT NULL DEFAULT 0 CHECK(recovery_required IN (0,1)),
  version INTEGER NOT NULL DEFAULT 0 CHECK(version >= 0),
  token_hmac TEXT UNIQUE,
  content_mode TEXT NOT NULL CHECK(content_mode IN ('memory_only','encrypted')),
  created_at_ms INTEGER NOT NULL, expires_at_ms INTEGER NOT NULL,
  CHECK(expires_at_ms > created_at_ms),
  CHECK(channel != 'chatbot' OR token_hmac IS NOT NULL)
);
CREATE TABLE executions (
  id TEXT PRIMARY KEY,
  session_id TEXT NOT NULL REFERENCES sessions(id) ON DELETE RESTRICT,
  state TEXT NOT NULL CHECK(state IN ('accepted','analyzing','evaluating','routing','running','screening','completed','denied','failed','cancelled','uncertain')),
  policy_digest TEXT NOT NULL, registry_digest TEXT NOT NULL,
  input_hmac TEXT NOT NULL, request_version INTEGER NOT NULL DEFAULT 1,
  error_code TEXT,
  content_availability TEXT NOT NULL DEFAULT 'unavailable' CHECK(content_availability IN ('available','unavailable','expired')),
  usage_json TEXT CHECK(usage_json IS NULL OR json_valid(usage_json)),
  created_at_ms INTEGER NOT NULL, updated_at_ms INTEGER NOT NULL,
  deadline_at_ms INTEGER NOT NULL
);
CREATE INDEX ix_executions_session ON executions(session_id, created_at_ms);
CREATE TABLE attempts (
  id TEXT PRIMARY KEY, execution_id TEXT NOT NULL REFERENCES executions(id) ON DELETE RESTRICT,
  ordinal INTEGER NOT NULL CHECK(ordinal IN (1,2)), target_id TEXT NOT NULL,
  state TEXT NOT NULL CHECK(state IN ('prepared','dispatching','running','completed','failed','cancelled','uncertain')),
  side_effect_state TEXT NOT NULL CHECK(side_effect_state IN ('none','possible','observed','unknown')),
  grant_nonce_hmac TEXT NOT NULL UNIQUE, payload_hmac TEXT NOT NULL,
  started_at_ms INTEGER, ended_at_ms INTEGER, error_code TEXT,
  UNIQUE(execution_id, ordinal), UNIQUE(execution_id, target_id)
);
CREATE TABLE decisions (
  execution_id TEXT NOT NULL REFERENCES executions(id) ON DELETE RESTRICT,
  attempt_ordinal INTEGER NOT NULL CHECK(attempt_ordinal IN (1,2)),
  target_id TEXT NOT NULL, allowed INTEGER NOT NULL CHECK(allowed IN (0,1)),
  reasons_json TEXT NOT NULL CHECK(json_valid(reasons_json)),
  score_json TEXT CHECK(score_json IS NULL OR json_valid(score_json)),
  policy_digest TEXT NOT NULL, registry_digest TEXT NOT NULL,
  PRIMARY KEY(execution_id, attempt_ordinal, target_id)
);
CREATE TABLE audit_events (
  sequence INTEGER PRIMARY KEY AUTOINCREMENT, event_id TEXT NOT NULL UNIQUE,
  execution_id TEXT REFERENCES executions(id) ON DELETE RESTRICT,
  attempt_id TEXT REFERENCES attempts(id) ON DELETE RESTRICT,
  code TEXT NOT NULL, metadata_json TEXT NOT NULL CHECK(json_valid(metadata_json)),
  created_at_ms INTEGER NOT NULL
);
CREATE INDEX ix_audit_execution ON audit_events(execution_id, sequence);
CREATE TABLE execution_events (
  execution_id TEXT NOT NULL REFERENCES executions(id) ON DELETE RESTRICT,
  sequence INTEGER NOT NULL CHECK(sequence > 0),
  state TEXT NOT NULL CHECK(state IN ('accepted','analyzing','evaluating','routing','running','screening','completed','denied','failed','cancelled','uncertain')),
  created_at_ms INTEGER NOT NULL, expires_at_ms INTEGER NOT NULL,
  PRIMARY KEY(execution_id, sequence)
);
CREATE TABLE content_blobs (
  id TEXT PRIMARY KEY, session_id TEXT NOT NULL REFERENCES sessions(id) ON DELETE RESTRICT,
  execution_id TEXT REFERENCES executions(id) ON DELETE RESTRICT,
  kind TEXT NOT NULL CHECK(kind IN ('request','transcript','output','capsule','checkpoint','observation','embedding')),
  schema_version INTEGER NOT NULL DEFAULT 1,
  relative_path TEXT NOT NULL UNIQUE CHECK(relative_path NOT LIKE '%..%' AND relative_path LIKE 'content/%'),
  cipher_sha256 TEXT NOT NULL, byte_length INTEGER NOT NULL CHECK(byte_length > 0),
  created_at_ms INTEGER NOT NULL, expires_at_ms INTEGER NOT NULL
);
CREATE TABLE idempotency_keys (
  session_id TEXT NOT NULL REFERENCES sessions(id) ON DELETE RESTRICT,
  key_hmac TEXT NOT NULL, request_hmac TEXT NOT NULL,
  execution_id TEXT NOT NULL REFERENCES executions(id) ON DELETE RESTRICT,
  created_at_ms INTEGER NOT NULL, expires_at_ms INTEGER NOT NULL,
  PRIMARY KEY(session_id, key_hmac)
);
CREATE INDEX ix_blobs_expiry ON content_blobs(expires_at_ms);
CREATE INDEX ix_idempotency_expiry ON idempotency_keys(expires_at_ms);
-- Runner starts BEGIN IMMEDIATE before DDL (after PRAGMAs), inserts the migration
-- version/checksum using parameters after DDL and commits once. Rollback on error.
