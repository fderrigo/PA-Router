-- Run in a migration transaction after 002; insert migration checksum then commit.
-- Instradamento della memoria per classe del dato: 'local' e' la destinazione predefinita, e lo
-- resta per tutto cio' che gia' esiste. 'enhanced' e' il servizio che puo' stare fuori dalla
-- macchina, e la classe che lo ammette e' una sola.
ALTER TABLE observations ADD COLUMN index_destination TEXT NOT NULL DEFAULT 'local';
CREATE TRIGGER observation_destination_valid BEFORE INSERT ON observations
 WHEN NEW.index_destination NOT IN ('local','enhanced')
 BEGIN SELECT RAISE(ABORT,'index_destination invalid'); END;
CREATE TRIGGER observation_destination_public_only BEFORE INSERT ON observations
 WHEN NEW.index_destination = 'enhanced' AND NEW.classification != 'public'
 BEGIN SELECT RAISE(ABORT,'only public may leave'); END;
CREATE TRIGGER observation_destination_immutable BEFORE UPDATE OF index_destination ON observations
 WHEN NEW.index_destination != OLD.index_destination
 BEGIN SELECT RAISE(ABORT,'index_destination immutable'); END;
CREATE INDEX ix_memory_destination ON observations(project_id,index_destination,deleted_at_ms);
