-- 009: i motivi di un turno negato entrano nell'audit (S83).
--
-- `decisions` esisteva dal primo schema con `attempt_ordinal NOT NULL CHECK(attempt_ordinal IN (1,2))`:
-- un ordinale valido presupponeva un tentativo. Ma un turno **negato** si ferma prima di crearne uno,
-- quindi i motivi per cui ogni candidato e' stato escluso non avevano una riga in cui stare, e
-- andavano persi. L'audit sapeva dire che un'esecuzione era `denied`, non perche'.
--
-- E' il caso in cui la domanda «perche'?» pesa di piu': quando il turno e' avvenuto, l'operatore ha
-- almeno la risposta del servizio; quando e' stato negato non ha niente.
--
-- L'ordinale **0** significa «deciso prima di ogni tentativo». Non e' un valore sentinella scelto per
-- comodita': mantiene la chiave primaria (esecuzione, ordinale, target) valida e ordinabile, e lascia
-- 1 e 2 al significato che hanno sempre avuto. Le righe esistenti non cambiano.
--
-- In SQLite un CHECK non si altera: la tabella si ricostruisce, come gia' fatto dalla 005 e dalla 007.
CREATE TABLE decisions_new (
  execution_id TEXT NOT NULL REFERENCES executions(id) ON DELETE RESTRICT,
  attempt_ordinal INTEGER NOT NULL CHECK(attempt_ordinal IN (0,1,2)),
  target_id TEXT NOT NULL, allowed INTEGER NOT NULL CHECK(allowed IN (0,1)),
  reasons_json TEXT NOT NULL CHECK(json_valid(reasons_json)),
  score_json TEXT CHECK(score_json IS NULL OR json_valid(score_json)),
  policy_digest TEXT NOT NULL, registry_digest TEXT NOT NULL,
  PRIMARY KEY(execution_id, attempt_ordinal, target_id)
);
INSERT INTO decisions_new(execution_id, attempt_ordinal, target_id, allowed, reasons_json,
                          score_json, policy_digest, registry_digest)
SELECT execution_id, attempt_ordinal, target_id, allowed, reasons_json,
       score_json, policy_digest, registry_digest
FROM decisions;
DROP TABLE decisions;
ALTER TABLE decisions_new RENAME TO decisions;
