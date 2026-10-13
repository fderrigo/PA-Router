-- Run in a migration transaction after 006; insert migration checksum then commit.
-- Stato `tool_calling` negli eventi di un'esecuzione (ADR-020, S46). Mentre il router esegue gli strumenti che
-- il modello ha chiesto, lo stream SSE lo dice: la fase, non quali strumenti ne' con quali argomenti.
--
-- SQLite non sa modificare un CHECK: la tabella si ricostruisce e le righe si ricopiano tali e quali, perche'
-- nessuna di esse puo' violare un insieme di stati che si allarga.
CREATE TABLE execution_events_new (
  execution_id TEXT NOT NULL REFERENCES executions(id) ON DELETE RESTRICT,
  sequence INTEGER NOT NULL CHECK(sequence > 0),
  state TEXT NOT NULL CHECK(state IN ('accepted','analyzing','evaluating','routing','running','tool_calling','screening','completed','denied','failed','cancelled','uncertain')),
  created_at_ms INTEGER NOT NULL, expires_at_ms INTEGER NOT NULL,
  PRIMARY KEY(execution_id, sequence)
);
INSERT INTO execution_events_new(execution_id, sequence, state, created_at_ms, expires_at_ms)
 SELECT execution_id, sequence, state, created_at_ms, expires_at_ms FROM execution_events;
DROP TABLE execution_events;
ALTER TABLE execution_events_new RENAME TO execution_events;
