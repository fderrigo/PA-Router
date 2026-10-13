-- 008: nome leggibile e archiviazione di una sessione (S62).
--
-- Un identificatore e' un GUID: riconoscere una conversazione fra dieci GUID non e' possibile, e un
-- selettore che li elenca non aiuta nessuno. Il nome lo sceglie l'operatore, quindi non si puo'
-- derivare da cio' che c'e' gia': serve una colonna.
--
-- L'archiviazione e' l'altra meta': togliere dalla vista senza cancellare. Cancellare una sessione
-- rimuove contenuti ed esecuzioni; archiviarla la lascia dov'e' e smette di proporla. Sono due
-- decisioni diverse e devono restare due operazioni diverse.
--
-- Entrambe le colonne sono facoltative: una sessione senza nome resta valida, e una non archiviata
-- ha `archived_at_ms` nullo. Nessuna riga esistente cambia significato.
ALTER TABLE sessions ADD COLUMN display_name TEXT;
ALTER TABLE sessions ADD COLUMN archived_at_ms INTEGER;

-- Il nome e' unico fra le sessioni che ne hanno uno: riprendere «rifattorizzazione» deve scegliere
-- una conversazione sola. L'indice parziale lascia fuori i NULL, che restano molti.
CREATE UNIQUE INDEX ux_sessions_display_name ON sessions(display_name) WHERE display_name IS NOT NULL;

-- Il selettore elenca le sessioni non archiviate dalla piu' recente: e' l'unica interrogazione che
-- il terminale fa su questa tabella, e senza indice diventa una scansione quando le sessioni sono
-- molte.
CREATE INDEX ix_sessions_recent ON sessions(archived_at_ms, created_at_ms DESC);
