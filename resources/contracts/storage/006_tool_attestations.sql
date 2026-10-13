-- Run in a migration transaction after 005; insert migration checksum then commit.
-- Attestazioni degli strumenti dichiarate dalle applicazioni (ADR-020). Il router scopre gli strumenti da
-- solo; quello che non puo' osservare - che dati accettano e restituiscono, dove li trattano - lo dichiara
-- l'applicazione, e il router lo registra qui con la scadenza e l'impronta dello strumento a cui si riferisce.
-- Un'impronta diversa rende la riga inutilizzabile: lo strumento va dichiarato di nuovo.
--
-- Nessuna credenziale, nessun argomento, nessun risultato. Le classi `secret` e `unknown` non sono
-- dichiarabili: nessuna dichiarazione di una sorgente vale abbastanza per mandarle a uno strumento.
CREATE TABLE tool_attestations (
 project_id TEXT NOT NULL,
 source_key TEXT NOT NULL CHECK(length(source_key) BETWEEN 1 AND 2048),
 tool_name TEXT NOT NULL CHECK(length(tool_name) BETWEEN 1 AND 128),
 fingerprint TEXT NOT NULL CHECK(length(fingerprint) = 71 AND substr(fingerprint, 1, 7) = 'sha256:'),
 accepts TEXT NOT NULL CHECK(accepts IN ('public','internal','personal','sensitive')),
 returns TEXT NOT NULL CHECK(returns IN ('public','internal','personal','sensitive')),
 regions_json TEXT NOT NULL CHECK(json_valid(regions_json)),
 declared_by TEXT NOT NULL CHECK(declared_by IN ('application')),
 recorded_at_ms INTEGER NOT NULL,
 expires_at_ms INTEGER NOT NULL CHECK(expires_at_ms > recorded_at_ms),
 PRIMARY KEY(project_id, source_key, tool_name)
);
