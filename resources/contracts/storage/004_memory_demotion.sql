-- Run in a migration transaction after 003; insert migration checksum then commit.
-- La destinazione di un ricordo non sale mai, ma puo' **scendere**: un ricordo instradato alla
-- memoria potenziata il cui testo, riletto al momento dell'embedding, risulta di classe piu' alta
-- torna all'indice locale. Vietare anche la discesa lo lascerebbe senza indice per sempre.
DROP TRIGGER observation_destination_immutable;
CREATE TRIGGER observation_destination_no_promotion BEFORE UPDATE OF index_destination ON observations
 WHEN NEW.index_destination != OLD.index_destination
  AND NOT (OLD.index_destination = 'enhanced' AND NEW.index_destination = 'local')
 BEGIN SELECT RAISE(ABORT,'index_destination may only fall back to local'); END;
