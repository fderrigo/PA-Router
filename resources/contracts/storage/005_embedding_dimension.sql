-- Run in a migration transaction after 004; insert migration checksum then commit.
-- Il limite di 2048 componenti era scritto in tre posti diversi - il provider, una costante che
-- nessuno usava e questo vincolo - e nessuno dei tre era configurabile. Quanto costa un vettore
-- lungo dipende dalla macchina su cui il router gira, non dal prodotto: una macchina con piu'
-- memoria puo' permettersi modelli piu' capaci, e il limite di chi sviluppa non deve decidere per
-- lei.
--
-- Qui resta il tetto **strutturale**, cioe' cio' che lo store accetta di rappresentare. Quanto la
-- singola installazione ammette lo dichiara `memory.maxEmbeddingDimension` nella configurazione, e
-- lo verifica la validazione semantica: un servizio di embedding oltre quel limite viene rifiutato
-- alla lettura della configurazione, non alla prima scrittura.
--
-- SQLite non sa modificare un CHECK: la tabella si ricostruisce e i dati si ricopiano. Le righe
-- esistenti passano tali e quali, perche' nessuna di esse puo' violare un limite che si allarga.
CREATE TABLE embeddings_new (
 chunk_id TEXT NOT NULL REFERENCES memory_chunks(id) ON DELETE RESTRICT,
 model_fingerprint TEXT NOT NULL, dimension INTEGER NOT NULL CHECK(dimension BETWEEN 1 AND 4096),
 vector_blob_id TEXT NOT NULL REFERENCES content_blobs(id) ON DELETE RESTRICT,
 state TEXT NOT NULL CHECK(state IN ('ready','superseded')),
 PRIMARY KEY(chunk_id,model_fingerprint)
);
INSERT INTO embeddings_new(chunk_id,model_fingerprint,dimension,vector_blob_id,state)
 SELECT chunk_id,model_fingerprint,dimension,vector_blob_id,state FROM embeddings;
DROP TABLE embeddings;
ALTER TABLE embeddings_new RENAME TO embeddings;
