-- 010: un grant registra l'abilitazione, non dei permessi (S85).
--
-- `plugin_grants.permissions_json` conservava l'insieme concesso dall'operatore. Dalla S81 nessun
-- percorso di esecuzione consuma un permesso di estensione — il broker degli strumenti e il ponte
-- verso la memoria, gli unici che ne pretendevano uno, sono usciti dal prodotto — e un pacchetto
-- porta solo testo di skill. La colonna descriveva quindi una concessione che non concedeva niente.
--
-- Il **grant resta**, ed e' portante: dice che l'operatore ha abilitato questa installazione per
-- questo progetto, ed e' cio' che una skill pretende per entrare in un turno. Cambia solo che non
-- trasporta piu' un elenco di permessi.
--
-- In SQLite una colonna si puo' togliere con ALTER dalla 3.35, ma la tabella qui si ricostruisce
-- come nella 005, nella 007 e nella 009: la forma e' gia' quella della casa, e mantiene esplicite
-- chiave primaria e chiavi esterne invece di affidarle alla riscrittura implicita.
--
-- Le righe esistenti sopravvivono: si perde l'elenco dei permessi, che non governava piu' nulla, e
-- si conservano installazione, progetto, istante della concessione e istante della revoca.
CREATE TABLE plugin_grants_new (
  install_digest TEXT NOT NULL REFERENCES plugins(install_digest) ON DELETE RESTRICT,
  project_id TEXT NOT NULL REFERENCES projects(id) ON DELETE RESTRICT,
  granted_at_ms INTEGER NOT NULL, revoked_at_ms INTEGER,
  PRIMARY KEY(install_digest,project_id)
);
INSERT INTO plugin_grants_new(install_digest, project_id, granted_at_ms, revoked_at_ms)
SELECT install_digest, project_id, granted_at_ms, revoked_at_ms
FROM plugin_grants;
DROP TABLE plugin_grants;
ALTER TABLE plugin_grants_new RENAME TO plugin_grants;
