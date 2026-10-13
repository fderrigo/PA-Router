-- Run in a migration transaction after 001; insert migration checksum then commit.
CREATE TABLE projects (
 id TEXT PRIMARY KEY, root_hmac TEXT NOT NULL UNIQUE,
 name TEXT NOT NULL, memory_generation INTEGER NOT NULL DEFAULT 0,
 active_embedding_fingerprint TEXT, created_at_ms INTEGER NOT NULL
);
INSERT INTO projects(id, root_hmac, name, created_at_ms)
 VALUES ('00000000-0000-4000-8000-000000000001','legacy-local-project','local-default',0);
ALTER TABLE sessions ADD COLUMN project_id TEXT REFERENCES projects(id) ON DELETE RESTRICT;
UPDATE sessions SET project_id = '00000000-0000-4000-8000-000000000001';
CREATE TRIGGER session_project_required_insert BEFORE INSERT ON sessions
 WHEN NEW.project_id IS NULL BEGIN SELECT RAISE(ABORT,'project_id required'); END;
CREATE TRIGGER session_project_immutable BEFORE UPDATE OF project_id ON sessions
 WHEN NEW.project_id IS NULL OR NEW.project_id != OLD.project_id
 BEGIN SELECT RAISE(ABORT,'project_id immutable'); END;
CREATE TABLE observations (
 id TEXT PRIMARY KEY, project_id TEXT NOT NULL REFERENCES projects(id) ON DELETE RESTRICT,
 session_id TEXT NOT NULL REFERENCES sessions(id) ON DELETE RESTRICT,
 execution_id TEXT REFERENCES executions(id) ON DELETE RESTRICT,
 event_key TEXT NOT NULL, kind TEXT NOT NULL CHECK(kind IN ('decision','discovery','bugfix','test_result','handover','note')),
 verification TEXT NOT NULL CHECK(verification IN ('verified','unverified')),
 classification TEXT NOT NULL CHECK(classification IN ('public','internal','personal','sensitive')),
 scope TEXT NOT NULL CHECK(scope IN ('session','project')),
 published_for_chatbot INTEGER NOT NULL DEFAULT 0 CHECK(published_for_chatbot IN (0,1)),
 version INTEGER NOT NULL CHECK(version > 0),
 content_blob_id TEXT NOT NULL REFERENCES content_blobs(id) ON DELETE RESTRICT,
 created_at_ms INTEGER NOT NULL, expires_at_ms INTEGER NOT NULL, deleted_at_ms INTEGER,
 UNIQUE(project_id,event_key),
 CHECK(expires_at_ms > created_at_ms),
 CHECK(scope = 'session' OR classification IN ('public','internal')),
 CHECK(published_for_chatbot = 0 OR scope = 'project')
);
CREATE TRIGGER observation_project_matches BEFORE INSERT ON observations
 WHEN NEW.project_id != (SELECT project_id FROM sessions WHERE id=NEW.session_id)
 BEGIN SELECT RAISE(ABORT,'session project mismatch'); END;
CREATE TABLE memory_chunks (
 id TEXT PRIMARY KEY, observation_id TEXT NOT NULL REFERENCES observations(id) ON DELETE RESTRICT,
 ordinal INTEGER NOT NULL CHECK(ordinal >= 0), content_hmac TEXT NOT NULL,
 start_byte INTEGER NOT NULL CHECK(start_byte >= 0), byte_length INTEGER NOT NULL CHECK(byte_length > 0),
 UNIQUE(observation_id,ordinal)
);
CREATE TABLE embeddings (
 chunk_id TEXT NOT NULL REFERENCES memory_chunks(id) ON DELETE RESTRICT,
 model_fingerprint TEXT NOT NULL, dimension INTEGER NOT NULL CHECK(dimension BETWEEN 1 AND 2048),
 vector_blob_id TEXT NOT NULL REFERENCES content_blobs(id) ON DELETE RESTRICT,
 state TEXT NOT NULL CHECK(state IN ('ready','superseded')),
 PRIMARY KEY(chunk_id,model_fingerprint)
);
CREATE TABLE memory_jobs (
 id TEXT PRIMARY KEY, observation_id TEXT NOT NULL REFERENCES observations(id) ON DELETE RESTRICT,
 observation_version INTEGER NOT NULL, model_fingerprint TEXT NOT NULL,
 kind TEXT NOT NULL CHECK(kind IN ('index','purge')),
 state TEXT NOT NULL CHECK(state IN ('pending','running','completed','failed')),
 attempts INTEGER NOT NULL DEFAULT 0 CHECK(attempts BETWEEN 0 AND 3),
 available_at_ms INTEGER NOT NULL, lease_expires_at_ms INTEGER, error_code TEXT,
 UNIQUE(observation_id,observation_version,model_fingerprint,kind)
);
CREATE INDEX ix_memory_scope ON observations(project_id,scope,session_id,deleted_at_ms,expires_at_ms);
CREATE INDEX ix_memory_jobs ON memory_jobs(state,available_at_ms);
CREATE TABLE plugins (
 id TEXT NOT NULL, version TEXT NOT NULL, install_digest TEXT NOT NULL UNIQUE,
 manifest_json TEXT NOT NULL CHECK(json_valid(manifest_json)),
 state TEXT NOT NULL CHECK(state IN ('installed','enabled','disabled')),
 installed_at_ms INTEGER NOT NULL, PRIMARY KEY(id,version)
);
CREATE TABLE plugin_grants (
 install_digest TEXT NOT NULL REFERENCES plugins(install_digest) ON DELETE RESTRICT,
 project_id TEXT NOT NULL REFERENCES projects(id) ON DELETE RESTRICT,
 permissions_json TEXT NOT NULL CHECK(json_valid(permissions_json)),
 granted_at_ms INTEGER NOT NULL, revoked_at_ms INTEGER,
 PRIMARY KEY(install_digest,project_id)
);
